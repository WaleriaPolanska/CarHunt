using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Web;
using AngleSharp.Dom;
using CarHunt.Models;

namespace CarHunt.Services.Scraping;

/// <summary>
/// Germany's largest private-seller marketplace, and the source of the cheapest cars in
/// the results.
/// </summary>
public class KleinanzeigenScraper : HtmlCardScraper
{
    public KleinanzeigenScraper(HttpClient http) : base(http) { }

    public override string SourceName => "Kleinanzeigen";
    protected override string BaseUrl => "https://www.kleinanzeigen.de";
    protected override string Country => "DE";
    protected override string Currency => "EUR";
    protected override string AcceptLanguage => "de-DE,de;q=0.9,en;q=0.8";
    // The site moved to a new front end in 2026: the old "aditem" markup is gone and the
    // classes around the cards are generated utility names that change on their own.
    // Only the article and its data attributes are stable, so everything else is read by
    // what the text looks like rather than by where it sits.
    protected override string CardSelector => "article[data-adid]";

    protected override void ReadCard(IElement card, Car car)
    {
        car.Title = Text(card, "h3");
        car.SourceUrl = ScraperHelpers.NormalizeUrl(card.GetAttribute("data-href"), BaseUrl);
        car.ImageUrl = ScraperHelpers.ExtractImage(card, BaseUrl);

        // A reduced ad shows the old price struck through next to the current one.
        var price = card.QuerySelectorAll("p")
            .FirstOrDefault(e => e.TextContent.Contains('€') &&
                                 !(e.GetAttribute("class") ?? "").Contains("line-through"));
        car.Price = ScraperHelpers.ExtractPrice(price?.TextContent ?? "");

        // The place reads "49124 Georgsmarienhütte" - a five-digit postcode, then the town.
        car.City = card.QuerySelectorAll("span")
            .Select(e => e.TextContent.Trim())
            .FirstOrDefault(t => t.Length > 6 && t[5] == ' ' && t.Take(5).All(char.IsDigit)) ?? "";

        // Tags under the price, e.g. "198.500 km" and "EZ 11/2010".
        var tags = string.Join(" ", card.QuerySelectorAll("span")
            .Select(e => e.TextContent.Trim())
            .Where(t => t.EndsWith("km") || t.StartsWith("EZ")));
        car.Mileage = ScraperHelpers.ExtractMileage(tags);
        car.Year = ScraperHelpers.ExtractYear(tags);
    }

    protected override string BuildUrl(SearchParameters p)
    {
        // c216 is the cars category; the make goes in the path as a keyword.
        var brand = HttpUtility.UrlEncode(ScraperHelpers.BrandSlug(p.Brand));
        // The page number is a path segment, not a query parameter: /s-autos/seite:2/bmw/k0c216
        var page = p.Page > 1 ? $"seite:{p.Page}/" : "";
        var path = string.IsNullOrEmpty(brand)
            ? $"{BaseUrl}/s-autos/{page}c216"
            : $"{BaseUrl}/s-autos/{page}{brand}/k0c216";

        // Only the bounds that were given: an empty maxPrice in the query made the site
        // drop the price filter altogether, so a lower bound on its own did nothing.
        var bounds = new List<string> { "priceType=FIXED" };
        if (p.PriceFrom.HasValue) bounds.Add($"minPrice={p.PriceFrom}");
        if (p.PriceTo.HasValue) bounds.Add($"maxPrice={p.PriceTo}");

        return bounds.Count == 1 ? path : $"{path}?{string.Join('&', bounds)}";
    }
}
