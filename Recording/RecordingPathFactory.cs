using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using RustCctv.Configuration;

namespace RustCctv.Recording;

public sealed class RecordingPathFactory(RecorderOptions options)
{
    public string CameraDirectory
    {
        get
        {
            var id = options.CameraId;
            var safe = Regex.Replace(id, "[^A-Za-z0-9_-]", "_");
            if (safe != id || safe.Length > 80 || Regex.IsMatch(safe, "^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$", RegexOptions.IgnoreCase))
                safe = safe[..Math.Min(safe.Length, 80)] + "_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..12];
            return Path.GetFullPath(Path.Combine(options.OutputDirectory, safe));
        }
    }

    public static string CompletedName(DateTimeOffset start, string session, int index) =>
        $"{start.UtcDateTime.ToString("yyyy-MM-dd_HH-mm-ss-fff", CultureInfo.InvariantCulture)}Z_{session}_{index:D6}.mp4";
}
