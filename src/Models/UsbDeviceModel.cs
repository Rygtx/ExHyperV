using CommunityToolkit.Mvvm.ComponentModel;

namespace ExHyperV.Models
{
    // 虚拟机基础信息
    public class VmInfo
    {
        public string Name { get; set; }
        public Guid Id { get; set; }
    }

    // USB 设备原始数据模型
    public partial class UsbDeviceModel
    {
        public string BusId { get; set; }
        public string VidPid { get; set; }
        public string Description { get; set; }
        public string Status { get; set; }
    }

    // USB 设备视图模型
    public partial class UsbDeviceViewModel : ObservableObject
    {
        public string BusId { get; }
        public string VidPid { get; }
        public string Description { get; }
        public string Status { get; }

        // 当前分配目标 (如: "主机" 或 虚拟机名称)
        [ObservableProperty]
        private string _currentAssignment;

        // 分配选项列表
        public List<string> AssignmentOptions { get; }

        public UsbDeviceViewModel(UsbDeviceModel model, List<string> runningVmNames)
        {
            BusId = model.BusId;
            VidPid = model.VidPid;
            Description = model.Description;
            Status = model.Status;

            _currentAssignment = "主机";

            // 初始化选项列表
            AssignmentOptions = new List<string> { "主机" };
            AssignmentOptions.AddRange(runningVmNames);
        }
    }
}