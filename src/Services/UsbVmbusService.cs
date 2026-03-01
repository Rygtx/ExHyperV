using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using ExHyperV.Tools;
using ExHyperV.Models;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;

namespace ExHyperV.Services
{
    public class UsbVmbusService
    {
        // 全局活动连接记录表 (BusId -> VMName)
        public static ConcurrentDictionary<string, string> ActiveTunnels { get; } = new ConcurrentDictionary<string, string>();

        // 运行中的任务控制
        private static readonly ConcurrentDictionary<string, CancellationTokenSource> _activeCts = new();

        [DllImport("ws2_32.dll", SetLastError = true)]
        private static extern IntPtr socket(int af, int type, int protocol);

        private const int AF_HYPERV = 34;
        private const int SOCK_STREAM = 1;
        private const int HV_PROTOCOL_RAW = 1;
        private static readonly Guid ServiceId = Guid.Parse("45784879-7065-7256-5553-4250726F7879");

        private const int ProxyBufSize = 512 * 1024;

        public UsbVmbusService()
        {
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.RealTime; } catch { }
        }

        private void Log(string msg) => Debug.WriteLine($"[ExHyperV-USB] [{DateTime.Now:HH:mm:ss.fff}] {msg}");

        // 停止隧道任务
        public async Task StopTunnelAsync(string busId)
        {
            // 1. 取消信号并移除记录
            if (_activeCts.TryRemove(busId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
                Log($"[StopTunnel] 正在停止旧隧道: {busId}");
            }

            // 2. 显式等待 unbind 完成，确保宿主机重新获得控制权
            Log($"[StopTunnel] 强制执行 unbind: {busId}");
            await RunUsbIpCommand($"unbind --busid {busId}");

            // 给系统一点反应时间（usbipd 驱动释放需要时间）
            await Task.Delay(500);
        }

        // 自动恢复隧道 (由 Watchdog 调用)
        public async Task AutoRecoverTunnel(string busId, string vmName)
        {
            // 如果已经有任务在跑，需要判断目标是否一致（或者为了保险，切换时直接重建）
            if (_activeCts.ContainsKey(busId))
            {
                // 如果你的逻辑允许直接切换，这里需要先调用 Stop
                await StopTunnelAsync(busId);
            }

            var cts = new CancellationTokenSource();
            if (!_activeCts.TryAdd(busId, cts)) return;

            try
            {
                // 关键：在 bind 之前确保环境干净
                await RunUsbIpCommand($"unbind --busid {busId}");

                bool bound = await RunUsbIpCommand($"bind --busid {busId}");
                if (!bound) bound = await RunUsbIpCommand($"bind --busid {busId} --force");

                if (!bound) throw new Exception("usbipd bind failed");

                var vms = await GetRunningVMsAsync();
                var targetVm = vms.FirstOrDefault(v => v.Name == vmName);
                if (targetVm == null) throw new Exception($"VM {vmName} not found or not running");

                await StartTunnelAsync(targetVm.Id, busId, cts.Token);
            }
            catch (Exception ex)
            {
                Log($"[AutoRecover] 隧道建立失败: {ex.Message}");
                _activeCts.TryRemove(busId, out _);
            }
        }

        public async Task StartTunnelAsync(Guid vmId, string busId, CancellationToken ct)
        {
            // 保持你的 P/Invoke Socket 创建方式
            IntPtr handle = socket(AF_HYPERV, SOCK_STREAM, HV_PROTOCOL_RAW);
            if (handle == (IntPtr)(-1)) throw new SocketException(Marshal.GetLastWin32Error());

            var safeHandle = new System.Net.Sockets.SafeSocketHandle(handle, true);
            Socket hv = new Socket(safeHandle);

            // 这是一个信号，用于监控 Pump 线程是否结束
            var completion = new TaskCompletionSource<bool>();
            ct.Register(() => {
                try { hv.Close(); } catch { }
                completion.TrySetResult(true);
            });

            try
            {
                Log($"Tunnel: Connecting VMBus {vmId}...");
                // 核心修复：在 P/Invoke 句柄上必须使用同步 Connect 配合 Task.Run，否则会报“无效参数”
                await Task.Run(() => hv.Connect(new HyperVEndPoint(vmId, ServiceId)), ct);

                hv.Blocking = true;
                hv.Send(Encoding.ASCII.GetBytes(busId + "\0"));

                Socket tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                tcp.NoDelay = true;

                Log("Tunnel: Connecting Local TCP 3240...");
                bool tcpOk = false;
                for (int i = 0; i < 5; i++)
                {
                    try { await tcp.ConnectAsync("127.0.0.1", 3240, ct); tcpOk = true; break; }
                    catch { await Task.Delay(500, ct); }
                }
                if (!tcpOk) throw new Exception("usbipd service unreachable");

                // 启动泵线程
                StartHighPriorityPump(hv, tcp, "VMBUS_TO_TCP", () => completion.TrySetResult(false), ct);
                StartHighPriorityPump(tcp, hv, "TCP_TO_VMBUS", () => completion.TrySetResult(false), ct);

                Log("Tunnel: Established.");
                // 关键：此处必须阻塞，直到任务被取消或 Pump 线程报错断开
                await completion.Task;
            }
            finally
            {
                try { hv.Dispose(); } catch { }
            }
        }

        private void StartHighPriorityPump(Socket sIn, Socket sOut, string label, Action onFault, CancellationToken ct)
        {
            new Thread(() =>
            {
                Thread.CurrentThread.Priority = ThreadPriority.Highest;
                byte[] buffer = GC.AllocateArray<byte>(ProxyBufSize, pinned: true);
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        int n = sIn.Receive(buffer, 0, buffer.Length, SocketFlags.None);
                        if (n <= 0) break;

                        int sent = 0;
                        while (sent < n)
                        {
                            int count = sOut.Send(buffer, sent, n - sent, SocketFlags.None);
                            if (count <= 0) break;
                            sent += count;
                        }
                    }
                }
                catch { }
                finally { onFault?.Invoke(); }
            })
            { IsBackground = true, Name = $"Pump_{label}" }.Start();
        }

        public async Task WatchdogLoopAsync(CancellationToken globalCt)
        {
            while (!globalCt.IsCancellationRequested)
            {
                foreach (var entry in ActiveTunnels)
                {
                    if (!_activeCts.ContainsKey(entry.Key))
                    {
                        _ = Task.Run(() => AutoRecoverTunnel(entry.Key, entry.Value));
                    }
                }
                await Task.Delay(2000, globalCt);
            }
        }

        public async Task<bool> EnsureDeviceSharedAsync(string busId)
        {
            return await RunUsbIpCommand($"bind --busid {busId}");
        }

        private async Task<bool> RunUsbIpCommand(string args)
        {
            try
            {
                var psi = new ProcessStartInfo("usbipd", args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                var proc = Process.Start(psi);
                if (proc != null) { await proc.WaitForExitAsync(); return proc.ExitCode == 0; }
                return false;
            }
            catch { return false; }
        }

        public async Task<List<VmInfo>> GetRunningVMsAsync()
        {
            var list = new List<VmInfo>();
            try
            {
                // 直接运行原生命令，不需要拼字符串，直接返回对象
                string script = "Get-VM | Where-Object {$_.State -eq 'Running'} | Select-Object Name, Id";

                // 使用你的 Utils.Run2
                var results = await Utils.Run2(script);

                foreach (var psObj in results)
                {
                    // 直接从属性中提取值，SDK 会自动处理类型转换
                    // Name 是 String，Id 是 Guid (在 PowerShell 中实际上是个对象)
                    var name = psObj.Properties["Name"]?.Value?.ToString();
                    var idValue = psObj.Properties["Id"]?.Value;

                    if (!string.IsNullOrEmpty(name) && idValue != null)
                    {
                        list.Add(new VmInfo
                        {
                            Name = name,
                            // 处理可能出现的不同 ID 类型包装
                            Id = idValue is Guid guid ? guid : Guid.Parse(idValue.ToString())
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ExHyperV-USB] 获取虚拟机列表失败: {ex.Message}");
            }
            return list;
        }

        public async Task<List<UsbDeviceModel>> GetUsbIpDevicesAsync()
        {
            var list = new List<UsbDeviceModel>();
            try
            {
                var psi = new ProcessStartInfo("usbipd", "list") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = System.Text.Encoding.Default };
                using var proc = Process.Start(psi);
                string outStr = await proc.StandardOutput.ReadToEndAsync();
                var lines = outStr.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                bool foundSection = false;
                foreach (var line in lines)
                {
                    if (line.Contains("BUSID")) { foundSection = true; continue; }
                    if (foundSection && line.Trim().Length > 0 && char.IsDigit(line.Trim()[0]))
                    {
                        var m = Regex.Match(line.Trim(), @"^([0-9\-.]+)\s+([0-9a-fA-F:]+)\s+(.*?)\s{2,}");
                        if (m.Success) list.Add(new UsbDeviceModel { BusId = m.Groups[1].Value.Trim(), VidPid = m.Groups[2].Value.Trim(), Description = m.Groups[3].Value.Trim(), Status = "Ready" });
                    }
                }
            }
            catch { }
            return list;
        }

        public void EnsureServiceRegistered()
        {
            try
            {
                string regPath = $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization\GuestCommunicationServices\{ServiceId:B}";
                using var key = Registry.LocalMachine.CreateSubKey(regPath);
                key.SetValue("ElementName", "ExHyperV USB Proxy Infrastructure");
            }
            catch { }
        }
    }
}