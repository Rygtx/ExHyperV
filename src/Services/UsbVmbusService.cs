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
        // 全局活动连接记录表
        public static ConcurrentDictionary<string, string> ActiveTunnels { get; } = new ConcurrentDictionary<string, string>();

        [DllImport("ws2_32.dll", SetLastError = true)]
        private static extern IntPtr socket(int af, int type, int protocol);

        private const int AF_HYPERV = 34;
        private const int SOCK_STREAM = 1;
        private const int HV_PROTOCOL_RAW = 1;
        private static readonly Guid ServiceId = Guid.Parse("45784879-7065-7256-5553-4250726F7879");

        // 代理缓冲区大小
        private const int ProxyBufSize = 512 * 1024;
        // VMBus 单次写入块限制
        private const int MaxVmbusWriteChunk = 32 * 1024;

        public long TotalRx;
        public long TotalTx;

        public UsbVmbusService()
        {
            try
            {
                // 设置进程为实时优先级以减少延迟
                Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.RealTime;
            }
            catch { }
        }

        private void Log(string msg) => Debug.WriteLine($"[ExHyperV-USB] [{DateTime.Now:HH:mm:ss.fff}] {msg}");

        public async Task StartTunnelAsync(Guid vmId, string busId, CancellationToken ct)
        {
            IntPtr handle = socket(AF_HYPERV, SOCK_STREAM, HV_PROTOCOL_RAW);
            if (handle == (IntPtr)(-1)) throw new SocketException(Marshal.GetLastWin32Error());

            var safeHandle = new System.Net.Sockets.SafeSocketHandle(handle, true);
            Socket hv = new Socket(safeHandle);

            // 注册取消回调，确保在取消时关闭 Socket
            ct.Register(() => { try { hv.Close(); } catch { } });

            try
            {
                Log($"Tunnel: Connecting VMBus {vmId}...");
                await Task.Run(() => hv.Connect(new HyperVEndPoint(vmId, ServiceId)), ct);

                hv.Blocking = true;
                hv.Send(Encoding.ASCII.GetBytes(busId + "\0"));

                Socket tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                tcp.NoDelay = true;
                tcp.Blocking = true;

                Log("Tunnel: Connecting Local TCP 3240...");
                await tcp.ConnectAsync("127.0.0.1", 3240, ct);

                TotalRx = 0; TotalTx = 0;

                // 启动双向转发线程
                StartHighPriorityPump(hv, tcp, "VMBUS_TO_TCP", ct);
                StartHighPriorityPump(tcp, hv, "TCP_TO_VMBUS", ct);

                Log("Tunnel: Established. Returning to UI...");
            }
            catch (Exception ex)
            {
                Log($"Tunnel ERROR: {ex.Message}");
                hv.Dispose();
                throw;
            }
        }

        private void StartHighPriorityPump(Socket sIn, Socket sOut, string label, CancellationToken ct)
        {
            new Thread(() =>
            {
                Thread.CurrentThread.Priority = ThreadPriority.Highest;
                byte[] buffer = GC.AllocateArray<byte>(ProxyBufSize, pinned: true);
                bool isToVm = label.EndsWith("VMBUS");

                Log($"Pump [{label}]: Thread start.");

                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        int n = sIn.Receive(buffer, 0, buffer.Length, SocketFlags.None);
                        if (n <= 0)
                        {
                            Log($"Pump [{label}]: Received 0 (Peer Closed).");
                            break;
                        }

                        if (isToVm) Interlocked.Add(ref TotalTx, n);
                        else Interlocked.Add(ref TotalRx, n);

                        int sent = 0;
                        while (sent < n)
                        {
                            int count = sOut.Send(buffer, sent, n - sent, SocketFlags.None);
                            if (count <= 0) throw new SocketException((int)SocketError.ConnectionAborted);
                            sent += count;
                        }
                    }
                }
                catch (SocketException ex)
                {
                    if (!ct.IsCancellationRequested) Log($"Pump [{label}]: Socket Error {ex.SocketErrorCode}");
                }
                catch (Exception ex)
                {
                    Log($"Pump [{label}]: Fatal {ex.Message}");
                }
                finally
                {
                    Log($"Pump [{label}]: Closing sockets.");
                    try { sIn.Close(); sOut.Close(); } catch { }
                }
            })
            { IsBackground = true, Name = $"Pump_{label}" }.Start();
        }

        // 注册 GuestCommunicationService 注册表项
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

        // 调用 usbipd 绑定设备
        public async Task<bool> EnsureDeviceSharedAsync(string busId)
        {
            try
            {
                var psi = new ProcessStartInfo("usbipd", $"bind --busid {busId}") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                var proc = Process.Start(psi);
                if (proc != null) { await proc.WaitForExitAsync(); return proc.ExitCode == 0; }
                return false;
            }
            catch { return false; }
        }

        // 获取正在运行的虚拟机列表
        public async Task<List<VmInfo>> GetRunningVMsAsync()
        {
            var list = new List<VmInfo>();
            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -Command \"Get-VM | Where-Object {$_.State -eq 'Running'} | ForEach-Object { $_.Name + '|' + $_.Id.Guid }\"") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = System.Text.Encoding.Default };
                using var proc = Process.Start(psi);
                string outStr = await proc.StandardOutput.ReadToEndAsync();
                foreach (var line in outStr.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var p = line.Trim().Split('|');
                    if (p.Length == 2) list.Add(new VmInfo { Name = p[0], Id = Guid.Parse(p[1]) });
                }
            }
            catch { }
            return list;
        }

        // 获取 usbipd 设备列表
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
    }
}