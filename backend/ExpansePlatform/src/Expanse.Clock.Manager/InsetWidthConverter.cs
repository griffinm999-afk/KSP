using System.Globalization;
using System.Windows.Data;

namespace Expanse.Clock.Manager;

public sealed class InsetWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double inset = double.TryParse(parameter?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : 40;
        return value is double width && double.IsFinite(width) ? Math.Max(0, width - inset) : 0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
