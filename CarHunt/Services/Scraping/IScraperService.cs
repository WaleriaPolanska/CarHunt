using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CarHunt.Models;

namespace CarHunt.Services.Scraping;

public interface IScraperService
{
    string SourceName { get; }

    /// <summary>
    /// Currency this site quotes its prices in. The price filter is typed in euro, so it
    /// has to be converted before it goes into the site's own query — 10 000 handed to a
    /// Polish site unchanged means 10 000 zloty, about a quarter of what was asked for.
    /// </summary>
    string FilterCurrency { get; }

    /// <summary>
    /// Why the last search returned nothing, or null when it went through. Scrapers
    /// swallow their exceptions so one dead site cannot take the others down with it —
    /// which also meant a dead site was indistinguishable from a site with no cars.
    /// </summary>
    ScrapeFailure? LastFailure { get; }

    Task<List<Car>> SearchAsync(SearchParameters parameters, CancellationToken ct = default);
}

/// <summary>What went wrong, in the terms a person cares about.</summary>
public enum ScrapeFailure
{
    /// <summary>The request never reached the site: no network, or DNS failed.</summary>
    NoConnection,

    /// <summary>The site answered, but with a refusal or an error.</summary>
    SiteRefused,

    /// <summary>The site answered normally and the page held no listings we could read.</summary>
    NothingReadable
}
