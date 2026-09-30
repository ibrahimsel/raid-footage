namespace RustCctv.Configuration;

// Only messages authored by this application belong here; never include supplied values.
public sealed class RecorderConfigurationException(string message) : Exception(message);
