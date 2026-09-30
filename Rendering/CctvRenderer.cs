using RustCctv.Configuration;
using RustPlusApi.Data.Cameras;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace RustCctv.Rendering;

/// <summary>
/// Renders observed samples with readable shadows and a bounded history.
/// Surface colours are a visualization of material codes, not game textures.
/// Call AddRays and Render under the same lock.
/// </summary>
public sealed class CctvRenderer
{
    private static readonly Rgba32 Background = new(22, 25, 39);
    // Keep the reference material distinctions. Lift low-alignment surfaces instead
    // of multiplying them to black; unknown codes use neutral grey, never modulo 8.
    private static readonly Rgba32[] Palette =
    [
        new(128, 128, 128), new(174, 161, 107), new(60, 151, 221), new(153, 153, 153),
        new(178, 178, 178), new(225, 166, 87), new(246, 85, 80), new(255, 50, 45)
    ];
    private readonly int width;
    private readonly int height;
    private readonly int scale;
    private readonly double lifetime;
    private double estimatedSweepSeconds;
    private long? lastSamplesAt;
    private readonly TimeProvider time;
    private readonly int[] positions;
    private readonly CameraRay[] rays;
    private readonly long[] received;
    private readonly bool[] known;
    private CameraFrame? lastFrame;
    private CameraFrame? viewReference;
    private long lastFrameAt;

    public CctvRenderer(int width, int height, RecorderOptions options, TimeProvider? timeProvider = null)
    {
        if (width is < 1 or > 2048 || height is < 1 or > 2048)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (options.RenderScale is < 1 or > 4 || !double.IsFinite(options.SampleLifetimeSeconds)
            || options.SampleLifetimeSeconds is < 0.25 or > 30)
            throw new ArgumentOutOfRangeException(nameof(options));
        this.width = width;
        this.height = height;
        scale = options.RenderScale;
        lifetime = options.SampleLifetimeSeconds;
        time = timeProvider ?? TimeProvider.System;
        positions = BuildPositions(width * height);
        rays = new CameraRay[positions.Length];
        received = new long[positions.Length];
        known = new bool[positions.Length];
    }

    public void AddRays(CameraFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.SampleOffset < 0) throw new InvalidDataException("Negative camera sample offset.");
        if (viewReference is null || ViewChanged(viewReference, frame))
        {
            Array.Clear(known);
            viewReference = frame;
        }
        else if ((viewReference.CameraRotation is null && frame.CameraRotation is not null)
            || (viewReference.CameraPosition is null && frame.CameraPosition is not null))
        {
            // Pose metadata can arrive after the first ray packet. Start comparing
            // against its first known value instead of keeping a null reference forever.
            viewReference = viewReference with
            {
                CameraRotation = viewReference.CameraRotation ?? frame.CameraRotation,
                CameraPosition = viewReference.CameraPosition ?? frame.CameraPosition
            };
        }
        var now = time.GetTimestamp();
        var cursor = frame.SampleOffset % positions.Length;
        var sampleCount = 0;
        foreach (var ray in CameraRayDecoder.Decode(frame.RayData))
        {
            var index = positions[cursor];
            rays[index] = ray;
            received[index] = now;
            known[index] = true;
            cursor = (cursor + 1) % positions.Length;
            sampleCount++;
        }
        UpdateSweepEstimate(sampleCount, now);
        // Copy entities so a caller cannot mutate a list while a subsequent frame renders.
        lastFrame = frame with { Entities = frame.Entities.Where(entity => entity.Type == CameraEntityType.Player)
            .Take(256).ToArray() };
        lastFrameAt = now;
    }

    public byte[] Render()
    {
        var now = time.GetTimestamp();
        // Allow a complete native-resolution sample sweep plus headroom. A fixed
        // three-second expiry can erase pixels before a sparse feed revisits them.
        var retention = Math.Max(lifetime, Math.Min(30, estimatedSweepSeconds * 1.5));
        using var image = new Image<Rgba32>(width, height, Background);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(height - 1 - y);
                for (var x = 0; x < width; x++)
                {
                    var index = y * width + x;
                    if (!known[index]) continue;
                    var age = time.GetElapsedTime(received[index], now).TotalSeconds;
                    if (age >= retention) { known[index] = false; continue; }
                    var ray = rays[index];
                    // The no-hit sentinel contains no surface. Leave it as background.
                    if (ray is { Distance: 1023, Alignment: 0, Material: 0 }) continue;
                    var colour = ray.Material < Palette.Length ? Palette[ray.Material] : Palette[0];
                    var light = 0.38 + 0.62 * Math.Sqrt(ray.Alignment / 63.0);
                    // Fade only at the end of the retention interval. Old samples must
                    // not survive forever when a player or the camera moves.
                    var freshness = Math.Clamp((retention - age) / (retention * 0.25), 0, 1);
                    row[x] = new Rgba32(Shade(colour.R, Background.R), Shade(colour.G, Background.G),
                        Shade(colour.B, Background.B));
                    byte Shade(byte channel, byte backdrop) =>
                        (byte)Math.Clamp(Math.Round(backdrop + (channel * light - backdrop) * freshness), 0, 255);
                }
            }
        });
        if (scale != 1)
            image.Mutate(context => context.Resize(width * scale, height * scale, KnownResamplers.NearestNeighbor));
        // Metadata is current-frame data, not an indefinitely accumulated player trail.
        if (lastFrame is not null && time.GetElapsedTime(lastFrameAt, now).TotalSeconds < Math.Min(lifetime, 0.75))
            PlayerOverlay.Draw(image, lastFrame);
        using var stream = new MemoryStream();
        image.SaveAsPng(stream, new PngEncoder { CompressionLevel = PngCompressionLevel.Level1 });
        return stream.ToArray();
    }

    private void UpdateSweepEstimate(int sampleCount, long now)
    {
        if (sampleCount == 0) return;
        if (lastSamplesAt is { } previous)
        {
            var interval = time.GetElapsedTime(previous, now).TotalSeconds;
            if (interval > 0)
            {
                var observed = Math.Min(30, interval * positions.Length / Math.Min(sampleCount, positions.Length));
                // Extend promptly when delivery slows; shorten gradually when it
                // recovers so packet jitter does not repeatedly punch holes in the scene.
                estimatedSweepSeconds = observed > estimatedSweepSeconds ? observed
                    : estimatedSweepSeconds * 0.9 + observed * 0.1;
            }
        }
        lastSamplesAt = now;
    }

    private static bool ViewChanged(CameraFrame previous, CameraFrame current)
    {
        if (float.IsFinite(previous.VerticalFov) && float.IsFinite(current.VerticalFov)
            && Math.Abs(previous.VerticalFov - current.VerticalFov) > 0.25) return true;
        if (previous.CameraPosition is { } a && current.CameraPosition is { } b
            && (Math.Abs(a.X - b.X) > 0.05 || Math.Abs(a.Y - b.Y) > 0.05 || Math.Abs(a.Z - b.Z) > 0.05)) return true;
        // SampleRotation is the ray-casting orientation and includes sampling jitter.
        // Only the physical camera pose is a reason to discard the accumulated view.
        var oldRotation = previous.CameraRotation;
        var newRotation = current.CameraRotation;
        return oldRotation is { } c && newRotation is { } d
            && (Angle(c.X, d.X) > 0.5 || Angle(c.Y, d.Y) > 0.5 || Angle(c.Z, d.Z) > 0.5);
    }

    private static double Angle(float a, float b) => Math.Abs(Math.IEEERemainder(a - b, 360));

    private static int[] BuildPositions(int count)
    {
        // Protocol-specific shuffle, including its signed conversion. Changing it
        // to Random or a conventional xorshift conversion scrambles received rays.
        var positions = Enumerable.Range(0, count).ToArray();
        var state = 1337;
        NextState();
        for (var index = count - 1; index >= 1; index--)
        {
            var swap = (int)(NextState() * (index + 1) / 4294967295L);
            (positions[index], positions[swap]) = (positions[swap], positions[index]);
        }
        return positions;

        long NextState()
        {
            unchecked
            {
                var old = state;
                state ^= state << 13;
                state ^= state >>> 17;
                state ^= state << 5;
                return old >= 0 ? old : 4294967295L + old - 1;
            }
        }
    }
}
