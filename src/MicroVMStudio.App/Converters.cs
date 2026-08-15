using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using MicroVMStudio.Core.Models;

namespace MicroVMStudio.App;

public static class Converters
{
    /// <summary>
    /// Colours the status pill. Deliberately three-way rather than binary:
    /// "moving" states need to read differently from "broken" ones, because the
    /// user's next action differs — wait, versus go look at the logs.
    /// </summary>
    public static readonly IValueConverter StatusBrush =
        new FuncValueConverter<VmStatus, IBrush>(status => status switch
        {
            VmStatus.Running => new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43)),
            VmStatus.Provisioning or VmStatus.Booting or VmStatus.Stopping
                => new SolidColorBrush(Color.FromRgb(0xBF, 0x8B, 0x00)),
            VmStatus.Crashed or VmStatus.CrashLoopBackoff or VmStatus.BootFailed
                => new SolidColorBrush(Color.FromRgb(0xCF, 0x22, 0x2E)),
            _ => new SolidColorBrush(Color.FromRgb(0x6E, 0x77, 0x81)),
        });

    public static readonly IValueConverter PreflightBrush =
        new FuncValueConverter<bool, IBrush>(ok => ok
            ? new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43))
            : new SolidColorBrush(Color.FromRgb(0xCF, 0x22, 0x2E)));

    public static readonly IValueConverter PreflightGlyph =
        new FuncValueConverter<bool, string>(ok => ok ? "✓" : "✗");

    public static readonly IValueConverter IsNotNull =
        new FuncValueConverter<object?, bool>(value => value is not null);

    public static readonly IValueConverter IsNullOrEmpty =
        new FuncValueConverter<string?, bool>(string.IsNullOrEmpty);

    public static readonly IValueConverter IsNotNullOrEmpty =
        new FuncValueConverter<string?, bool>(s => !string.IsNullOrEmpty(s));
}

/// <summary>Enum values XAML can reference directly; ComboBox needs concrete items.</summary>
public static class EgressEffects
{
    public static EgressEffect Allow => EgressEffect.Allow;
    public static EgressEffect Deny => EgressEffect.Deny;
}

/// <summary>Highlights the left-nav entry matching the current section.</summary>
public sealed class SectionActiveConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string current && parameter is string section && current == section;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
