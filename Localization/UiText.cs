using System.Globalization;
using System.Resources;

namespace Pulse.Localization;

internal static class UiText
{
    private static readonly ResourceManager Resources = new("Pulse.Localization.Strings", typeof(UiText).Assembly);
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase) { "en", "de", "ru", "uk", "es", "fr", "zh-Hans" };
    public static CultureInfo Language { get; private set; } = CultureInfo.GetCultureInfo("en");

    public static void UsePreferredLanguages(IEnumerable<string> preferences)
    {
        Language = SelectLanguage(preferences);
        CultureInfo.DefaultThreadCurrentUICulture = Language;
        CultureInfo.CurrentUICulture = Language;
    }

    private static CultureInfo SelectLanguage(IEnumerable<string> preferences)
    {
        foreach (var tag in preferences)
        {
            CultureInfo culture;
            try { culture = CultureInfo.GetCultureInfo(tag); }
            catch (CultureNotFoundException) { continue; }

            if (culture.Name == "zh") return CultureInfo.GetCultureInfo("zh-Hans");
            for (; culture.Name.Length != 0; culture = culture.Parent)
                if (Supported.Contains(culture.Name)) return culture;
        }
        return CultureInfo.GetCultureInfo("en");
    }

    public static string Get(string key) => Resources.GetString(key, Language)
        ?? throw new MissingManifestResourceException($"Missing UI string '{key}'.");

    public static string Format(string key, params object?[] values) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), values);
}
