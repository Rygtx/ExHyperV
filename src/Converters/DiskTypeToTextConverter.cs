using System.Globalization;
using System.Windows.Data;

namespace ExHyperV.Converters
{
    public class DiskTypeToTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string type)
            {
                return type.ToUpper() switch
                {
                    "DYNAMIC" => "动态磁盘",
                    "FIXED" => "固定磁盘",
                    "DIFFERENCING" => "差异磁盘",
                    "ISO" => "光盘镜像",
                    "PHYSICAL" => "物理磁盘",
                    "DVDDRIVE" => "物理光驱",
                    _ => type // 如果没匹配上，返回原样
                };
            }
            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}