using Pulse.Localization;
using Pulse.Telemetry;

namespace Pulse.Models;

public sealed class StartupRow : ObservableRow
{
    private string _name = "", _command = "", _source = "", _state = "", _host = "";

    internal StartupRow() { }
    internal StartupItem? Item { get; private set; }

    public string Name => _name;
    public string Command => _command;
    public string Source => _source;
    public string State => _state;
    // The running process started from this entry, if any.
    public string Host => _host;

    internal void Update(StartupItem item, string process)
    {
        Item = item;
        Set(ref _name, item.Name, nameof(Name));
        Set(ref _command, item.Command, nameof(Command));
        Set(ref _source, item.Source switch
        {
            StartupSource.UserRun => @"HKCU\…\Run",
            StartupSource.MachineRun => @"HKLM\…\Run",
            StartupSource.MachineRun32 => @"HKLM\…\WOW6432Node\…\Run",
            StartupSource.UserRunOnce => @"HKCU\…\RunOnce",
            StartupSource.MachineRunOnce => @"HKLM\…\RunOnce",
            StartupSource.UserFolder => UiText.Get("StartupUserFolder"),
            _ => UiText.Get("StartupCommonFolder")
        }, nameof(Source));
        Set(ref _state, UiText.Get(item.Enabled ? "StartupEnabled" : "StartupDisabled"), nameof(State));
        Set(ref _host, process, nameof(Host));
    }
}
