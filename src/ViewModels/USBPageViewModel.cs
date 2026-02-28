using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Services;

namespace ExHyperV.ViewModels
{
    public partial class USBPageViewModel : ObservableObject
    {
        private readonly UsbVmbusService _srv;
        private CancellationTokenSource _cts;

        [ObservableProperty] private bool _isLoading;
        [ObservableProperty] private bool _isUiEnabled = true;

        public ObservableCollection<UsbDeviceViewModel> Devices { get; }
        public IAsyncRelayCommand LoadDataCommand { get; }
        public IAsyncRelayCommand<object> ChangeAssignmentCommand { get; }

        public USBPageViewModel()
        {
            _srv = new UsbVmbusService();
            Devices = new ObservableCollection<UsbDeviceViewModel>();
            LoadDataCommand = new AsyncRelayCommand(LoadDataAsync);
            ChangeAssignmentCommand = new AsyncRelayCommand<object>(ChangeAssignmentAsync);
            LoadDataCommand.Execute(null);
        }

        private async Task LoadDataAsync()
        {
            IsUiEnabled = false;
            IsLoading = true;
            try
            {
                _srv.EnsureServiceRegistered();

                var vmsTask = _srv.GetRunningVMsAsync();
                var usbTask = _srv.GetUsbIpDevicesAsync();
                await Task.WhenAll(vmsTask, usbTask);

                var vmNames = vmsTask.Result.Select(v => v.Name).ToList();
                var usbDevices = usbTask.Result;

                Devices.Clear();
                foreach (var device in usbDevices)
                {
                    var deviceVM = new UsbDeviceViewModel(device, vmNames);

                    // 从活动记录中恢复连接状态
                    if (UsbVmbusService.ActiveTunnels.TryGetValue(device.BusId, out string connectedVmName))
                    {
                        deviceVM.CurrentAssignment = connectedVmName;
                    }

                    Devices.Add(deviceVM);
                }
            }
            finally
            {
                IsLoading = false;
                IsUiEnabled = true;
            }
        }

        private async Task ChangeAssignmentAsync(object parameter)
        {
            if (parameter is not object[] parameters || parameters.Length < 2 ||
                parameters[0] is not UsbDeviceViewModel deviceVM ||
                parameters[1] is not string selectedTarget) return;

            if (deviceVM.CurrentAssignment == selectedTarget) return;

            IsUiEnabled = false;
            try
            {
                if (selectedTarget == "主机")
                {
                    _cts?.Cancel();
                    deviceVM.CurrentAssignment = "主机";

                    // 从活动记录中移除
                    UsbVmbusService.ActiveTunnels.TryRemove(deviceVM.BusId, out _);
                }
                else
                {
                    var vms = await _srv.GetRunningVMsAsync();
                    var targetVm = vms.FirstOrDefault(v => v.Name == selectedTarget);

                    if (targetVm != null)
                    {
                        deviceVM.CurrentAssignment = "正在连接...";

                        bool shared = await _srv.EnsureDeviceSharedAsync(deviceVM.BusId);
                        if (!shared) throw new Exception("usbipd bind failed");

                        _cts?.Cancel();
                        _cts = new CancellationTokenSource();

                        try
                        {
                            await _srv.StartTunnelAsync(targetVm.Id, deviceVM.BusId, _cts.Token);

                            deviceVM.CurrentAssignment = selectedTarget;

                            // 更新活动记录
                            UsbVmbusService.ActiveTunnels[deviceVM.BusId] = selectedTarget;
                        }
                        catch (Exception)
                        {
                            deviceVM.CurrentAssignment = "连接失败";
                            _cts?.Cancel();

                            // 失败时清理记录
                            UsbVmbusService.ActiveTunnels.TryRemove(deviceVM.BusId, out _);
                        }
                    }
                }
            }
            catch (Exception)
            {
                deviceVM.CurrentAssignment = "错误";
            }
            finally
            {
                IsUiEnabled = true;
            }
        }
    }
}