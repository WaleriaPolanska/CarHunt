using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using CarHunt.Models;

namespace CarHunt.Services.Scraping;

/// <summary>
/// Base for sites that publish no structured data, so their listings have to be read
/// out of the markup card by card. Kleinanzeigen and Motogratka differ only in the
/// address, the card selector and which element holds which field — everything around
/// that was the same code twice.
///
/// Reading markup means these sources fall silent rather than fail when a site is
/// redesigned: the selector matches nothing and the result is an empty list.
/// </summary>
public abstract class HtmlCardScraper : IScraperService
{
    private readonly HttpClient _http;

    protected HtmlCardScraper(HttpClient http) => _http = http;

    public abstract string SourceName { get; }

    public ScrapeFailure? LastFailure { get; private set; }

    /// <summary>Site root, used to turn relative links and images into absolute ones.</summary>
    protected abstract string BaseUrl { get; }

    /// <summary>Two-letter country the ads are published in.</summary>
    protected abstract string Country { get; }

    /// <summary>Currency the prices are quoted in.</summary>
    protected abstract string Currency { get; }

    public string FilterCurrency => Currency;

    protected abstract string AcceptLanguage { get; }

    /// <summary>CSS selector matching one listing card.</summary>
    protected abstract string CardSelector { get; }

    protected abstract string BuildUrl(SearchParameters p);

    /// <summary>Fills the fields this site publishes from one card.</summary>
    protected abstract void ReadCard(IElement card, Car car);

    public async Task<List<Car>> SearchAsync(SearchParameters p, CancellationToken ct = default)
    {
        var cars = new List<Car>();

        try
        {
            // The page text is scoped to this call so it can be collected as soon as the
            // cards are parsed; held alongside the DOM it doubled the peak for nothing.
            var cards = await LoadCardsAsync(p, ct);

            foreach (var card in cards)
            {
                var car = new Car { Source = SourceName, Country = Country, Currency = Currency };
                ReadCard(card, car);

                car.Brand = p.Brand;
                car.Model = p.Model;
                car.DateScraped = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                // A listing with no price is a swap or a "make me an offer" post; it
                // cannot be compared or sorted alongside the rest.
                if (!string.IsNullOrWhiteSpace(car.Title) && car.Price > 0)
                    cars.Add(car);
            }

            cars = ScraperHelpers.FilterByModel(cars, p.Model);
            LastFailure = cars.Count > 0 ? null : ScrapeFailure.NothingReadable;
        }
        catch (Exception e)
        {
            LastFailure = ScraperHelpers.Classify(e);
        }

        return cars;
    }

    /// <summary>
    /// Downloads the page and returns its listing cards. Parsed with HtmlParser rather
    /// than a BrowsingContext: a browsing context carries a document loader, cookies and
    /// a request pipeline that nothing here uses, and measured on a 372 KB page it cost
    /// 5.5 MB against 5.0 MB for the same 27 cards.
    /// </summary>
    private async Task<List<IElement>> LoadCardsAsync(SearchParameters p, CancellationToken ct)
    {
        var html = await ScraperHelpers.FetchHtmlAsync(_http, BuildUrl(p), AcceptLanguage, ct);
        var doc = new HtmlParser().ParseDocument(html);
        return doc.QuerySelectorAll(CardSelector).ToList();
    }

    /// <summary>Text of the first element matching <paramref name="selector"/>, trimmed.</summary>
    protected static string Text(IElement card, string selector) =>
        card.QuerySelector(selector)?.TextContent.Trim() ?? "";
}
