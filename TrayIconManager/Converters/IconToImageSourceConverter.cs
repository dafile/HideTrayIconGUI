using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace TrayIconManager.Converters;

public class IconToImageSourceConverter : IValueConverter
{
    public static readonly IconToImageSourceConverter Instance = new();

    public object? Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Icon icon)
        {
            try
            {
                using var ms = new MemoryStream();
                icon.Save(ms);
                ms.Position = 0;
                var bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.StreamSource = ms;
                bitmapImage.EndInit();
                bitmapImage.Freeze();
                return bitmapImage;
            }
            catch
            {
                // Return null if icon conversion fails
            }
        }
        return null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}
