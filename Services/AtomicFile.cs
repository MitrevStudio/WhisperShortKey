using System.Text;

namespace whispershortkey.Services;

/// <summary>
/// Write-then-rename, so a crash or a full disk mid-write leaves the previous file intact
/// instead of a truncated one. Losing settings.json this way costs the user their API keys.
/// </summary>
internal static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void WriteAllText(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temp = path + ".tmp";
        File.WriteAllText(temp, contents, Utf8NoBom);
        File.Move(temp, path, overwrite: true);
    }
}
