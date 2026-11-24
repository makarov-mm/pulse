using Pulse.Localization;
using Pulse.Telemetry;

namespace Pulse.Models;

public sealed class ServiceRow : ObservableRow
{
    private string _name = "", _title = "", _state = "", _pid = "", _host = "";

    internal ServiceRow() { }
    internal int ProcessId { get; private set; }
    internal ServiceInfo? Info { get; private set; }

    public string Name => _name;
    public string Title => _title;
    public string State => _state;
    public string Pid => _pid;
    public string Host => _host;

    internal void Update(ServiceInfo service, string process)
    {
        ProcessId = service.Pid;
        Info = service;
        Set(ref _name, service.Name, nameof(Name));
        Set(ref _title, service.DisplayName, nameof(Title));
        Set(ref _state, StateLabel(service.State), nameof(State));
        Set(ref _pid, service.Pid == 0 ? "" : service.Pid.ToString(), nameof(Pid));
        Set(ref _host, process, nameof(Host));
    }

    internal static string StateLabel(ServiceState state) => UiText.Get(state switch
    {
        ServiceState.Running => "ServiceRunning",
        ServiceState.Stopped => "ServiceStopped",
        ServiceState.StartPending or ServiceState.ContinuePending => "ServiceStarting",
        ServiceState.StopPending => "ServiceStopping",
        _ => "ServicePaused"
    });
}
