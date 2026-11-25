namespace Pulse.Telemetry;

internal sealed record CoreSample(int Group, int Number, double? Usage);
internal sealed record DiskSample(string Instance, int? Index, string Volumes, double? Read, double? Write, double? Active, double? Response)
{
    public bool IsVolume { get; init; }
}
internal sealed record AdapterSample(string Id, string Name, double? Received, double? Sent, long? LinkSpeed);
