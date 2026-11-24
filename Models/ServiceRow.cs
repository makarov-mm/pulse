using Pulse.Localization;
using Pulse.Telemetry;

namespace Pulse.Models;

public sealed class ServiceRow : ObservableRow
{
    private string _name = "", _description = "", _status = "", _pid = "", _process = "";
    private double _emphasis = 1;

    internal int ProcessId { get; private set; }

    public string Name => _name;
    public string Description => _description;
    public string Status => _status;
    public string Pid => _pid;
    public string Process => _process;
    // Stopped services are dimmed so the running ones stand out.
    public double Emphasis => _emphasis;

    internal void Update(ServiceInfo service, string process)
    {
        ProcessId = service.Pid;
        Set(ref _name, service.Name, nameof(Name));
        Set(ref _description, service.DisplayName, nameof(Description));
        Set(ref _status, UiText.Get(service.State switch
        {
            ServiceState.Running => "ServiceRunning",
            ServiceState.Stopped => "ServiceStopped",
            ServiceState.StartPending or ServiceState.ContinuePending => "ServiceStarting",
            ServiceState.StopPending => "ServiceStopping",
            _ => "ServicePaused"
        }), nameof(Status));
        Set(ref _pid, service.Pid == 0 ? "" : service.Pid.ToString(), nameof(Pid));
        Set(ref _process, process, nameof(Process));
        Set(ref _emphasis, service.State == ServiceState.Stopped ? 0.6 : 1, nameof(Emphasis));
    }
}
