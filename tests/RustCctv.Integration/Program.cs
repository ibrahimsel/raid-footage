using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RustCctv.Configuration;
using RustCctv.Integration;
using RustCctv.Recording;
using RustCctv.Rust;
using RustCctv.Services;

// The integration executable also supplies a controlled failed/stalled encoder.
if (args.Contains("pipe:0"))
{
    if (Environment.GetEnvironmentVariable("TEST_ENCODER_MODE") == "stall") await Task.Delay(TimeSpan.FromMinutes(1));
    return 23;
}

var ffmpeg = Environment.GetEnvironmentVariable("TEST_FFMPEG") ?? "ffmpeg";
var ffprobe = Environment.GetEnvironmentVariable("TEST_FFPROBE") ?? "ffprobe";
var output = Path.GetFullPath(Path.Combine("test-output", Guid.NewGuid().ToString("N")));
using var loggers = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true));
Console.WriteLine($"Integration runtime: {Environment.Version}; output: {output}");
RendererChecks.Run(output);
if (args.Contains("--renderer-only")) return 0;
await using var server = new CameraServer();
await server.StartAsync();
Environment.SetEnvironmentVariable("RUST_SERVER", "127.0.0.1");
Environment.SetEnvironmentVariable("RUST_PORT", server.Port.ToString());
Environment.SetEnvironmentVariable("RUST_PLAYER_ID", "1");
Environment.SetEnvironmentVariable("RUST_PLAYER_TOKEN", "123456789"); // synthetic fixture, no live credentials
var connection = RustConnectionOptions.FromEnvironment();
var options = new RecorderOptions
{
    CameraId = "TEST_CAMERA", OutputDirectory = output, Fps = 10, RenderScale = 2, SegmentMinutes = 2.0 / 60,
    FfmpegPath = ffmpeg, FrameTimeoutSeconds = 2, RequestTimeoutSeconds = 2, ReconnectMaxSeconds = 2
};
var paths = new RecordingPathFactory(options);
Check(options.IsValid(), "test settings valid");
var lifetime = new TestLifetime();
using var worker = new RecorderWorker(options, connection, new CameraRecorder(options, paths, loggers),
    new RunMode(false), lifetime, loggers.CreateLogger<RecorderWorker>());
await worker.StartAsync(CancellationToken.None);
await UntilAsync(() => server.Subscriptions >= 2, 16); // Real controller's default 10-second renewal.
Check(server.Connections == 1, "renewal retains one connection");
Check(Directory.GetFiles(paths.CameraDirectory, "*.mp4").Length >= 2, "segments rotate while process stays alive");
server.DropConnection();
await UntilAsync(() => server.Connections >= 2 && server.Subscriptions >= 3, 12);
await Task.Delay(2500);
using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15))) await worker.StopAsync(stop.Token);
Check(Environment.ExitCode == 0, "worker stopped cleanly");
Check(Directory.GetFiles(paths.CameraDirectory, "*.partial.mp4").Length == 0, "shutdown finalizes current segment");
var clips = Directory.GetFiles(paths.CameraDirectory, "*.mp4");
Check(clips.Length >= 5, "recordings survive reconnect without collisions");
foreach (var clip in clips)
{
    var probe = await RunAsync(ffprobe, "-v", "error", "-select_streams", "v:0", "-count_frames",
        "-show_entries", "stream=codec_name,pix_fmt,width,height,r_frame_rate,nb_read_frames:format=duration", "-of", "json", clip);
    using var json = JsonDocument.Parse(probe);
    var stream = json.RootElement.GetProperty("streams")[0];
    Check(stream.GetProperty("codec_name").GetString() == "h264", "H.264 codec");
    Check(stream.GetProperty("pix_fmt").GetString() == "yuv420p", "compatible pixel format");
    Check(stream.GetProperty("r_frame_rate").GetString() == "10/1", "configured constant frame rate");
    Check(stream.GetProperty("width").GetInt32() == 128 && stream.GetProperty("height").GetInt32() == 96, "render dimensions");
    var frames = int.Parse(stream.GetProperty("nb_read_frames").GetString()!);
    var duration = double.Parse(json.RootElement.GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
    Check(frames is >= 1 and <= 20 && Math.Abs(duration - frames / 10.0) < 0.1, "segment timing matches frame count");
    await RunAsync(ffmpeg, "-v", "error", "-xerror", "-i", clip, "-f", "null", "-");
}
Console.WriteLine($"PASS: renewal, reconnect, worker shutdown, and full decode of {clips.Length} H.264 segments.");

await using (var camera = new RustCameraClient(connection, options))
{
    await camera.ConnectAsync(CancellationToken.None);
    var png = camera.Render();
    Check(png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "snapshot PNG signature");
    server.SendFrames = false;
    await Task.Delay(2300);
    await ThrowsAsync<CameraSessionException>(() => { camera.EnsureHealthy(); return Task.CompletedTask; });
}
server.SendFrames = true;
Console.WriteLine("PASS: PNG snapshot and stale-frame detection.");

server.RejectRenewals = true;
await using (var camera = new RustCameraClient(connection, options))
{
    await camera.ConnectAsync(CancellationToken.None);
    await Task.Delay(10500);
    await ThrowsAsync<CameraSessionException>(() => { camera.EnsureHealthy(); return Task.CompletedTask; });
}
server.RejectRenewals = false;
Console.WriteLine("PASS: failed subscription renewal terminates the session.");

server.SendFrames = false;
await using (var camera = new RustCameraClient(connection, options))
    await ThrowsAsync<OperationCanceledException>(() => camera.ConnectAsync(CancellationToken.None));
server.SendFrames = true;
Console.WriteLine("PASS: missing first frame has a bounded deadline.");

options.FfmpegPath = Environment.ProcessPath!;
options.WriteTimeoutSeconds = 1;
options.FinalizeTimeoutSeconds = 1;
Environment.SetEnvironmentVariable("TEST_ENCODER_MODE", "stall");
var stalled = new FfmpegEncoder(options, paths, NullLogger<FfmpegEncoder>.Instance);
await ThrowsAsync<TimeoutException>(() => stalled.WriteAsync(new byte[8 * 1024 * 1024], CancellationToken.None));
await ThrowsAsync<TimeoutException>(() => stalled.DisposeAsync().AsTask());
Environment.SetEnvironmentVariable("TEST_ENCODER_MODE", "exit");
var exited = new FfmpegEncoder(options, paths, NullLogger<FfmpegEncoder>.Instance);
await Task.Delay(500);
await ThrowsAsync<IOException>(() => exited.WriteAsync(new byte[1], CancellationToken.None));
await ThrowsAsync<IOException>(() => exited.DisposeAsync().AsTask());
Environment.SetEnvironmentVariable("TEST_ENCODER_MODE", null);
Console.WriteLine("PASS: stalled encoder input, bounded forced teardown, and early encoder exit.");
Console.WriteLine("ALL INTEGRATION CHECKS PASSED");
return 0;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL: " + message);
}

static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}

static async Task UntilAsync(Func<bool> predicate, int seconds)
{
    var timer = Stopwatch.StartNew();
    while (!predicate())
    {
        if (timer.Elapsed.TotalSeconds > seconds) throw new TimeoutException("Integration condition timed out.");
        await Task.Delay(100);
    }
}

static async Task<string> RunAsync(string command, params string[] args)
{
    var info = new ProcessStartInfo(command) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    foreach (var arg in args) info.ArgumentList.Add(arg);
    using var process = Process.Start(info)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    try { await process.WaitForExitAsync(deadline.Token); }
    catch { process.Kill(true); throw; }
    var error = await stderr;
    Check(process.ExitCode == 0, $"{command} returned {process.ExitCode}: {error}");
    return await stdout;
}

internal sealed class TestLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void StopApplication() { }
}
