using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using RustCctv.Configuration;

namespace RustCctv.Recording;

public sealed class FfmpegEncoder : IAsyncDisposable
{
    private readonly Process process;
    private readonly RecorderOptions options;
    private readonly ILogger logger;
    private readonly Task segmentReader;
    private readonly Task errorReader;
    private readonly string directory;
    private readonly string session = Guid.NewGuid().ToString("N");
    private readonly DateTimeOffset start = DateTimeOffset.UtcNow;
    private readonly Queue<string> errors = new();
    private bool disposed;

    public FfmpegEncoder(RecorderOptions options, RecordingPathFactory paths, ILogger<FfmpegEncoder> logger)
    {
        this.options = options;
        this.logger = logger;
        directory = paths.CameraDirectory;
        Directory.CreateDirectory(directory);
        var seconds = (options.SegmentMinutes * 60).ToString("0.########", CultureInfo.InvariantCulture);
        var fps = options.Fps.ToString(CultureInfo.InvariantCulture);
        var info = new ProcessStartInfo(options.FfmpegPath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true,
            WorkingDirectory = directory
        };
        string[] args = ["-hide_banner", "-loglevel", "warning", "-nostats", "-nostdin", "-n",
            // image2pipe otherwise waits for 4096-byte reads, delaying highly compressed/blank PNGs.
            "-f", "image2pipe", "-frame_size", "128", "-framerate", fps, "-c:v", "png",
            "-probesize", "32", "-analyzeduration", "1", "-fpsprobesize", "0", "-threads", "1", "-i", "pipe:0",
            "-an", "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-crf", options.Crf.ToString(CultureInfo.InvariantCulture),
            "-threads", options.EncoderThreads.ToString(CultureInfo.InvariantCulture),
            "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2", "-pix_fmt", "yuv420p",
            "-g", (options.Fps * 2).ToString(CultureInfo.InvariantCulture), "-sc_threshold", "0",
            "-force_key_frames", $"expr:gte(t,n_forced*{seconds})",
            "-f", "segment", "-segment_time", seconds, "-segment_time_delta", (0.5 / options.Fps).ToString(CultureInfo.InvariantCulture),
            "-reset_timestamps", "1", "-segment_format", "mp4", "-segment_format_options", "movflags=+faststart",
            "-segment_list", "pipe:1", "-segment_list_type", "csv", "-segment_list_size", "0",
            $"{session}_%06d.partial.mp4"];
        foreach (var arg in args) info.ArgumentList.Add(arg);
        process = new Process { StartInfo = info };
        try
        {
            if (!process.Start()) throw new IOException("FFmpeg failed to start.");
        }
        catch { process.Dispose(); throw; }
        // Both redirected streams must be drained for the entire process lifetime.
        errorReader = ReadErrorsAsync();
        segmentReader = ReadSegmentsAsync();
        logger.LogInformation("Encoder started: {Directory}, {Fps} FPS, {Minutes} minute segments (UTC names).",
            directory, options.Fps, options.SegmentMinutes);
    }

    private async Task ReadErrorsAsync()
    {
        while (await process.StandardError.ReadLineAsync() is { } line)
        {
            if (errors.Count == 12) errors.Dequeue();
            errors.Enqueue(line.Length > 1000 ? line[..1000] : line);
        }
    }

    private async Task ReadSegmentsAsync()
    {
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            var fields = line.Split(','); // Our generated relative filename cannot contain commas.
            if (fields.Length != 3 || !double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var offset)
                || !double.IsFinite(offset) || offset < 0)
                throw new IOException("Invalid FFmpeg segment completion record.");
            var filename = fields[0].Trim('"');
            if (!filename.StartsWith(session + "_", StringComparison.Ordinal) || filename != Path.GetFileName(filename)
                || !filename.EndsWith(".partial.mp4", StringComparison.Ordinal)
                || !int.TryParse(filename.AsSpan(session.Length + 1, 6), out var index))
                throw new IOException("Unexpected FFmpeg segment filename.");
            var completed = RecordingPathFactory.CompletedName(start.AddSeconds(offset), session, index);
            File.Move(Path.Combine(directory, filename), Path.Combine(directory, completed), overwrite: false);
            logger.LogInformation("Finalized recording: {File}", completed);
        }
    }

    public async Task WriteAsync(byte[] png, CancellationToken token)
    {
        if (segmentReader.IsFaulted) await segmentReader;
        if (errorReader.IsFaulted) await errorReader;
        if (process.HasExited) throw new IOException($"FFmpeg exited with code {process.ExitCode}.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.WriteTimeoutSeconds));
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(png, deadline.Token);
            // Process.StandardInput can wrap a buffered FileStream (notably on Windows).
            // Small PNGs must reach FFmpeg now, not when that buffer happens to fill.
            await process.StandardInput.BaseStream.FlushAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("FFmpeg input stalled."); }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            // EOF lets FFmpeg flush delayed frames and write the MP4 trailer. Do not use the canceled host token.
            process.StandardInput.Close();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(options.FinalizeTimeoutSeconds));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                logger.LogError("FFmpeg did not finish before its deadline; the last .partial.mp4 may be incomplete.");
                throw new TimeoutException("FFmpeg finalization timed out.");
            }
            await Task.WhenAll(errorReader, segmentReader);
            if (process.ExitCode != 0)
            {
                logger.LogError("FFmpeg failed ({Code}): {Diagnostics}", process.ExitCode, string.Join(" | ", errors));
                throw new IOException("FFmpeg could not finalize normally.");
            }
            logger.LogInformation("Encoder stopped and recording finalized.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            try { await Task.WhenAll(errorReader, segmentReader); }
            catch (Exception) { logger.LogError("Encoder output processing failed; inspect any .partial.mp4 files."); }
            process.Dispose();
        }
    }
}
