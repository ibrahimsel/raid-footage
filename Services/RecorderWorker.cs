using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RustCctv.Configuration;
using RustCctv.Recording;
using RustCctv.Rust;

namespace RustCctv.Services;

public sealed record RunMode(bool Snapshot);

public sealed class RecorderWorker(RecorderOptions options, RustConnectionOptions connection,
    CameraRecorder recorder, RunMode mode, IHostApplicationLifetime lifetime, ILogger<RecorderWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var attempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var session = Stopwatch.StartNew();
            try
            {
                await using var camera = new RustCameraClient(connection, options);
                logger.LogInformation("Connecting to Rust+ and subscribing to the configured camera.");
                await camera.ConnectAsync(stoppingToken);
                logger.LogInformation("Camera is receiving rays; managed subscription renewal is active.");
                if (mode.Snapshot)
                {
                    await Task.Delay(TimeSpan.FromSeconds(options.SnapshotSeconds), stoppingToken);
                    await File.WriteAllBytesAsync("snapshot.png", camera.Render(), stoppingToken);
                    logger.LogInformation("Saved snapshot.png.");
                    lifetime.StopApplication();
                    return;
                }
                await recorder.RecordAsync(camera, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Library exception messages/server error text can contain credentials. Log only owned reasons or types.
                var reason = ex is CameraSessionException ? ex.Message : ex.GetType().Name;
                if (stoppingToken.IsCancellationRequested)
                {
                    logger.LogError("Shutdown failed: {Reason}. Inspect the current recording.", reason);
                    Environment.ExitCode = 1;
                    break;
                }
                if (mode.Snapshot)
                {
                    logger.LogError("Snapshot failed: {Reason}.", reason);
                    Environment.ExitCode = 1;
                    lifetime.StopApplication();
                    return;
                }
                if (session.Elapsed >= TimeSpan.FromMinutes(2)) attempt = 0;
                var seconds = Math.Min(options.ReconnectMaxSeconds, Math.Pow(2, Math.Min(attempt++, 8)));
                var delay = TimeSpan.FromSeconds(seconds * (0.8 + Random.Shared.NextDouble() * 0.2));
                logger.LogWarning("Recording session ended: {Reason} Retrying in {Seconds:F1}s.", reason, delay.TotalSeconds);
                try { await Task.Delay(delay, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
        logger.LogInformation("Recorder stopped.");
    }
}
