using System.Net.Http;

namespace CarHunt.Services.Scraping;

public class OtoMotoScraper : OlxMarketplaceScraper
{
    public OtoMotoScraper(HttpClient http) : base(http) { }

    public override string SourceName => "OtoMoto";
    protected override string ListingsUrl => "https://www.otomoto.pl/osobowe";

    public override string FilterCurrency => "PLN";
    protected override string Country => "PL";
    protected override string AcceptLanguage => "pl-PL,pl;q=0.9,en;q=0.8";
}
