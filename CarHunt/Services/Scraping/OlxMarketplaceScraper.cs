using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CarHunt.Models;

namespace CarHunt.Services.Scraping;

/// <summary>
/// Reader for the OLX Group car marketplaces. Their sites share one platform, down to
/// the query parameter names, so a site supplies only its address, country and language.
/// OtoMoto is the one in use; others from the group drop in the same way.
/// </summary>
public abstract class OlxMarketplaceScraper : IScraperService
{
    private readonly HttpClient _http;

    protected OlxMarketplaceScraper(HttpClient http) => _http = http;

    public abstract string SourceName { get; }

    public abstract string FilterCurrency { get; }

    public ScrapeFailure? LastFailure { get; private set; }

    /// <summary>Listing path without a make, e.g. https://www.autovit.ro/autoturisme</summary>
    protected abstract string ListingsUrl { get; }

    /// <summary>Two-letter country the ads are published in.</summary>
    protected abstract string Country { get; }

    protected abstract string AcceptLanguage { get; }

    /// <summary>
    /// Whether the site offers the "not damaged" filter. Not every country in the group
    /// does, and asking for a filter that does not exist there returns an empty page
    /// rather than ignoring it.
    /// </summary>
    protected virtual bool SupportsUndamagedFilter => true;

    public async Task<List<Car>> SearchAsync(SearchParameters p, CancellationToken ct = default)
    {
        var cars = new List<Car>();

        try
        {
            var page = await ScraperHelpers.FetchBytesAsync(_http, BuildUrl(p), AcceptLanguage, ct);

            using var doc = ScraperHelpers.ParseNextData(page);
            if (doc == null) return cars;
            // Results ship inside an urql GraphQL cache:
            // props.pageProps.urqlState.{hash}.data is itself a JSON string.
            if (!doc.RootElement.TryGetProperty("props", out var props)) return cars;
            if (!props.TryGetProperty("pageProps", out var pageProps)) return cars;
            if (!pageProps.TryGetProperty("urqlState", out var urql) || urql.ValueKind != JsonValueKind.Object)
                return cars;

            var seen = new HashSet<string>();
            foreach (var entry in urql.EnumerateObject())
            {
                if (!entry.Value.TryGetProperty("data", out var dataEl)) continue;
                var dataStr = dataEl.ValueKind == JsonValueKind.String ? dataEl.GetString() : dataEl.GetRawText();
                if (string.IsNullOrWhiteSpace(dataStr)) continue;

                using var inner = JsonDocument.Parse(dataStr);
                CollectAdverts(inner.RootElement, cars, seen);
            }

            // These sites publish a model field alongside the title, so both are matched.
            cars = ScraperHelpers.FilterByModel(cars, p.Model, c => $"{c.Title} {c.Model}");
            LastFailure = cars.Count > 0 ? null : ScrapeFailure.NothingReadable;
        }
        catch (Exception e)
        {
            LastFailure = ScraperHelpers.Classify(e);
        }

        return cars;
    }

    private void CollectAdverts(JsonElement el, List<Car> cars, HashSet<string> seen)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("__typename", out var tn) && tn.ValueKind == JsonValueKind.String &&
                tn.GetString() == "Advert" &&
                el.TryGetProperty("title", out _) && el.TryGetProperty("url", out _))
            {
                var car = MapAdvert(el);
                if (car != null && (string.IsNullOrEmpty(car.SourceUrl) || seen.Add(car.SourceUrl)))
                    cars.Add(car);
                return;
            }

            foreach (var prop in el.EnumerateObject())
                CollectAdverts(prop.Value, cars, seen);
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in el.EnumerateArray())
                CollectAdverts(v, cars, seen);
        }
    }

    private Car? MapAdvert(JsonElement node)
    {
        var car = new Car { Source = SourceName, Country = Country };
        car.Title = GetStr(node, "title") ?? "";
        car.Description = GetStr(node, "shortDescription") ?? "";
        car.SourceUrl = ScraperHelpers.NormalizeUrl(GetStr(node, "url"), ListingsUrl);

        // price.amount.units (+ currencyCode) — the currency differs per country, so it
        // is read from the ad rather than assumed.
        if (node.TryGetProperty("price", out var price) && price.TryGetProperty("amount", out var amount))
        {
            if (amount.TryGetProperty("units", out var units))
            {
                if (units.ValueKind == JsonValueKind.Number) car.Price = units.GetInt32();
                else if (units.ValueKind == JsonValueKind.String && int.TryParse(units.GetString(), out var pv)) car.Price = pv;
            }
            car.Currency = GetStr(amount, "currencyCode") ?? "EUR";
        }

        if (node.TryGetProperty("thumbnail", out var th))
            car.ImageUrl = Thumbnail(ScraperHelpers.NormalizeUrl(GetStr(th, "x2") ?? GetStr(th, "x1"), ListingsUrl));

        if (node.TryGetProperty("parameters", out var prms) && prms.ValueKind == JsonValueKind.Array)
        {
            foreach (var par in prms.EnumerateArray())
            {
                var key = GetStr(par, "key");
                var val = GetStr(par, "value");
                var disp = GetStr(par, "displayValue");
                switch (key)
                {
                    case "year": if (int.TryParse(val, out var yr)) car.Year = yr; break;
                    case "mileage": if (int.TryParse(val, out var mi)) car.Mileage = mi; break;
                    case "fuel_type": car.FuelType = disp ?? val ?? ""; break;
                    case "gearbox": car.Transmission = disp ?? val ?? ""; break;
                    case "make": car.Brand = disp ?? val ?? ""; break;
                    case "model": car.Model = disp ?? val ?? ""; break;
                }
            }
        }

        if (node.TryGetProperty("location", out var loc) && loc.TryGetProperty("city", out var city))
            car.City = GetStr(city, "name") ?? "";

        if (DateTimeOffset.TryParse(GetStr(node, "createdAt"), out var created))
            car.ListedAt = created.ToUnixTimeSeconds();

        car.DateScraped = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return string.IsNullOrWhiteSpace(car.Title) ? null : car;
    }

    private string BuildUrl(SearchParameters p)
    {
        var brand = ScraperHelpers.BrandSlug(p.Brand);
        // Make-only path reliably returns listings; the model is filtered afterwards
        // because each site uses its own localised model slugs.
        var url = string.IsNullOrWhiteSpace(brand) ? ListingsUrl : $"{ListingsUrl}/{brand}";

        url += SupportsUndamagedFilter ? "?search%5Bfilter_enum_damaged%5D=0" : "?";

        // "created_at_first" is the real publication date; plain "created_at" is the
        // bumped date and returns stale ads first. There is no oldest-first ordering,
        // so that case is sorted from what we fetched.
        if (p.SortBy.Contains(SortOption.NewestFirst))
            url += "&search%5Border%5D=created_at_first%3Adesc";

        if (p.PriceFrom.HasValue) url += $"&search%5Bfilter_float_price%3Afrom%5D={p.PriceFrom}";
        if (p.PriceTo.HasValue) url += $"&search%5Bfilter_float_price%3Ato%5D={p.PriceTo}";
        if (p.YearFrom.HasValue) url += $"&search%5Bfilter_float_year%3Afrom%5D={p.YearFrom}";
        if (p.YearTo.HasValue) url += $"&search%5Bfilter_float_year%3Ato%5D={p.YearTo}";
        if (p.MileageTo.HasValue) url += $"&search%5Bfilter_float_mileage%3Ato%5D={p.MileageTo}";
        if (p.Page > 1) url += $"&page={p.Page}";

        return url;
    }

    /// <summary>
    /// Asks the image service for a card-sized photo. The listings quote 640x480, which
    /// is 90 KB to download and 1.1 MB once decoded into a bitmap, for a picture drawn
    /// about 200 points wide. At 320x240 it is 22 KB and 300 KB — the same picture as
    /// far as the card is concerned.
    /// </summary>
    private static string Thumbnail(string url) =>
        string.IsNullOrEmpty(url) ? url : Regex.Replace(url, @"s=\d+x\d+", "s=320x240");

    private static string? GetStr(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

}
