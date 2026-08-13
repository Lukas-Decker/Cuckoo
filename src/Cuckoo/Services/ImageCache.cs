using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using Cuckoo.Core;

namespace Cuckoo.Services;

/// <summary>
/// Downloads and caches remote images (campaign box art) on background threads.
/// Disk cache with 7-day expiry, ported from the original miner (minus perceptual hashing).
/// Returned bitmaps are frozen, so they can be created off the UI thread.
/// </summary>
public sealed class ImageCache
{
    private static readonly TimeSpan Expiry = TimeSpan.FromDays(7);
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private readonly string _cacheDir;
    private readonly ConcurrentDictionary<string, Task<BitmapImage?>> _inFlight = new();

    public ImageCache()
    {
        _cacheDir = Path.Combine(Constants.WorkingDir, "cache");
        Directory.CreateDirectory(_cacheDir);
        CleanupExpired();
    }

    private void CleanupExpired()
    {
        try
        {
            foreach (FileInfo file in new DirectoryInfo(_cacheDir).EnumerateFiles())
            {
                if (DateTime.UtcNow - file.LastWriteTimeUtc > Expiry)
                    file.Delete();
            }
        }
        catch (IOException) { }
    }

    /// <summary>Gets a decoded, frozen bitmap for the URL, from disk cache or the network.</summary>
    public Task<BitmapImage?> GetAsync(string url, int decodeWidth = 0)
    {
        if (string.IsNullOrEmpty(url))
            return Task.FromResult<BitmapImage?>(null);
        return _inFlight.GetOrAdd($"{url}#{decodeWidth}", _ => FetchAsync(url, decodeWidth));
    }

    private async Task<BitmapImage?> FetchAsync(string url, int decodeWidth)
    {
        try
        {
            string cachePath = Path.Combine(_cacheDir, HashName(url));
            byte[]? bytes = null;
            if (File.Exists(cachePath)
                && DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) <= Expiry)
            {
                bytes = await File.ReadAllBytesAsync(cachePath).ConfigureAwait(false);
            }
            if (bytes is null)
            {
                bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
                await File.WriteAllBytesAsync(cachePath, bytes).ConfigureAwait(false);
            }
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeWidth > 0)
                bitmap.DecodePixelWidth = decodeWidth;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze(); // usable from any thread
            return bitmap;
        }
        catch (Exception)
        {
            // image loading is purely cosmetic: any failure just leaves the image empty
            return null;
        }
    }

    private static string HashName(string url)
    {
        string extension = Path.GetExtension(new Uri(url).AbsolutePath);
        if (extension is not (".jpg" or ".jpeg" or ".png" or ".gif"))
            extension = ".img";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexStringLower(hash)[..32] + extension;
    }
}
