using RustCctv.Configuration;
using RustCctv.Rendering;
using RustPlusApi.Data.Cameras;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace RustCctv.Integration;

/// <summary>Entirely synthetic ray traffic; these checks are not a live recording quality claim.</summary>
internal static class TimedRendererChecks
{
    private const int Width = 320;
    private const int Height = 180;
    private const int Pixels = Width * Height;
    private const int RaysPerPacket = 3000;

    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        var failures = new List<string>();
        foreach (var packetsPerSecond in new[] { 5, 2 })
        foreach (var hasCameraRotation in new[] { true, false })
        {
            var name = $"synthetic-{packetsPerSecond}hz-{(hasCameraRotation ? "fixed-camera" : "no-camera-rotation")}";
            try { CheckSparseStream(output, name, packetsPerSecond, hasCameraRotation); }
            catch (Exception error)
            {
                failures.Add(name + ": " + error.Message);
                Console.WriteLine("FAIL: " + failures[^1]);
            }
        }
        CheckActualCameraRotation();
        CheckLatePoseMetadata();
        CheckNoHitReplacement();
        if (failures.Count != 0)
            throw new Exception("Timed synthetic renderer checks failed:\n" + string.Join('\n', failures));
        Console.WriteLine("PASS: synthetic timed sparse streams retain complete scenes at 5/2 Hz, ignore sampling jitter, " +
                          "clear on camera rotation, and expire after silence (10 FPS renders throughout).");
    }

    private static void CheckSparseStream(string output, string name, int packetsPerSecond, bool hasCameraRotation)
    {
        var time = new SyntheticTimeProvider();
        var renderer = new CctvRenderer(Width, Height, Options(), time);
        using var background = Image.Load<Rgba32>(renderer.Render());
        var missing = background[0, 0];
        var packet = LiteralFrame(RaysPerPacket);
        Check(CameraRayDecoder.Decode(packet.RayData).Count() == RaysPerPacket,
            "every synthetic literal packet decodes to exactly 3000 rays");

        // Obtain the exact unfaded material shade through the public API. No production
        // palette, shuffle implementation, or retention calculation is copied here.
        var reference = new CctvRenderer(Width, Height, Options(), time);
        reference.AddRays(LiteralFrame(Pixels));
        using var fresh = Image.Load<Rgba32>(reference.Render());
        var expected = fresh[0, 0];
        Check(expected != missing, "synthetic geometry differs from the no-hit background");
        Check(CountPixels(fresh, expected) == Pixels, "full synthetic reference has one uniform fresh shade");

        var ticksPerPacket = 10 / packetsPerSecond;
        var packetsForSweep = (int)Math.Ceiling((double)Pixels / RaysPerPacket);
        var ticksToRun = packetsForSweep * ticksPerPacket * 3;
        var packetsSent = 0;
        var previousFilled = 0;
        var minimumFresh = Pixels;
        var savedComplete = false;
        for (var tick = 0; tick < ticksToRun; tick++)
        {
            if (tick % ticksPerPacket == 0)
            {
                // Offsets address the protocol's shuffled sample order. The jitter is
                // intentionally larger than the former 0.5-degree view-reset threshold.
                renderer.AddRays(packet with
                {
                    SampleOffset = packetsSent * RaysPerPacket % Pixels,
                    CameraRotation = hasCameraRotation ? new() { X = 0, Y = 0, Z = 0 } : null,
                    SampleRotation = new() { X = packetsSent % 2 == 0 ? -1 : 1, Y = 0, Z = 0 }
                });
                packetsSent++;
            }

            var png = renderer.Render();
            using var image = Image.Load<Rgba32>(png);
            if (tick == 0)
                File.WriteAllBytes(Path.Combine(output, name + "-first-sparse-packet.png"), png);
            var filled = Pixels - CountPixels(image, missing);
            Check(filled >= previousFilled,
                $"synthetic steady view lost accumulated samples at {tick / 10.0:F1}s " +
                $"({previousFilled} -> {filled}); sampling jitter must not reset the scene");
            previousFilled = filled;

            // Render between packets as the recorder does. Checking only on arrival or
            // feeding every packet at one fake instant misses the expiry/fading failure.
            if (packetsSent >= packetsForSweep)
            {
                var exactFresh = CountPixels(image, expected);
                minimumFresh = Math.Min(minimumFresh, exactFresh);
                if (!savedComplete)
                {
                    File.WriteAllBytes(Path.Combine(output, name + "-after-full-sweep.png"), png);
                    savedComplete = true;
                }
                Check(filled >= Pixels * 0.95,
                    $"synthetic full-sweep coverage at {tick / 10.0:F1}s is {100.0 * filled / Pixels:F1}% (needs >=95%)");
                Check(exactFresh >= Pixels * 0.95,
                    $"synthetic healthy scene at {tick / 10.0:F1}s has {100.0 * exactFresh / Pixels:F1}% " +
                    "fresh-colored pixels (needs >=95%); valid samples must not fade between refreshes");
            }
            time.Advance(TimeSpan.FromMilliseconds(100));
        }

        Check(savedComplete, "synthetic simulation completed a full pixel sweep");
        time.Advance(TimeSpan.FromSeconds(35));
        using var expired = Image.Load<Rgba32>(renderer.Render());
        Check(CountPixels(expired, missing) == Pixels, "synthetic stopped stream expires after 35 seconds of silence");
        Console.WriteLine($"PASS: {name}; minimum fresh coverage after a full sweep: " +
                          $"{100.0 * minimumFresh / Pixels:F1}%; synthetic previews saved in {output}.");
    }

    private static void CheckActualCameraRotation()
    {
        var time = new SyntheticTimeProvider();
        var renderer = new CctvRenderer(Width, Height, Options(), time);
        using var initial = Image.Load<Rgba32>(renderer.Render());
        var missing = initial[0, 0];
        var populated = LiteralFrame(Pixels) with
        {
            CameraRotation = new() { X = 0, Y = 0, Z = 0 },
            SampleRotation = new() { X = 1, Y = 0, Z = 0 }
        };
        renderer.AddRays(populated);
        using var scene = Image.Load<Rgba32>(renderer.Render());
        Check(CountPixels(scene, missing) == 0, "synthetic rotation check starts with a populated scene");
        time.Advance(TimeSpan.FromMilliseconds(100));
        renderer.AddRays(populated with
        {
            RayData = [],
            CameraRotation = new() { X = 0, Y = 15, Z = 0 }
            // Keep SampleRotation identical: actual camera motion alone must reset.
        });
        using var cleared = Image.Load<Rgba32>(renderer.Render());
        Check(CountPixels(cleared, missing) == Pixels,
            "actual CameraRotation change clears synthetic old geometry even with unchanged SampleRotation");
    }

    private static RecorderOptions Options() => new() { RenderScale = 1, SampleLifetimeSeconds = 3 };

    private static void CheckLatePoseMetadata()
    {
        foreach (var rotate in new[] { false, true })
        {
            var renderer = new CctvRenderer(4, 4, Options(), new SyntheticTimeProvider());
            using var empty = Image.Load<Rgba32>(renderer.Render());
            var background = empty[0, 0];
            renderer.AddRays(LiteralFrame(16));
            var known = LiteralFrame(0) with
            {
                CameraRotation = new() { Y = 20 }, CameraPosition = new() { X = 10 }
            };
            renderer.AddRays(known);
            using var anchored = Image.Load<Rgba32>(renderer.Render());
            Check(CountPixels(anchored, background) == 0, "first available pose preserves the existing scene");
            renderer.AddRays(rotate
                ? known with { CameraRotation = new() { Y = 30 } }
                : known with { CameraPosition = new() { X = 20 } });
            using var reset = Image.Load<Rgba32>(renderer.Render());
            Check(CountPixels(reset, background) == 16, "later pose changes reset even when the first packet lacked pose");
        }
    }

    private static void CheckNoHitReplacement()
    {
        var time = new SyntheticTimeProvider();
        var renderer = new CctvRenderer(4, 4, Options(), time);
        using var initial = Image.Load<Rgba32>(renderer.Render());
        var missing = initial[0, 0];
        var surface = LiteralFrame(16);
        renderer.AddRays(surface);
        using var populated = Image.Load<Rgba32>(renderer.Render());
        Check(CountPixels(populated, missing) == 0, "synthetic no-hit check starts with observed geometry");
        var noHit = new byte[16 * 4];
        for (var index = 0; index < 16; index++)
        {
            noHit[index * 4] = 255;
            noHit[index * 4 + 1] = 255;
            noHit[index * 4 + 2] = 192; // Distance 1023, alignment 0, material 0.
        }
        time.Advance(TimeSpan.FromMilliseconds(100));
        renderer.AddRays(surface with { RayData = noHit });
        using var cleared = Image.Load<Rgba32>(renderer.Render());
        Check(CountPixels(cleared, missing) == 16,
            "new synthetic no-hit samples immediately replace previously observed geometry");
    }

    private static CameraFrame LiteralFrame(int count)
    {
        var data = new byte[count * 4];
        for (var index = 0; index < count; index++)
        {
            data[index * 4] = 255;      // Full literal, independent of the lookback table.
            data[index * 4 + 1] = 75;  // Distance 300.
            data[index * 4 + 2] = 63;  // Full surface alignment.
            data[index * 4 + 3] = 5;   // Constant, visibly non-background material.
        }
        return new CameraFrame { RayData = data, VerticalFov = 60, Distance = 100 };
    }

    private static int CountPixels(Image<Rgba32> image, Rgba32 color)
    {
        var count = 0;
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                foreach (var pixel in row)
                    if (pixel == color) count++;
            }
        });
        return count;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: timed renderer: " + message);
    }

    private sealed class SyntheticTimeProvider : TimeProvider
    {
        private long timestamp;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(timestamp);
        public override long GetTimestamp() => timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan elapsed) => timestamp += elapsed.Ticks;
    }
}
