namespace Pulse.Models;

// One device's values for a single sample, formatted for a device tile.
internal readonly record struct DeviceReading(string Key, string Name, double? Value, string Text, string Caption);
