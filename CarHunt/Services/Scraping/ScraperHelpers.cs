using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using CarHunt.Models;

namespace CarHunt.Services.Scraping;

/// <summary>
/// Small shared helpers for turning messy scraped markup into usable URLs.
/// </summary>
public static class ScraperHelpers
{
    private const string DesktopUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    /// <summary>
    /// Turns an exception into the reason a person would give. A refusal carries an HTTP
    /// status; anything below that — DNS, no route, a dead socket — never reached the site.
    /// </summary>
    public static ScrapeFailure Classify(Exception e) => e switch
    {
        HttpRequestException { StatusCode: not null } => ScrapeFailure.SiteRefused,
        TaskCanceledException => ScrapeFailure.NoConnection,
        HttpRequestException => ScrapeFailure.NoConnection,
        _ => ScrapeFailure.NothingReadable
    };

    /// <summary>
    /// Fetches a listing page. Every source did this identically, down to the browser
    /// string, so the four copies are now one.
    ///
    /// Headers go on the request rather than the client: one HttpClient is shared by
    /// scrapers running in parallel, and mutating DefaultRequestHeaders would race.
    /// </summary>
    public static async Task<string> FetchHtmlAsync(
        HttpClient http, string url, string acceptLanguage, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(DesktopUserAgent);
        request.Headers.AcceptLanguage.ParseAdd(acceptLanguage);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Fetches a page as raw bytes. Used where the payload is only a wrapper around
    /// embedded JSON: a 1.8 MB page becomes a 3.6 MB string once decoded to UTF-16, and
    /// the JSON is then copied out of it a second time. Bytes skip both.
    /// </summary>
    public static async Task<byte[]> FetchBytesAsync(
        HttpClient http, string url, string acceptLanguage, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(DesktopUserAgent);
        request.Headers.AcceptLanguage.ParseAdd(acceptLanguage);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>
    /// Pulls the &lt;script id="__NEXT_DATA__"&gt; payload out of a Next.js page and
    /// parses it in place — the document reads straight out of the downloaded buffer,
    /// with no substring taken. Both AutoScout24 and the OLX sites are built this way and
    /// each used to cut the block out by hand.
    /// </summary>
    public static JsonDocument? ParseNextData(byte[] page)
    {
        var span = page.AsSpan();

        var marker = span.IndexOf("id=\"__NEXT_DATA__\""u8);
        if (marker < 0) return null;

        var tagEnd = span[marker..].IndexOf((byte)'>');
        if (tagEnd < 0) return null;
        var start = marker + tagEnd + 1;

        var length = span[start..].IndexOf("</script>"u8);
        if (length <= 0) return null;

        try
        {
            return JsonDocument.Parse(new ReadOnlyMemory<byte>(page, start, length));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Narrows results to a model. The sites filter by make only, and model names are
    /// localised, so this happens here — falling back to everything when the name
    /// matches nothing, which beats showing an empty list.
    /// </summary>
    /// <param name="text">
    /// What to match against. Defaults to the title; the OLX sites publish a model field
    /// of their own and match on both.
    /// </param>
    public static List<Car> FilterByModel(List<Car> cars, string? model, Func<Car, string>? text = null)
    {
        if (string.IsNullOrWhiteSpace(model) || cars.Count == 0) return cars;

        text ??= c => c.Title;
        var wanted = NormalizeText(model);
        // Nothing matched means this site has no such car, not that every car will do.
        // Falling back to the whole list is how a search for one model answered with the
        // site's entire front page.
        return cars.Where(c => NormalizeText(text(c)).Contains(wanted)).ToList();
    }

    /// <summary>
    /// The make as the listing sites spell it in their addresses. They expect their own
    /// slug and answer an unknown one with their full catalogue rather than an error:
    /// "mercedes" returned 32 cars of which 4 were a Mercedes, "mercedes-benz" all 32.
    /// </summary>
    public static string BrandSlug(string? brand)
    {
        var name = (brand ?? "").Trim().ToLowerInvariant();
        if (name.Length == 0) return "";

        name = Aliases.TryGetValue(name, out var full) ? full : name;
        return name.Replace(' ', '-');
    }

    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["mercedes"] = "mercedes-benz",
        ["merc"] = "mercedes-benz",
        ["vw"] = "volkswagen",
        ["alfa"] = "alfa-romeo",
        ["range rover"] = "land-rover",
        ["rover"] = "land-rover",
        ["citroen"] = "citroen",
        ["vauxhall"] = "opel"
    };

    /// <summary>
    /// Lowercases and strips everything but letters/digits, for fuzzy model matching
    /// (e.g. "3 Series" -&gt; "3series").
    /// </summary>
    public static string NormalizeText(string? s) =>
        string.IsNullOrWhiteSpace(s) ? "" : new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>
    /// Normalizes a possibly relative / protocol-relative URL into an absolute https URL.
    /// </summary>
    public static string NormalizeUrl(string? url, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        url = url.Trim();

        if (url.StartsWith("//")) return Keep("https:" + url);          // protocol-relative
        if (url.StartsWith("/")) return Keep(baseUrl.TrimEnd('/') + url); // root-relative
        return Keep(url);
    }

    /// <summary>
    /// Passes only http(s). These URLs come out of other people's listing pages, and
    /// anything else there — javascript:, file:, intent: — would be handed straight to
    /// the in-app browser or the platform, running whatever the listing author wrote.
    /// </summary>
    private static string Keep(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
        (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp)
            ? url
            : "";

    /// <summary>
    /// Picks the first URL out of a srcset attribute ("url1 320w, url2 640w").
    /// </summary>
    public static string FirstFromSrcset(string? srcset)
    {
        if (string.IsNullOrWhiteSpace(srcset)) return "";
        var first = srcset.Split(',')[0].Trim();
        var space = first.IndexOf(' ');
        return space > 0 ? first.Substring(0, space) : first;
    }

    /// <summary>
    /// Robustly extracts an image URL from a listing element, accounting for lazy-loading
    /// (data-src / srcset) and &lt;picture&gt;&lt;source&gt; markup used by these sites.
    /// </summary>
    public static string ExtractImage(IElement? item, string baseUrl)
    {
        if (item is null) return "";

        var img = item.QuerySelector("img");
        string? src = img?.GetAttribute("src");

        if (IsUnusable(src)) src = img?.GetAttribute("data-src");
        if (IsUnusable(src)) src = FirstFromSrcset(img?.GetAttribute("srcset"));
        if (IsUnusable(src)) src = FirstFromSrcset(img?.GetAttribute("data-srcset"));

        if (IsUnusable(src))
        {
            var source = item.QuerySelector("source[srcset], source[data-srcset]");
            src = FirstFromSrcset(source?.GetAttribute("srcset") ?? source?.GetAttribute("data-srcset"));
        }

        return NormalizeUrl(src, baseUrl);
    }

    private static bool IsUnusable(string? url) =>
        string.IsNullOrWhiteSpace(url) || url.StartsWith("data:");

    /// <summary>
    /// Loosely finds a price near a currency marker (€12.500, 12.500 €, EUR 12500, ...)
    /// in raw listing text. Shared by scrapers that fall back to plain-text parsing.
    /// </summary>
    public static int ExtractPrice(string text)
    {
        var patterns = new[] { "€", "EUR", "eur" };
        foreach (var pat in patterns)
        {
            var idx = text.IndexOf(pat, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            // Look after the symbol
            var afterStart = idx + pat.Length;
            var afterLen = Math.Min(15, text.Length - afterStart);
            if (afterLen > 0)
            {
                var after = text.Substring(afterStart, afterLen).Trim();
                var digits = new string(after.TakeWhile(c => char.IsDigit(c) || c == '.' || c == ',' || c == ' ').ToArray());
                var cleaned = digits.Replace(".", "").Replace(",", "").Replace(" ", "");
                if (int.TryParse(cleaned, out var price) && price > 100) return price;
            }
            // Look before the symbol
            var beforeStart = Math.Max(0, idx - 15);
            var before = text.Substring(beforeStart, idx - beforeStart).Trim();
            var bDigits = new string(before.Reverse().TakeWhile(c => char.IsDigit(c) || c == '.' || c == ',' || c == ' ').Reverse().ToArray());
            var bCleaned = bDigits.Replace(".", "").Replace(",", "").Replace(" ", "");
            if (int.TryParse(bCleaned, out var bPrice) && bPrice > 100) return bPrice;
        }
        return 0;
    }

    /// <summary>Loosely finds a mileage figure ("123.456 km") in raw listing text.</summary>
    public static int ExtractMileage(string text)
    {
        var idx = text.IndexOf("km", StringComparison.OrdinalIgnoreCase);
        if (idx < 3) return 0;
        var start = Math.Max(0, idx - 12);
        var before = text.Substring(start, idx - start);
        var cleaned = before.Replace(".", "").Replace(",", "").Replace(" ", "");
        var digits = new string(cleaned.Where(char.IsDigit).ToArray());
        int.TryParse(digits, out var r);
        return r;
    }

    /// <summary>Loosely finds a plausible model/registration year in raw listing text.</summary>
    public static int ExtractYear(string text)
    {
        var maxYear = DateTime.UtcNow.Year + 1; // next model year is already on sale before Jan 1
        for (int i = 0; i <= text.Length - 4; i++)
        {
            if (char.IsDigit(text[i]) && char.IsDigit(text[i + 1]) && char.IsDigit(text[i + 2]) && char.IsDigit(text[i + 3]))
            {
                if (int.TryParse(text.Substring(i, 4), out var y) && y >= 1990 && y <= maxYear)
                    if (i + 4 >= text.Length || !char.IsDigit(text[i + 4])) return y;
            }
        }
        return 0;
    }
}

