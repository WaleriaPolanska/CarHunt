using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Text;
using AngleSharp.Html.Parser;
using CarHunt.Models;

namespace CarHunt.Services.Scraping;

public class AutoScout24Scraper : IScraperService
{
    public string FilterCurrency => "EUR";

    public string SourceName => "AutoScout24";

    public ScrapeFailure? LastFailure { get; private set; }
    private readonly HttpClient _http;

    public AutoScout24Scraper(HttpClient http) => _http = http;

    public async Task<List<Car>> SearchAsync(SearchParameters p, CancellationToken ct = default)
    {
        var url = BuildUrl(p);
        var cars = new List<Car>();
        try
        {
            var page = await ScraperHelpers.FetchBytesAsync(_http, url, "en-US,en;q=0.9", ct);

            // The site is Next.js, so the listings are in the page's data block.
            using (var json = ScraperHelpers.ParseNextData(page))
            {
                if (json != null)
                {
                    cars = ParseNextData(json.RootElement, p);
                    if (cars.Count > 0) return cars;
                }
            }

            // Fallback for when that block is missing or holds no listings: read the
            // markup. Only here is the page decoded into text, and only here is a DOM
            // built for it.
            var doc = new HtmlParser().ParseDocument(Encoding.UTF8.GetString(page));
            var articles = doc.QuerySelectorAll("article, [data-testid*=\"listing\"]");
            foreach (var article in articles.Take(30))
            {
                var car = new Car { Source = SourceName, Currency = "EUR" };
                var titleEl = article.QuerySelector("h2, a[title]");
                car.Title = titleEl?.TextContent.Trim() ?? titleEl?.GetAttribute("title") ?? "";
                var allText = article.TextContent;
                car.Price = ScraperHelpers.ExtractPrice(allText);
                car.Mileage = ScraperHelpers.ExtractMileage(allText);
                car.Year = ScraperHelpers.ExtractYear(allText);
                car.ImageUrl = ScraperHelpers.ExtractImage(article, "https://www.autoscout24.com");
                var linkEl = article.QuerySelector("a[href]");
                car.SourceUrl = ScraperHelpers.NormalizeUrl(linkEl?.GetAttribute("href"), "https://www.autoscout24.com");
                car.Brand = p.Brand;
                car.Model = p.Model;
                car.DateScraped = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (!string.IsNullOrEmpty(car.Title) && car.Price > 0)
                    cars.Add(car);
            }
            LastFailure = cars.Count > 0 ? null : ScrapeFailure.NothingReadable;
        }
        catch (Exception e)
        {
            LastFailure = ScraperHelpers.Classify(e);
        }
        return cars;
    }

    private List<Car> ParseNextData(JsonElement root, SearchParameters p)
    {
        var cars = new List<Car>();
        try
        {
            if (!root.TryGetProperty("props", out var props)) return cars;
            if (!props.TryGetProperty("pageProps", out var pageProps)) return cars;
            if (!pageProps.TryGetProperty("listings", out var listings) || listings.ValueKind != JsonValueKind.Array)
                return cars;

            var modelFilter = ScraperHelpers.NormalizeText(p.Model);

            foreach (var item in listings.EnumerateArray())
            {
                var car = new Car { Source = SourceName, Currency = "EUR" };

                var vehicle = item.TryGetProperty("vehicle", out var v) ? v : default;
                var make = GetStr(vehicle, "make") ?? p.Brand;
                var model = GetStr(vehicle, "modelGroup") ?? GetStr(vehicle, "model") ?? p.Model;
                var variant = GetStr(vehicle, "variant");
                car.Brand = make ?? "";
                car.Model = model ?? "";
                car.Title = string.Join(" ", new[] { make, model, variant }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));

                // The "tracking" object exposes clean numeric strings.
                var tracking = item.TryGetProperty("tracking", out var t) ? t : default;
                car.Price = ParseIntLoose(GetStr(tracking, "price"));
                if (car.Price == 0 && item.TryGetProperty("price", out var pr))
                    car.Price = ParseIntLoose(GetStr(pr, "priceFormatted"));
                car.Mileage = ParseIntLoose(GetStr(tracking, "mileage") ?? GetStr(vehicle, "mileageInKm"));
                car.Year = ParseYear(GetStr(tracking, "firstRegistration"));
                car.FuelType = GetStr(vehicle, "fuel") ?? "";
                car.Transmission = GetStr(vehicle, "transmission") ?? "";

                car.ImageUrl = GetImage(item);
                car.SourceUrl = ScraperHelpers.NormalizeUrl(GetStr(item, "url"), "https://www.autoscout24.com");

                if (item.TryGetProperty("location", out var loc))
                {
                    car.City = GetStr(loc, "city") ?? "";
                    car.Country = GetStr(loc, "countryCode") ?? "";
                }

                car.DateScraped = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                if (string.IsNullOrWhiteSpace(car.Title)) continue;
                // The URL only filters by brand, so narrow by model on our side.
                if (modelFilter.Length > 0 && !ScraperHelpers.NormalizeText(car.Title).Contains(modelFilter))
                    continue;

                cars.Add(car);
            }
        }
        catch { }
        return cars;
    }

    static string? GetStr(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    static int ParseIntLoose(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        var digits = new string(s.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var r) ? r : 0;
    }

    static int ParseYear(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        var m = System.Text.RegularExpressions.Regex.Match(s, @"(19|20)\d{2}");
        return m.Success && int.TryParse(m.Value, out var y) ? y : 0;
    }

    // AutoScout24's Next.js data can expose images as an "images" array of strings
    // or objects ({ uri | url | src }), or a single "image"/"imageUrl" string.
    static string GetImage(JsonElement item)
    {
        if (item.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array && imgs.GetArrayLength() > 0)
        {
            var first = imgs[0];
            if (first.ValueKind == JsonValueKind.String)
                return ScraperHelpers.NormalizeUrl(first.GetString(), "https://www.autoscout24.com");
            if (first.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "uri", "url", "src", "image" })
                    if (first.TryGetProperty(key, out var u) && u.ValueKind == JsonValueKind.String)
                        return ScraperHelpers.NormalizeUrl(u.GetString(), "https://www.autoscout24.com");
            }
        }

        foreach (var key in new[] { "image", "imageUrl", "thumbnail" })
        {
            var s = GetStr(item, key);
            if (!string.IsNullOrEmpty(s))
                return ScraperHelpers.NormalizeUrl(s, "https://www.autoscout24.com");
        }
        return "";
    }

    string BuildUrl(SearchParameters p)
    {
        var brand = HttpUtility.UrlEncode(ScraperHelpers.BrandSlug(p.Brand));
        // Model is intentionally NOT placed in the path: AutoScout returns 404 for
        // unknown model slugs (e.g. "3-series"), so we query by brand and filter the
        // results by model in ParseNextData.
        // Price sorting is deliberately NOT delegated to the site: sorting by price
        // (either direction) fills the first page with placeholder ads — 1 EUR when
        // ascending, 11,111,111 EUR when descending. Relevance order returns real
        // offers, and we sort those by price ourselves. Age sorting is safe though,
        // and it is the only way to order by listing date since AutoScout24 does not
        // expose one in its data.
        // Only ask the site for date ordering; its price ordering returns placeholders
        // at both extremes, so price is sorted on our side instead.
        var sort = "";
        if (p.SortBy.Contains(SortOption.NewestFirst)) sort = "&sort=age&desc=1";
        else if (p.SortBy.Contains(SortOption.OldestFirst)) sort = "&sort=age&desc=0";
        var page = p.Page > 1 ? $"&page={p.Page}" : "";
        var url = string.IsNullOrEmpty(brand)
            ? $"https://www.autoscout24.com/lst?atype=C{sort}{page}"
            : $"https://www.autoscout24.com/lst/{brand}?atype=C{sort}{page}";
        if (p.YearFrom.HasValue) url += $"&fregfrom={p.YearFrom}";
        if (p.YearTo.HasValue) url += $"&fregto={p.YearTo}";
        if (p.PriceFrom.HasValue) url += $"&pricefrom={p.PriceFrom}";
        if (p.PriceTo.HasValue) url += $"&priceto={p.PriceTo}";
        if (p.MileageTo.HasValue) url += $"&kmto={p.MileageTo}";
        return url;
    }
}
