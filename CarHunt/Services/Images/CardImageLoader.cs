using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using AsyncImageLoader.Loaders;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace CarHunt.Services.Images;

/// <summary>
/// Loads listing photos, keeping a bounded number of them decoded and freeing the rest.
///
/// A decoded photo lives in native memory, which the garbage collector cannot see: it
/// counts a bitmap as a few dozen managed bytes and has no reason to hurry. Scrolling
/// a long list therefore piles up photos nothing is showing any more — measured on the
/// emulator, sixty swipes took the native heap from 71 MB to 167 MB and it kept going.
///
/// So the photos are held here instead, most recently used first, and anything past the
/// limit is disposed the moment it falls off the end. The limit is far larger than a
/// screenful, so a photo is only ever released once it is well out of sight.
/// </summary>
public class CardImageLoader : DiskCachedWebImageLoader
{
    private const int KeepDecoded = 120;

    private readonly Dictionary<string, LinkedListNode<(string Url, Bitmap Image)>> _index = new();
    private readonly LinkedList<(string Url, Bitmap Image)> _order = new();
    private readonly object _gate = new();

    public CardImageLoader(string cacheFolder) : base(WebpClient(), true, cacheFolder) { }

    /// <summary>
    /// Asks for WebP. Measured on one photo per source: Motogratka 20 KB to 12 KB and
    /// Kleinanzeigen 4 KB to 3 KB, the other two already serve it. Same picture, a third
    /// less to download and a third less to keep on disk.
    /// </summary>
    private static HttpClient WebpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // WebP but not AVIF: Kleinanzeigen serves AVIF when it is offered, and Skia
        // cannot decode it — the cards came back with blank photo boxes.
        http.DefaultRequestHeaders.Accept.ParseAdd("image/webp,image/jpeg,image/png,*/*;q=0.8");
        return http;
    }

    /// <summary>
    /// Keeps the photo folder from growing without end. Files are dropped oldest first
    /// until the folder is back under the limit — they are only copies of what the sites
    /// still hold, so losing one costs a re-download and nothing else.
    /// </summary>
    public static void TrimCache(string folder, long limitBytes)
    {
        try
        {
            var files = new DirectoryInfo(folder).GetFiles()
                .OrderByDescending(f => f.LastWriteTimeUtc).ToList();

            long kept = 0;
            foreach (var file in files)
            {
                kept += file.Length;
                if (kept > limitBytes) file.Delete();
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// The overload the image control actually calls when the page has a storage
    /// provider. Both routes go through the same cache — overriding only the other one
    /// left this path loading photos the cache never saw.
    /// </summary>
    public override Task<Bitmap?> ProvideImageAsync(string url, IStorageProvider? storageProvider) =>
        ProvideImageAsync(url);

    public override async Task<Bitmap?> ProvideImageAsync(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;

        lock (_gate)
        {
            if (_index.TryGetValue(url, out var found))
            {
                // Touched, so it goes back to the front and outlives colder photos.
                _order.Remove(found);
                _order.AddFirst(found);
                return found.Value.Image;
            }
        }

        var image = await base.ProvideImageAsync(url);
        if (image is null) return null;

        Bitmap? evicted = null;
        lock (_gate)
        {
            // Another card may have asked for the same photo while this one was loading.
            if (_index.TryGetValue(url, out var raced))
            {
                image.Dispose();
                return raced.Value.Image;
            }

            _index[url] = _order.AddFirst((url, image));

            if (_order.Count > KeepDecoded)
            {
                var last = _order.Last!;
                _order.RemoveLast();
                _index.Remove(last.Value.Url);
                evicted = last.Value.Image;
            }
        }

        evicted?.Dispose();
        return image;
    }
}
