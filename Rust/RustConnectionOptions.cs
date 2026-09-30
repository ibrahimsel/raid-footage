using System.Globalization;
using RustPlusApi;
using RustCctv.Configuration;

namespace RustCctv.Rust;

// Intentionally not a record: its generated ToString must never expose credentials.
public sealed class RustConnectionOptions
{
    private readonly RustPlusConnection connection;
    private RustConnectionOptions(RustPlusConnection connection) => this.connection = connection;
    internal RustPlusConnection ToConnection() => connection;

    public static RustConnectionOptions FromEnvironment()
    {
        var server = Environment.GetEnvironmentVariable("RUST_SERVER");
        if (string.IsNullOrWhiteSpace(server))
            throw new RecorderConfigurationException("Set RUST_SERVER.");
        if (!int.TryParse(Environment.GetEnvironmentVariable("RUST_PORT"), out var port) || port is < 1 or > 65535)
            throw new RecorderConfigurationException("Set RUST_PORT to a valid companion port.");
        if (!ulong.TryParse(Environment.GetEnvironmentVariable("RUST_PLAYER_ID"), NumberStyles.None,
                CultureInfo.InvariantCulture, out var playerId) || playerId == 0)
            throw new RecorderConfigurationException("Set RUST_PLAYER_ID to a valid Steam ID.");
        if (!int.TryParse(Environment.GetEnvironmentVariable("RUST_PLAYER_TOKEN"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var token))
            throw new RecorderConfigurationException("Set RUST_PLAYER_TOKEN to a signed 32-bit integer.");
        return new(new RustPlusConnection(server, port, playerId, token));
    }
}
