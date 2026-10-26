using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Web;
using AngleSharp.Dom;
using CarHunt.Models;

namespace CarHunt.Services.Scraping;

/// <summary>
/// The car section of Gratka, a Polish marketplace carrying the cheap end of the market.
/// </summary>
public class MotogratkaScraper : HtmlCardScraper
{
    public MotogratkaScraper(HttpClient http) : base(http) { }

    public override string SourceName => "Motogratka";
    // motogratka.pl, not gratka.pl: the latter serves page one whatever page is asked
    // for, so paging through gratka.pl silently returned the same 32 listings over again.
    protected override string BaseUrl => "https://motogratka.pl";
    protected override string Country => "PL";
    protected override string Currency => "PLN";
    protected override string AcceptLanguage => "pl-PL,pl;q=0.9,en;q=0.8";
    protected override string CardSelector => "article.teaserUnified";

    protected override void ReadCard(IElement card, Car car)
    {
        car.Title = Text(card, ".teaserUnified__title");
        car.City = Text(card, ".teaserUnified__location");
        car.Price = ParsePrice(Text(card, ".teaserUnified__price"));
        car.ImageUrl = ScraperHelpers.ExtractImage(card, BaseUrl);
        // The whole card is the link: it carries role="link" and data-href rather than
        // wrapping an <a>, so querying for an anchor finds nothing.
        car.SourceUrl = ScraperHelpers.NormalizeUrl(card.GetAttribute("data-href"), BaseUrl);

        // Details come as "key: value" lines, e.g. "Przebieg: 85200".
        var details = string.Join(" ", card.QuerySelectorAll(".teaserUnified__listItem").Select(e => e.TextContent));
        car.Mileage = ValueAfter(details, "Przebieg");
        // Only about half the cards state a build year, and the title carries one even
        // less often — so the title is the fallback, not the source.
        car.Year = ValueAfter(details, "Rok produkcji");
        if (car.Year == 0) car.Year = ScraperHelpers.ExtractYear(car.Title);
    }

    /// <summary>Prices read "126 900 zł", with spaces grouping the thousands.</summary>
    private static int ParsePrice(string text)
    {
        var digits = new string(text.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var value) ? value : 0;
    }

    private static int ValueAfter(string details, string key)
    {
        var m = Regex.Match(details, key + @"\s*:\s*([\d\s]+)");
        if (!m.Success) return 0;
        var digits = new string(m.Groups[1].Value.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var value) ? value : 0;
    }

    protected override string BuildUrl(SearchParameters p)
    {
        var brand = HttpUtility.UrlEncode((p.Brand ?? "").Trim().ToLower());
        var url = string.IsNullOrEmpty(brand)
            ? $"{BaseUrl}/motoryzacja/osobowe"
            : $"{BaseUrl}/motoryzacja/osobowe/{brand}";

        // Filters are "field:min" / "field:max" with the colon percent-encoded. The
        // od/do spelling used before was accepted and silently ignored: asking for
        // 0-10,000 zl returned the same 32 listings priced 14,999 to 310,500.
        var query = new List<string>();
        if (p.PriceFrom.HasValue) query.Add($"cena-calkowita%3Amin={p.PriceFrom}");
        if (p.PriceTo.HasValue) query.Add($"cena-calkowita%3Amax={p.PriceTo}");
        if (p.YearFrom.HasValue) query.Add($"rok-produkcji%3Amin={p.YearFrom}");
        if (p.YearTo.HasValue) query.Add($"rok-produkcji%3Amax={p.YearTo}");
        if (p.MileageTo.HasValue) query.Add($"przebieg%3Amax={p.MileageTo}");
        if (p.Page > 1) query.Add($"page={p.Page}");

        return query.Count == 0 ? url : $"{url}?{string.Join("&", query)}";
    }
}
