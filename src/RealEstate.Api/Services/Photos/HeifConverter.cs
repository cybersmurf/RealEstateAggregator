using System.ComponentModel;
using System.Diagnostics;

namespace RealEstate.Api.Services.Photos;

/// <summary>
/// HEIC/HEIF z iPhonu → JPEG přes <c>heif-convert</c> (balík libheif-examples v obrazu API).
/// Prohlížeč HEIC nezobrazí a SkiaSharp ho nedekóduje, takže fotka z prohlídky byla do 7. 10. 2026
/// uložená, ale v galerii černá a pro obrazový model nepoužitelná.
/// </summary>
public static class HeifConverter
{
    private static readonly string[] HeifExtensions = [".heic", ".heif", ".hif"];
    private static bool _toolMissingLogged;

    public static bool IsHeif(string? fileName)
        => fileName is not null && HeifExtensions.Contains(Path.GetExtension(fileName).ToLowerInvariant());

    /// <summary>JPEG kvality 90, nebo null když převod selže (nástroj chybí, poškozený soubor) – fotka se pak uloží tak, jak přišla.</summary>
    public static async Task<byte[]?> ToJpegAsync(byte[] heif, ILogger? logger, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "heif-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var input = Path.Combine(dir, "in.heic");
        var output = Path.Combine(dir, "out.jpg");
        try
        {
            await File.WriteAllBytesAsync(input, heif, ct);
            var psi = new ProcessStartInfo("heif-convert")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-q");
            psi.ArgumentList.Add("90");
            psi.ArgumentList.Add(input);
            psi.ArgumentList.Add(output);

            using var process = Process.Start(psi);
            if (process is null) return null;
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0 || !File.Exists(output))
            {
                logger?.LogWarning("heif-convert selhal (kód {Code}): {Error}", process.ExitCode, stderr.Trim());
                return null;
            }
            return await File.ReadAllBytesAsync(output, ct);
        }
        catch (Win32Exception)
        {
            if (!_toolMissingLogged)
            {
                logger?.LogWarning("heif-convert není nainstalovaný – HEIC fotky zůstávají nepřevedené");
                _toolMissingLogged = true;
            }
            return null;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
