using Pulse.Localization;
using System.Globalization;

namespace Pulse.Models;

internal static class Format
{
    private static readonly string[] UnitKeys = ["Bytes", "Kibibytes", "Mebibytes", "Gibibytes", "Tebibytes"];
    private static readonly string[] BitRateKeys = ["BitsPerSecond", "KilobitsPerSecond", "MegabitsPerSecond", "GigabitsPerSecond"];
    public static string Number(double value, int digits = 0) => value.ToString($"N{digits}", CultureInfo.CurrentCulture);
    public static string Percent(double? value) => value is { } v ? $"{Number(v, 1)} %" : "—";

    public static (string Value, string Unit) Size(double? bytes, bool rate = false)
    {
        if (bytes is null) return ("—", "");
        double value = bytes.Value;
        int i = 0;
        while (value >= 1024 && i < UnitKeys.Length - 1) { value /= 1024; i++; }
        string unit = UiText.Get(UnitKeys[i]);
        return (Number(value, i != 0 && value < 100 ? 1 : 0), rate ? UiText.Format("RateUnit", unit) : unit);
    }

    public static string Bytes(double? bytes, bool rate = false)
    {
        var (value, unit) = Size(bytes, rate);
        return unit.Length > 0 ? $"{value} {unit}" : value;
    }

    // Names the Windows priority class of a process.
    public static string Priority(uint? priorityClass) => priorityClass switch
    {
        0x40 => UiText.Get("PriorityLow"),
        0x4000 => UiText.Get("PriorityBelowNormal"),
        0x20 => UiText.Get("PriorityNormal"),
        0x8000 => UiText.Get("PriorityAboveNormal"),
        0x80 => UiText.Get("PriorityHigh"),
        0x100 => UiText.Get("PriorityRealtime"),
        _ => "—"
    };

    public static string Milliseconds(double? seconds) => seconds is { } s
        ? $"{Number(s * 1000, s < 0.01 ? 1 : 0)} {UiText.Get("Milliseconds")}" : "—";

    // Link speeds use decimal prefixes, unlike byte sizes.
    public static string BitRate(long? bitsPerSecond)
    {
        if (bitsPerSecond is null) return "—";
        double value = bitsPerSecond.Value;
        int i = 0;
        while (value >= 1000 && i < BitRateKeys.Length - 1) { value /= 1000; i++; }
        return $"{Number(value, i != 0 && value < 10 && value % 1 != 0 ? 1 : 0)} {UiText.Get(BitRateKeys[i])}";
    }
}
