namespace Pulse.Telemetry;

internal sealed record CoreSample(int Group, int Number, double Usage);

// Instance is the PDH name, such as "0 C: D:". Active is the share of time spent servicing
// requests and Response is the average seconds per transfer.
internal sealed record DiskSample(string Instance, int? Index, string Volumes, double? Read, double? Write,
    double? Active, double? Response);

// LinkSpeed is in bits per second.
internal sealed record AdapterSample(string Id, string Name, double? Received, double? Sent, long? LinkSpeed);
