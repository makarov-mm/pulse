using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Pulse.Localization;
using Windows.System.UserProfile;

namespace Pulse;

public partial class App : Application
{
    private Window? _window;
    public App()
    {
        try { UiText.UsePreferredLanguages(GlobalizationPreferences.Languages); }
        catch (COMException) { UiText.UsePreferredLanguages(new[] { CultureInfo.CurrentUICulture.Name }); }
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}

