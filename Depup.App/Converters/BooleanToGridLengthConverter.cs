using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Depup.App.Converters;

public sealed class BooleanToGridLengthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? new GridLength(72) : new GridLength(240);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
