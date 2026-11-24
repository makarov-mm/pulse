using Pulse.Localization;
using System.Globalization;

namespace Pulse.Models;

internal static class Format
{
    private static readonly string[] UnitKeys = ["Bytes", "Kibibytes", "Mebibytes", "Gibibytes", "Tebibytes"];
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
}

