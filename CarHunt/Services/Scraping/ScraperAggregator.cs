using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CarHunt.Models;
using CarHunt.Services;
using CarHunt.Services.Currency;
using CarHunt.Services.Diagnostics;

namespace CarHunt.Services.Scraping;

public class ScraperAggregator
{
    private readonly IEnumerable<IScraperService> _scrapers;
    private readonly ExchangeRateService _rates;

    /// <summary>
    /// Answers already fetched, per source. Changing the sort or ticking a site off and
    /// on re-runs the search, and without this each of those went back to all four sites
    /// — measured at 1.3 s and roughly 3.4 MB per repeat, for pages that had not changed.
    /// Ordering by price or mileage happens here anyway, so a cached page serves it.
    /// </summary>
    private readonly Dictionary<string, (long At, List<Car> Cars)> _cache = new();
    // Fifteen minutes. Two was enough to cover a sort being toggled, but not a user
    // going back to a search they ran a few minutes ago — that paid for all four sites
    // again. Listings do not turn over anywhere near this fast.
    private const int CacheSeconds = 900;

    public ScraperAggregator(IEnumerable<IScraperService> scrapers, ExchangeRateService rates)
    {
        _scrapers = scrapers;
        _rates = rates;
    }

    /// <summary>
    /// What a source's answer depends on, the page number included — without it every
    /// page after the first was served page one out of the cache, every listing looked
    /// like a repeat, and paging stopped before it started.
    ///
    /// The date options are in here because two sites
    /// order by date server-side; price and mileage are ordered by us, so switching those
    /// reuses what was fetched.
    /// </summary>
    private static string CacheKey(string source, SearchParameters p)
    {
        var date = p.SortBy.Contains(SortOption.NewestFirst) ? "new"
            : p.SortBy.Contains(SortOption.OldestFirst) ? "old" : "";

        return string.Join('|', source, p.Brand, p.Model, p.YearFrom, p.YearTo, p.PriceFrom,
            p.PriceTo, p.MileageTo, p.FuelType, p.Transmission, date, p.Page).ToLowerInvariant();
    }

    private async Task<List<Car>> FetchAsync(IScraperService scraper, SearchParameters p, CancellationToken ct)
    {
        var key = CacheKey(scraper.SourceName, p);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (_cache.TryGetValue(key, out var hit) && now - hit.At < CacheSeconds)
            return hit.Cars;

        var cars = await scraper.SearchAsync(p, ct);

        // An empty answer is not cached: it means the site refused or was redesigned,
        // and the next search should ask again rather than repeat the silence.
        if (cars.Count > 0)
        {
            foreach (var stale in _cache.Where(e => now - e.Value.At >= CacheSeconds).Select(e => e.Key).ToList())
                _cache.Remove(stale);
            _cache[key] = (now, cars);
        }

        return cars;
    }

    /// <summary>How many pages deep a single search will go, per source.</summary>
    public const int MaxPages = 40;

    /// <summary>Pause between consecutive pages of the same site.</summary>
    private const int PageDelayMs = 250;

    private IEnumerable<IScraperService> ActiveSources(SearchParameters p) =>
        _scrapers.Where(s => s.SourceName switch
        {
            "AutoScout24" => p.SearchAutoScout24,
            "OtoMoto" => p.SearchOtoMoto,
            "Kleinanzeigen" => p.SearchKleinanzeigen,
            "Motogratka" => p.SearchMotogratka,
            _ => false
        });

    /// <summary>
    /// Walks every page each site has and hands back only what that round added, in
    /// order among itself. The caller can show page one immediately and let the rest
    /// arrive rather than waiting out forty pages.
    ///
    /// Deliberately the new listings and not the whole set: returning everything each
    /// round meant the caller rebuilt its list forty times over, and a list rebuilt
    /// underneath a reader is a list that blinks.
    ///
    /// A source stops when it runs out of pages, repeats listings already seen, or hits
    /// MaxPages. Pages of one site are fetched one after another with a pause: these are
    /// somebody else's servers, and Kleinanzeigen in particular watches for a client that
    /// hammers it.
    /// </summary>
    public async IAsyncEnumerable<List<Car>> SearchAllPagesAsync(
        SearchParameters parameters, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var seen = new HashSet<string>();
        var exhausted = new HashSet<string>();
        var sources = ActiveSources(parameters).ToList();

        for (var page = 1; page <= MaxPages; page++)
        {
            var live = sources.Where(s => !exhausted.Contains(s.SourceName)).ToList();
            if (live.Count == 0) yield break;

            var pageParameters = Clone(parameters, page);
            var perSourceParameters = new List<SearchParameters>();
            foreach (var s in live)
                perSourceParameters.Add(await InSiteCurrencyAsync(pageParameters, s.FilterCurrency, ct));

            var results = await Task.WhenAll(
                live.Select((s, i) => FetchAsync(s, perSourceParameters[i], ct)));

            var round = new List<List<Car>>();
            for (var i = 0; i < live.Count; i++)
            {
                // Only listings not already in hand: a site that has run out tends to
                // serve its last page again rather than an empty one.
                var fresh = results[i].Where(c => seen.Add(Identity(c))).ToList();
                if (fresh.Count == 0) { exhausted.Add(live[i].SourceName); continue; }

                foreach (var car in fresh)
                    car.PriceEur = await _rates.ToEurAsync(car.Price, car.Currency, ct);

                round.Add(fresh);
            }

            RecordFailures(live);

            if (round.Count == 0) yield break;

            yield return Order(round, parameters.SortBy);

            if (page < MaxPages) await Task.Delay(PageDelayMs, ct);
        }
    }

    /// <summary>
    /// How each source fared on the last round: the sites that failed and why. The view
    /// turns this into something the user can read, instead of a list that is quietly
    /// short a thousand cars.
    /// </summary>
    public IReadOnlyList<(string Source, ScrapeFailure Reason)> Failures { get; private set; } =
        Array.Empty<(string, ScrapeFailure)>();

    private void RecordFailures(IEnumerable<IScraperService> sources)
    {
        Failures = sources
            .Where(s => s.LastFailure is not null)
            .Select(s => (s.SourceName, s.LastFailure!.Value))
            .ToList();

        foreach (var (source, reason) in Failures)
            AppLog.Write($"{source}: {reason}");
    }

    /// <summary>The listing's address, or its details when the site published none.</summary>
    private static string Identity(Car c) =>
        !string.IsNullOrWhiteSpace(c.SourceUrl)
            ? c.SourceUrl
            : $"{c.Source}|{c.Title}|{c.Price}|{c.Mileage}";

    /// <summary>
    /// The price filter as the site understands it. Everything the user types is in euro,
    /// because that is what the results are shown in; a site quoting zloty has to be asked
    /// in zloty or it answers with cars four times cheaper than the ones requested.
    /// A rate we could not fetch drops the bound rather than sending a wrong one.
    /// </summary>
    private async Task<SearchParameters> InSiteCurrencyAsync(
        SearchParameters p, string currency, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(currency) ||
            currency.Equals("EUR", StringComparison.OrdinalIgnoreCase) ||
            (p.PriceFrom is null && p.PriceTo is null))
            return p;

        var converted = Clone(p, p.Page);
        converted.PriceFrom = await Bound(p.PriceFrom);
        converted.PriceTo = await Bound(p.PriceTo);
        return converted;

        async Task<int?> Bound(int? euro)
        {
            if (euro is null) return null;
            var local = await _rates.FromEurAsync(euro.Value, currency, ct);
            return local > 0 ? local : null;
        }
    }

    private static SearchParameters Clone(SearchParameters p, int page) => new()
    {
        Brand = p.Brand, Model = p.Model, YearFrom = p.YearFrom, YearTo = p.YearTo,
        PriceFrom = p.PriceFrom, PriceTo = p.PriceTo, MileageTo = p.MileageTo,
        FuelType = p.FuelType, Transmission = p.Transmission, SortBy = p.SortBy,
        SearchAutoScout24 = p.SearchAutoScout24, SearchOtoMoto = p.SearchOtoMoto,
        SearchKleinanzeigen = p.SearchKleinanzeigen, SearchMotogratka = p.SearchMotogratka,
        Page = page
    };

    public async Task<List<Car>> SearchAllAsync(SearchParameters parameters, CancellationToken ct = default)
    {
        var sources = ActiveSources(parameters).ToList();
        var perSourceParameters = new List<SearchParameters>();
        foreach (var s in sources)
            perSourceParameters.Add(await InSiteCurrencyAsync(parameters, s.FilterCurrency, ct));

        var results = await Task.WhenAll(
            sources.Select((s, i) => FetchAsync(s, perSourceParameters[i], ct)));
        RecordFailures(sources);
        var perSource = results.Where(r => r.Count > 0).ToList();

        // Sources quote different currencies (EUR, PLN), so sorting raw amounts would
        // compare zloty against euro. Convert first, then sort on the common value.
        foreach (var car in perSource.SelectMany(r => r))
            car.PriceEur = await _rates.ToEurAsync(car.Price, car.Currency, ct);

        return Order(perSource, parameters.SortBy);
    }

    /// <summary>
    /// Puts the sources' lists in the chosen order. Date on its own keeps the interleaving
    /// path, which orders each source by its own idea of recency; mixed with other keys it
    /// has to be a comparable number, and AutoScout24 publishes none — so those listings
    /// settle at the end.
    /// </summary>
    private static List<Car> Order(List<List<Car>> perSource, IReadOnlyList<SortOption> keys) =>
        keys.Count == 1 && IsDate(keys[0])
            ? MergeByDate(perSource, keys[0])
            : CarSorting.Apply(perSource.SelectMany(r => r), keys);

    private static bool IsDate(SortOption o) => o is SortOption.NewestFirst or SortOption.OldestFirst;

    /// <summary>
    /// AutoScout24 publishes no listing date, so its results can only be date-ordered by
    /// the site itself. Each source is therefore ordered on its own — by real date where
    /// we have one — and the lists are then interleaved, so one source can't take over
    /// the whole page.
    /// </summary>
    private static List<Car> MergeByDate(List<List<Car>> perSource, SortOption sort)
    {
        var ordered = perSource
            .Select(list => list.Any(c => c.ListedAt > 0)
                ? (sort == SortOption.OldestFirst
                    ? list.OrderBy(c => c.ListedAt).ToList()
                    : list.OrderByDescending(c => c.ListedAt).ToList())
                : list)
            .ToList();

        var merged = new List<Car>();
        for (var i = 0; merged.Count < ordered.Sum(l => l.Count); i++)
            foreach (var list in ordered)
                if (i < list.Count)
                    merged.Add(list[i]);

        return merged;
    }
}

