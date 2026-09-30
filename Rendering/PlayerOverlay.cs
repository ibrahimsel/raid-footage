using RustPlusApi.Data.Cameras;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace RustCctv.Rendering;

/// <summary>Marks the player metadata in the current packet; never identifies players from pixels.</summary>
public static class PlayerOverlay
{
    private static readonly Rgba32 Marker = new(150, 240, 95);
    private static readonly Rgba32 Backdrop = new(16, 20, 27);
    private const double NearPlane = 0.01;

    public static void Draw(Image<Rgba32> image, CameraFrame frame)
    {
        var scale = Math.Clamp(image.Height / 180, 1, 4);
        // Names are supplied by the server and can be absent for distant players.
        foreach (var entity in frame.Entities)
        {
            if (!TryProjectPlayer(entity, frame.VerticalFov, image.Width, image.Height, out var bounds))
                continue;

            var stroke = Math.Clamp(scale, 1, 3);
            Fill(image, bounds.Left, bounds.Top, bounds.Width, stroke, Marker);
            Fill(image, bounds.Left, bounds.Bottom - stroke, bounds.Width, stroke, Marker);
            Fill(image, bounds.Left, bounds.Top, stroke, bounds.Height, Marker);
            Fill(image, bounds.Right - stroke, bounds.Top, stroke, bounds.Height, Marker);

            var availableCharacters = Math.Min(40, (image.Width - 2 * scale) / (6 * scale));
            if (availableCharacters < 1) continue;
            var label = string.IsNullOrWhiteSpace(entity.Name) ? "PLAYER" : entity.Name;
            var text = new string(label.Take(availableCharacters).Select(DisplayCharacter).ToArray());
            var labelWidth = (text.Length * 6 + 1) * scale;
            var labelHeight = 9 * scale;
            var x = Math.Clamp(bounds.Left + (bounds.Width - labelWidth) / 2, 0, Math.Max(0, image.Width - labelWidth));
            var y = bounds.Top >= labelHeight ? bounds.Top - labelHeight : bounds.Top;
            y = Math.Clamp(y, 0, Math.Max(0, image.Height - labelHeight));
            Fill(image, x, y, labelWidth, labelHeight, Backdrop);
            for (var index = 0; index < text.Length; index++)
                DrawCharacter(image, text[index], x + (1 + index * 6) * scale, y + scale, scale);
        }
    }

    /// <summary>
    /// Projects an upright marker sized by the entity metadata. Camera entities use camera-relative
    /// positions, with positive Z forward: x = W/2 + X*f/Z, y = H/2 - Y*f/Z, f = H/(2*tan(vFov/2)).
    /// This is a marker, not a reconstructed player silhouette or an occlusion test.
    /// </summary>
    public static bool TryProjectPlayer(CameraEntity entity, float verticalFov, int width, int height, out Rectangle bounds)
    {
        bounds = default;
        if (entity.Type != CameraEntityType.Player || width <= 0 || height <= 0 ||
            !float.IsFinite(verticalFov) || verticalFov <= 0 || verticalFov >= 180 ||
            entity.Position is null || entity.Size is null)
            return false;

        double x = entity.Position.X, y = entity.Position.Y, z = entity.Position.Z;
        double sizeX = entity.Size.X, sizeY = entity.Size.Y, sizeZ = entity.Size.Z;
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z) || z <= NearPlane ||
            !double.IsFinite(sizeX) || !double.IsFinite(sizeY) || !double.IsFinite(sizeZ) ||
            sizeX <= 0 || sizeY <= 0 || sizeZ <= 0)
            return false;

        // A screen-facing metadata marker keeps the bounds stable as a player turns.
        // Use the entity depth rather than projecting any corner through or behind the camera.
        var focalLength = height / (2 * Math.Tan(verticalFov * Math.PI / 360));
        var centerX = width / 2.0 + x * focalLength / z;
        var centerY = height / 2.0 - y * focalLength / z;
        var halfWidth = sizeX * focalLength / (2 * z);
        var halfHeight = sizeY * focalLength / (2 * z);
        var left = centerX - halfWidth;
        var right = centerX + halfWidth;
        var top = centerY - halfHeight;
        var bottom = centerY + halfHeight;
        if (!double.IsFinite(left) || !double.IsFinite(right) || !double.IsFinite(top) || !double.IsFinite(bottom) ||
            right <= 0 || bottom <= 0 || left >= width || top >= height)
            return false;

        var clippedLeft = (int)Math.Floor(Math.Clamp(left, 0, width));
        var clippedRight = (int)Math.Ceiling(Math.Clamp(right, 0, width));
        var clippedTop = (int)Math.Floor(Math.Clamp(top, 0, height));
        var clippedBottom = (int)Math.Ceiling(Math.Clamp(bottom, 0, height));
        if (clippedRight <= clippedLeft || clippedBottom <= clippedTop) return false;
        bounds = new Rectangle(clippedLeft, clippedTop, clippedRight - clippedLeft, clippedBottom - clippedTop);
        return true;
    }

    // Portable bitmap text avoids requiring fonts to be installed on a recording host.
    // The overlay uses uppercase ASCII; unsupported characters are visibly replaced by '?'.
    private static char DisplayCharacter(char value)
    {
        var upper = char.ToUpperInvariant(value);
        return upper is >= ' ' and <= '~' ? upper : '?';
    }

    private static void DrawCharacter(Image<Rgba32> image, char value, int x, int y, int scale)
    {
        ReadOnlySpan<byte> rows = Glyph(value);
        for (var row = 0; row < rows.Length; row++)
            for (var column = 0; column < 5; column++)
                if ((rows[row] & (1 << (4 - column))) != 0)
                    Fill(image, x + column * scale, y + row * scale, scale, scale, Marker);
    }

    private static void Fill(Image<Rgba32> image, int x, int y, int width, int height, Rgba32 color)
    {
        var left = Math.Clamp(x, 0, image.Width);
        var top = Math.Clamp(y, 0, image.Height);
        var right = Math.Clamp(x + width, 0, image.Width);
        var bottom = Math.Clamp(y + height, 0, image.Height);
        for (var row = top; row < bottom; row++)
            for (var column = left; column < right; column++)
                image[column, row] = color;
    }

    // Five-bit rows, top to bottom. These intentionally simple glyphs are drawn locally.
    private static ReadOnlySpan<byte> Glyph(char value) => value switch
    {
        ' ' => [0, 0, 0, 0, 0, 0, 0],
        'A' => [14, 17, 17, 31, 17, 17, 17],
        'B' => [30, 17, 17, 30, 17, 17, 30],
        'C' => [14, 17, 16, 16, 16, 17, 14],
        'D' => [30, 17, 17, 17, 17, 17, 30],
        'E' => [31, 16, 16, 30, 16, 16, 31],
        'F' => [31, 16, 16, 30, 16, 16, 16],
        'G' => [14, 17, 16, 23, 17, 17, 14],
        'H' => [17, 17, 17, 31, 17, 17, 17],
        'I' => [14, 4, 4, 4, 4, 4, 14],
        'J' => [7, 2, 2, 2, 18, 18, 12],
        'K' => [17, 18, 20, 24, 20, 18, 17],
        'L' => [16, 16, 16, 16, 16, 16, 31],
        'M' => [17, 27, 21, 21, 17, 17, 17],
        'N' => [17, 25, 21, 19, 17, 17, 17],
        'O' => [14, 17, 17, 17, 17, 17, 14],
        'P' => [30, 17, 17, 30, 16, 16, 16],
        'Q' => [14, 17, 17, 17, 21, 18, 13],
        'R' => [30, 17, 17, 30, 20, 18, 17],
        'S' => [15, 16, 16, 14, 1, 1, 30],
        'T' => [31, 4, 4, 4, 4, 4, 4],
        'U' => [17, 17, 17, 17, 17, 17, 14],
        'V' => [17, 17, 17, 17, 17, 10, 4],
        'W' => [17, 17, 17, 21, 21, 27, 17],
        'X' => [17, 17, 10, 4, 10, 17, 17],
        'Y' => [17, 17, 10, 4, 4, 4, 4],
        'Z' => [31, 1, 2, 4, 8, 16, 31],
        '0' => [14, 17, 19, 21, 25, 17, 14],
        '1' => [4, 12, 4, 4, 4, 4, 14],
        '2' => [14, 17, 1, 2, 4, 8, 31],
        '3' => [30, 1, 1, 14, 1, 1, 30],
        '4' => [2, 6, 10, 18, 31, 2, 2],
        '5' => [31, 16, 16, 30, 1, 1, 30],
        '6' => [14, 16, 16, 30, 17, 17, 14],
        '7' => [31, 1, 2, 4, 8, 8, 8],
        '8' => [14, 17, 17, 14, 17, 17, 14],
        '9' => [14, 17, 17, 15, 1, 1, 14],
        '.' => [0, 0, 0, 0, 0, 6, 6],
        ',' => [0, 0, 0, 0, 6, 6, 4],
        ':' => [0, 6, 6, 0, 6, 6, 0],
        ';' => [0, 6, 6, 0, 6, 6, 4],
        '-' => [0, 0, 0, 31, 0, 0, 0],
        '_' => [0, 0, 0, 0, 0, 0, 31],
        '+' => [0, 4, 4, 31, 4, 4, 0],
        '=' => [0, 0, 31, 0, 31, 0, 0],
        '/' => [1, 2, 2, 4, 8, 8, 16],
        '\\' => [16, 8, 8, 4, 2, 2, 1],
        '(' => [2, 4, 8, 8, 8, 4, 2],
        ')' => [8, 4, 2, 2, 2, 4, 8],
        '[' => [14, 8, 8, 8, 8, 8, 14],
        ']' => [14, 2, 2, 2, 2, 2, 14],
        '{' => [3, 4, 4, 8, 4, 4, 3],
        '}' => [24, 4, 4, 2, 4, 4, 24],
        '<' => [1, 2, 4, 8, 4, 2, 1],
        '>' => [16, 8, 4, 2, 4, 8, 16],
        '!' => [4, 4, 4, 4, 4, 0, 4],
        '\'' => [4, 4, 8, 0, 0, 0, 0],
        '"' => [10, 10, 10, 0, 0, 0, 0],
        '*' => [0, 17, 10, 31, 10, 17, 0],
        '#' => [10, 10, 31, 10, 31, 10, 10],
        '@' => [14, 17, 23, 21, 23, 16, 14],
        '$' => [4, 15, 20, 14, 5, 30, 4],
        '%' => [25, 25, 2, 4, 8, 19, 19],
        '&' => [12, 18, 20, 8, 21, 18, 13],
        '^' => [4, 10, 17, 0, 0, 0, 0],
        '|' => [4, 4, 4, 4, 4, 4, 4],
        '~' => [0, 0, 8, 21, 2, 0, 0],
        '`' => [8, 4, 0, 0, 0, 0, 0],
        _ => [14, 17, 1, 2, 4, 0, 4]
    };
}
