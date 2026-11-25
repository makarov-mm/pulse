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
        UnhandledException += (_, e) => RecordFailure(e.Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => RecordFailure(e.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }

    // Appends an unexpected failure to %LocalAppData%\Pulse\errors.log so it can be reported.
    private static void RecordFailure(Exception error)
    {
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pulse");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "errors.log"), $"{DateTime.Now:O}{Environment.NewLine}{error}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
    }
}
