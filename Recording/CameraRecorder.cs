using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RustCctv.Configuration;
using RustCctv.Rust;

namespace RustCctv.Recording;

public sealed class CameraRecorder(RecorderOptions options, RecordingPathFactory paths, ILoggerFactory loggers)
{
    public async Task RecordAsync(RustCameraClient camera, CancellationToken token)
    {
        var first = camera.Render();
        await using var encoder = new FfmpegEncoder(options, paths, loggers.CreateLogger<FfmpegEncoder>());
        var clock = Stopwatch.StartNew();
        long count = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var due = TimeSpan.FromSeconds(count / (double)options.Fps);
            var delay = due - clock.Elapsed;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
            if (clock.Elapsed - due > TimeSpan.FromSeconds(options.MaxLagSeconds))
                throw new CameraSessionException("Encoding fell behind real time; reduce FPS or check storage.");
            // Every output tick samples the latest reconstruction, independent of packet arrival rate.
            // Absolute deadlines avoid accumulating render/write time as pacing drift. No frame queue.
            camera.EnsureHealthy();
            await encoder.WriteAsync(count == 0 ? first : camera.Render(), token);
            count++;
        }
    }
}
