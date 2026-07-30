using System.Globalization;
using System.Windows.Data;
using Leash.Models;

namespace Leash.Views.Converters;

/// <summary>Maps the enforcement state to the banner headline.</summary>
public sealed class StatusToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is MonitorStatus status
            ? status switch
            {
                MonitorStatus.Allowed => "ALLOWED",
                MonitorStatus.Blocked => "BLOCKED",
                MonitorStatus.Unknown => "UNKNOWN — BLOCKING (FAIL-CLOSED)",
                _ => "NOT MONITORING",
            }
            : "NOT MONITORING";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
