using Microsoft.UI.Xaml.Markup;

namespace Pulse.Localization;

[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed class LocalizeExtension : MarkupExtension
{
    public string Key { get; set; } = "";
    protected override object ProvideValue() => UiText.Get(Key);
}
