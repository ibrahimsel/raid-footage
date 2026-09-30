namespace RustCctv.Rendering;

public readonly record struct CameraRay(int Distance, int Alignment, int Material);

/// <summary>Rust+ depth/material wire decoder. See THIRD_PARTY_NOTICES for protocol references.</summary>
public static class CameraRayDecoder
{
    public static IEnumerable<CameraRay> Decode(byte[] data)
    {
        var lookback = new CameraRay[64];
        var position = 0;
        // A final one-byte lookup is a complete ray, not padding.
        while (position < data.Length)
        {
            int tag = data[position++];
            var mode = tag & 192;
            int required = tag == 255 ? 3 : mode == 0 ? 0 : mode == 192 ? 2 : 1;
            if (data.Length - position < required)
                throw new InvalidDataException("Truncated camera ray instruction.");

            CameraRay ray;
            if (tag == 255 || mode == 192)
            {
                int first = data[position++], second = data[position++];
                int material = tag == 255 ? data[position++] : tag & 63;
                ray = new CameraRay((first << 2) | (second >> 6), second & 63, material);
                var slot = (3 * (ray.Distance / 128) + 5 * (ray.Alignment / 16) + 7 * material) & 63;
                lookback[slot] = ray;
            }
            else
            {
                ray = lookback[tag & 63];
                if (mode == 64)
                {
                    int delta = data[position++];
                    ray = ray with { Distance = ray.Distance + (delta >> 3) - 15,
                        Alignment = ray.Alignment + (delta & 7) - 3 };
                }
                else if (mode == 128)
                    ray = ray with { Distance = ray.Distance + data[position++] - 127 };
            }
            if (ray.Distance is < 0 or > 1023 || ray.Alignment is < 0 or > 63)
                throw new InvalidDataException("Camera ray is outside its encoded range.");
            yield return ray;
        }
    }
}
