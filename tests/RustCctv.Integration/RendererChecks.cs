using System.Text.Json;
using RustCctv.Configuration;
using RustCctv.Rendering;
using RustPlusApi.Camera;
using RustPlusApi.Data.Cameras;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace RustCctv.Integration;

internal static class RendererChecks
{
    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        CheckDecoder();
        CheckSampleCoordinates();
        CheckShadingAndUnknownSamples();
        CheckSampleExpiry();
        CheckViewChanges();
        CheckDimensions();
        CheckPlayerOverlay(output);
        RenderLocalFixtures(output);
        Console.WriteLine("PASS: camera ray decoding, readable shadows, sample expiry, view resets, and render scaling.");
    }

    private static void CheckSampleCoordinates()
    {
        // Protocol shuffle coordinates pinned independently by the upstream decoder fixtures.
        foreach (var (offset, x, y) in new[] { (0, 2, 2), (1, 3, 0), (3, 0, 3), (16, 2, 2) })
        {
            var renderer = new CctvRenderer(4, 4, Options());
            using var empty = Image.Load<Rgba32>(renderer.Render());
            renderer.AddRays(UniformFrame(1, distance: 300, alignment: 63, material: 5) with
            {
                SampleOffset = offset
            });
            using var image = Image.Load<Rgba32>(renderer.Render());
            for (var row = 0; row < image.Height; row++)
            for (var column = 0; column < image.Width; column++)
                Check((image[column, row] != empty[column, row]) == (column == x && row == y),
                    "ray sample shuffle, Y-axis orientation, and sample offset wrapping");
        }
    }

    private static void CheckDecoder()
    {
        // All five instructions, ending in a single-byte reference to the last full sample.
        var actual = CameraRayDecoder.Decode([
            255, 128, 63, 2, // full: depth 512, alignment 63, material 2; table slot 41
            41,             // repeat
            105, 128,       // small delta: depth +1, alignment -3
            169, 128,       // depth delta: depth +1, unchanged alignment
            194, 128, 255,  // compact literal: depth 515, alignment 63, material 2
            41              // final byte is a real sample, not padding
        ]).ToArray();
        var expected = new[]
        {
            new CameraRay(512, 63, 2), new CameraRay(512, 63, 2), new CameraRay(513, 60, 2),
            new CameraRay(513, 63, 2), new CameraRay(515, 63, 2), new CameraRay(515, 63, 2)
        };
        Check(actual.SequenceEqual(expected), "all ray encodings including the final lookback sample");
        Check(!CameraRayDecoder.Decode([]).Any(), "empty ray payload");

        foreach (byte[] truncated in new byte[][]
        {
            [255], [255, 1], [255, 1, 2], [194], [194, 1], [64], [128],
            [255, 128, 63, 2, 255, 1]
        })
        {
            try
            {
                _ = CameraRayDecoder.Decode(truncated).ToArray();
            }
            catch (InvalidDataException) { continue; }
            throw new Exception("FAIL: truncated ray payload must report InvalidDataException.");
        }
    }

    private static void CheckShadingAndUnknownSamples()
    {
        var options = Options();
        var empty = new CctvRenderer(4, 4, options);
        using var background = Image.Load<Rgba32>(empty.Render());
        Check(AllPixels(background, pixel => pixel.A == 255), "unknown background is opaque");

        var shadow = new CctvRenderer(4, 4, options);
        shadow.AddRays(UniformFrame(16, distance: 300, alignment: 0, material: 1));
        using var shaded = Image.Load<Rgba32>(shadow.Render());
        Check(AllPixels(shaded, pixel => pixel.A == 255 && pixel.R + pixel.G + pixel.B > 25),
            "known geometry remains visible at zero alignment");
        Check(!SamePixels(background, shaded), "known shaded terrain differs from missing samples");

        var lit = new CctvRenderer(4, 4, options);
        lit.AddRays(UniformFrame(16, distance: 300, alignment: 63, material: 1));
        using var highlighted = Image.Load<Rgba32>(lit.Render());
        Check(!SamePixels(shaded, highlighted), "alignment still conveys surface shape");

        var noHit = new CctvRenderer(4, 4, options);
        noHit.AddRays(UniformFrame(16, distance: 1023, alignment: 0, material: 0));
        using var noHitImage = Image.Load<Rgba32>(noHit.Render());
        Check(SamePixels(noHitImage, background), "no-hit sentinel remains opaque background");
        var blueMaterial = new CctvRenderer(4, 4, options);
        blueMaterial.AddRays(UniformFrame(16, distance: 300, alignment: 63, material: 2));
        using var blueImage = Image.Load<Rgba32>(blueMaterial.Render());
        Check(AllPixels(blueImage, pixel => pixel.A == 255 && pixel.B > pixel.R), "blue material remains distinct");
        Check(!SamePixels(blueImage, shaded), "blue material and terrain remain distinguishable");

        // Unknown material 9 must not masquerade as terrain 1 through modulo-eight aliasing.
        var unknown = new CctvRenderer(4, 4, options);
        unknown.AddRays(UniformFrame(16, distance: 300, alignment: 63, material: 9));
        using var unknownImage = Image.Load<Rgba32>(unknown.Render());
        Check(!SamePixels(unknownImage, highlighted), "unknown materials do not wrap into known terrain colors");
    }

    private static void CheckSampleExpiry()
    {
        var time = new TestTimeProvider();
        var options = Options();
        var renderer = new CctvRenderer(4, 4, options, time);
        using var empty = Image.Load<Rgba32>(renderer.Render());
        renderer.AddRays(UniformFrame(16, distance: 300, alignment: 63, material: 6));
        using var initial = Image.Load<Rgba32>(renderer.Render());
        Check(!SamePixels(empty, initial), "fresh samples are shown");
        time.Advance(TimeSpan.FromSeconds(options.SampleLifetimeSeconds + 1));
        using var expired = Image.Load<Rgba32>(renderer.Render());
        Check(SamePixels(empty, expired), "old samples expire even when no packet arrives");
        renderer.AddRays(UniformFrame(16, distance: 300, alignment: 63, material: 6));
        using var refreshed = Image.Load<Rgba32>(renderer.Render());
        Check(SamePixels(initial, refreshed), "fresh samples restore the image after expiry");
    }

    private static void CheckViewChanges()
    {
        var original = UniformFrame(16, distance: 300, alignment: 63, material: 5) with
        {
            VerticalFov = 60,
            CameraPosition = new() { X = 5, Y = 5, Z = 5 },
            CameraRotation = new() { X = 0, Y = 0, Z = 0 },
            SampleRotation = new() { X = 0, Y = 0, Z = 0 }
        };
        var unchanged = original with { RayData = [] };
        var changes = new[]
        {
            unchanged with { VerticalFov = 35 },
            unchanged with { CameraPosition = new() { X = 15, Y = 5, Z = 5 } },
            unchanged with
            {
                CameraRotation = new() { X = 0, Y = 45, Z = 0 },
                SampleRotation = new() { X = 0, Y = 45, Z = 0 }
            }
        };
        foreach (var changed in changes)
        {
            var renderer = new CctvRenderer(4, 4, Options());
            using var background = Image.Load<Rgba32>(renderer.Render());
            renderer.AddRays(original);
            using var populated = Image.Load<Rgba32>(renderer.Render());
            renderer.AddRays(unchanged);
            using var stable = Image.Load<Rgba32>(renderer.Render());
            Check(SamePixels(populated, stable), "unchanged view preserves valid accumulated samples");
            renderer.AddRays(changed);
            using var reset = Image.Load<Rgba32>(renderer.Render());
            Check(SamePixels(background, reset), "changed view clears samples from the previous projection");
        }
    }

    private static void CheckDimensions()
    {
        var options = Options();
        options.RenderScale = 2;
        var renderer = new CctvRenderer(8, 6, options);
        renderer.AddRays(UniformFrame(48, distance: 300, alignment: 32, material: 5));
        using var image = Image.Load<Rgba32>(renderer.Render());
        Check(image.Width == 16 && image.Height == 12, "configured output scale");
    }

    private static void CheckPlayerOverlay(string output)
    {
        var player = new CameraEntity
        {
            EntityId = 1,
            Type = CameraEntityType.Player,
            Position = new() { X = 0, Y = 0, Z = 10 },
            Size = new() { X = 2, Y = 2, Z = 2 },
            Name = "EXAMPLE PLAYER"
        };
        Check(PlayerOverlay.TryProjectPlayer(player, 90, 320, 180, out var center), "visible player is projected");
        Check(Math.Abs(center.Left - 151) <= 1 && Math.Abs(center.Top - 81) <= 1 &&
              Math.Abs(center.Width - 18) <= 2 && Math.Abs(center.Height - 18) <= 2,
            "camera-relative player center and perspective size");
        var above = player with { Position = new() { X = 0, Y = 1, Z = 10 } };
        Check(PlayerOverlay.TryProjectPlayer(above, 90, 320, 180, out var upper) && upper.Top < center.Top,
            "positive camera Y projects upward");
        var partial = player with { Position = new() { X = 17, Y = 0, Z = 10 } };
        Check(PlayerOverlay.TryProjectPlayer(partial, 90, 320, 180, out var clipped) &&
              clipped.Right == 320 && clipped.Left > 0, "partly visible marker clips to frame");
        var invalidPlayers = new[]
        {
            player with { Type = CameraEntityType.Tree },
            player with { Position = new() { X = 0, Y = 0, Z = -10 } },
            player with { Position = new() { X = 0, Y = 0, Z = 0 } },
            player with { Position = new() { X = 19, Y = 0, Z = 10 } },
            player with { Position = new() { X = float.NaN, Y = 0, Z = 10 } },
            player with { Size = new() { X = 0, Y = 2, Z = 2 } },
            player with { Size = new() { X = 2, Y = float.PositiveInfinity, Z = 2 } }
        };
        foreach (var invalid in invalidPlayers)
            Check(!PlayerOverlay.TryProjectPlayer(invalid, 90, 320, 180, out _),
                "invisible or invalid entities produce no player marker");
        foreach (var invalidFov in new[] { 0f, 180f, float.NaN, float.PositiveInfinity })
            Check(!PlayerOverlay.TryProjectPlayer(player, invalidFov, 320, 180, out _), "invalid projection FOV rejected");

        var backdrop = new Rgba32(12, 15, 20);
        using var image = new Image<Rgba32>(320, 180, backdrop);
        var frame = new CameraFrame { VerticalFov = 90, Entities = [player] };
        PlayerOverlay.Draw(image, frame);
        Check(!AllPixels(image, pixel => pixel == backdrop), "player label and marker are drawn");
        Check(image[0, 0] == backdrop, "centered marker leaves distant pixels unchanged");
        image.SaveAsPng(Path.Combine(output, "synthetic-player-overlay.png"));

        // Exercise the border and tiny canvases with long, unsupported server-supplied names.
        foreach (var dimensions in new[] { (13, 7), (320, 180) })
        {
            using var borderImage = new Image<Rgba32>(dimensions.Item1, dimensions.Item2, backdrop);
            PlayerOverlay.Draw(borderImage, frame with
            {
                Entities = [partial with { Name = "İbrahim 漢字 😀 " + new string('W', 500) }]
            });
            Check(AllPixels(borderImage, pixel => pixel.A == 255), "clipped Unicode labels remain within an opaque image");
        }

        var time = new TestTimeProvider();
        var renderer = new CctvRenderer(320, 180, Options(), time);
        using var empty = Image.Load<Rgba32>(renderer.Render());
        renderer.AddRays(frame);
        using var marked = Image.Load<Rgba32>(renderer.Render());
        Check(!SamePixels(empty, marked), "renderer includes live player metadata");
        renderer.AddRays(frame with
        {
            Entities = Enumerable.Repeat(player with { Type = CameraEntityType.Tree }, 300).Append(player)
        });
        using var crowded = Image.Load<Rgba32>(renderer.Render());
        Check(SamePixels(marked, crowded), "non-player entities cannot crowd a player out of the overlay");
        time.Advance(TimeSpan.FromSeconds(1));
        using var expired = Image.Load<Rgba32>(renderer.Render());
        Check(SamePixels(empty, expired), "stale player metadata disappears without a new packet");
    }

    private static void RenderLocalFixtures(string output)
    {
        var directory = Path.GetFullPath(Path.Combine("test-output", "reference"));
        foreach (var name in new[] { "cam01", "cam02", "cctv01" })
        {
            var file = Path.Combine(directory, name + "-frames.json");
            if (!File.Exists(file)) continue;
            using var fixture = JsonDocument.Parse(File.ReadAllText(file));
            var width = fixture.RootElement.GetProperty("Width").GetInt32();
            var height = fixture.RootElement.GetProperty("Height").GetInt32();
            var before = new CameraRenderer(width, height);
            var after = new CctvRenderer(width, height, Options(), new TestTimeProvider());
            foreach (var captured in fixture.RootElement.GetProperty("Frames").EnumerateArray())
            {
                var frame = new CameraFrame
                {
                    SampleOffset = captured.GetProperty("SampleOffset").GetInt32(),
                    RayData = Convert.FromBase64String(captured.GetProperty("RayDataBase64").GetString()!)
                };
                Check(CameraRayDecoder.Decode(frame.RayData).Count() == 3000,
                    "upstream live packet contains all 3000 samples");
                before.AddRays(frame);
                after.AddRays(frame);
            }
            var beforePng = before.Render();
            var afterPng = after.Render();
            File.WriteAllBytes(Path.Combine(output, name + "-before.png"), beforePng);
            File.WriteAllBytes(Path.Combine(output, name + "-after.png"), afterPng);
            using var oldImage = Image.Load<Rgba32>(beforePng);
            using var newImage = Image.Load<Rgba32>(afterPng);
            Check(newImage.Width == width && newImage.Height == height, "live fixture preserves native dimensions");
            Check(!SamePixels(oldImage, newImage), "live fixture shading changes visibly");
            var goldenPath = Path.Combine(directory, name + "-golden.png");
            if (File.Exists(goldenPath))
            {
                using var golden = Image.Load<Rgba32>(goldenPath);
                Check(SamePixels(oldImage, golden), "baseline reproduces upstream captured golden image");
            }
            Console.WriteLine($"PASS: optional live fixture {name}; before/after PNGs saved in {output}.");
        }
    }

    private static RecorderOptions Options() => new() { RenderScale = 1, SampleLifetimeSeconds = 2 };

    private static CameraFrame UniformFrame(int count, int distance, int alignment, int material)
    {
        var rays = new byte[count * 4];
        for (var i = 0; i < count; i++)
        {
            rays[i * 4] = 255;
            rays[i * 4 + 1] = (byte)(distance >> 2);
            rays[i * 4 + 2] = (byte)(((distance & 3) << 6) | alignment);
            rays[i * 4 + 3] = (byte)material;
        }
        return new CameraFrame { SampleOffset = 0, RayData = rays, VerticalFov = 60, Distance = 100 };
    }

    private static bool AllPixels(Image<Rgba32> image, Func<Rgba32, bool> check)
    {
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
            if (!check(image[x, y])) return false;
        return true;
    }

    private static bool SamePixels(Image<Rgba32> left, Image<Rgba32> right)
    {
        if (left.Width != right.Width || left.Height != right.Height) return false;
        for (var y = 0; y < left.Height; y++)
        for (var x = 0; x < left.Width; x++)
            if (left[x, y] != right[x, y]) return false;
        return true;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: renderer: " + message);
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;
        private long timestamp;
        public override DateTimeOffset GetUtcNow() => now;
        public override long GetTimestamp() => timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan elapsed)
        {
            now += elapsed;
            timestamp += elapsed.Ticks;
        }
    }
}
