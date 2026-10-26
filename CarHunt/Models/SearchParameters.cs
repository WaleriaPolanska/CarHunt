using System.Collections.Generic;

namespace CarHunt.Models;

public enum SortOption
{
    CheapestFirst,
    MostExpensiveFirst,
    LowestMileage,
    HighestMileage,
    NewestFirst,
    OldestFirst
}

public class SearchParameters
{
    /// <summary>
    /// Sort keys in priority order: the first orders the list, the rest only break ties
    /// within it. Empty means cheapest first.
    /// </summary>
    public IReadOnlyList<SortOption> SortBy { get; set; } = new List<SortOption>();

    public string Brand { get; set; } = "";
    public string Model { get; set; } = "";
    public int? YearFrom { get; set; }
    public int? YearTo { get; set; }
    public int? PriceFrom { get; set; }
    public int? PriceTo { get; set; }
    public int? MileageFrom { get; set; }
    public int? MileageTo { get; set; }
    public string FuelType { get; set; } = ""; // Petrol, Diesel, Hybrid, Electric, Any
    public string Transmission { get; set; } = ""; // Manual, Automatic, Any
    public string Country { get; set; } = ""; // DE, PL, IT, FR, NL, Any

    /// <summary>Which page of each site's results to fetch. 1 is the first.</summary>
    public int Page { get; set; } = 1;

    public bool SearchAutoScout24 { get; set; } = true;
    public bool SearchOtoMoto { get; set; } = true;
    public bool SearchKleinanzeigen { get; set; } = true;
    public bool SearchMotogratka { get; set; } = true;
}

