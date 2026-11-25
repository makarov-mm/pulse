namespace Pulse.Models;

internal readonly record struct DeviceReading(string Key, string Name, double? Value, string Text, string Caption);
