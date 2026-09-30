namespace RustCctv.Configuration;

public sealed class RecorderOptions
{
    public string CameraId { get; set; } = "";
    public string OutputDirectory { get; set; } = "recordings";
    public int Fps { get; set; } = 10;
    public int RenderScale { get; set; } = 2;
    public double SampleLifetimeSeconds { get; set; } = 3;
    public double SegmentMinutes { get; set; } = 15;
    public string FfmpegPath { get; set; } = "ffmpeg";
    public int EncoderThreads { get; set; } = 2;
    public int Crf { get; set; } = 18;
    public int FrameTimeoutSeconds { get; set; } = 30;
    public int RequestTimeoutSeconds { get; set; } = 15;
    public int WriteTimeoutSeconds { get; set; } = 10;
    public int FinalizeTimeoutSeconds { get; set; } = 30;
    public int MaxLagSeconds { get; set; } = 5;
    public int ReconnectMaxSeconds { get; set; } = 60;
    public int SnapshotSeconds { get; set; } = 15;

    public bool IsValid() => !string.IsNullOrWhiteSpace(CameraId) && CameraId.Length <= 256
        && !string.IsNullOrWhiteSpace(OutputDirectory) && !string.IsNullOrWhiteSpace(FfmpegPath)
        && Fps is >= 1 and <= 30 && RenderScale is >= 1 and <= 4
        && double.IsFinite(SampleLifetimeSeconds) && SampleLifetimeSeconds is >= 0.25 and <= 30
        && double.IsFinite(SegmentMinutes)
        && SegmentMinutes is >= (1.0 / 60) and <= 1440
        && EncoderThreads is >= 1 and <= 4 && Crf is >= 0 and <= 51
        && FrameTimeoutSeconds is >= 1 and <= 300 && RequestTimeoutSeconds is >= 1 and <= 120
        && WriteTimeoutSeconds is >= 1 and <= 120 && FinalizeTimeoutSeconds is >= 1 and <= 120
        && MaxLagSeconds is >= 1 and <= 30 && ReconnectMaxSeconds is >= 1 and <= 300
        && SnapshotSeconds is >= 1 and <= 300;
}
