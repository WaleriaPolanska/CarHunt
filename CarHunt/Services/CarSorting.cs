using System.Collections.Generic;
using System.Linq;
using CarHunt.Models;

namespace CarHunt.Services;

/// <summary>
/// Orders listings by a chain of sort keys. Shared by the search results and the saved
/// list so both answer to the same choice.
/// </summary>
public static class CarSorting
{
    public static List<Car> Apply(IEnumerable<Car> cars, IReadOnlyList<SortOption> keys)
    {
        if (keys.Count == 0) return cars.OrderBy(Price).ToList();

        IOrderedEnumerable<Car>? ordered = null;
        foreach (var key in keys)
            ordered = ordered is null ? First(cars, key) : Next(ordered, key);

        return ordered!.ToList();
    }

    /// <summary>
    /// The same ordering as <see cref="Apply"/>, as a comparison between two listings.
    /// Pages arrive one after another, and this is what lets a new listing be dropped
    /// into its place in a list already on screen instead of the list being rebuilt.
    /// </summary>
    public static IComparer<Car> ComparerFor(IReadOnlyList<SortOption> keys) =>
        Comparer<Car>.Create((a, b) =>
        {
            if (keys.Count == 0) return Price(a).CompareTo(Price(b));

            foreach (var key in keys)
            {
                var result = key switch
                {
                    SortOption.CheapestFirst => Price(a).CompareTo(Price(b)),
                    SortOption.MostExpensiveFirst => PriceDesc(b).CompareTo(PriceDesc(a)),
                    SortOption.LowestMileage => Mileage(a).CompareTo(Mileage(b)),
                    SortOption.HighestMileage => b.Mileage.CompareTo(a.Mileage),
                    SortOption.NewestFirst => b.ListedAt.CompareTo(a.ListedAt),
                    _ => ListedAt(a).CompareTo(ListedAt(b))
                };
                if (result != 0) return result;
            }

            return 0;
        });

    private static IOrderedEnumerable<Car> First(IEnumerable<Car> cars, SortOption key) => key switch
    {
        SortOption.CheapestFirst => cars.OrderBy(Price),
        SortOption.MostExpensiveFirst => cars.OrderByDescending(PriceDesc),
        SortOption.LowestMileage => cars.OrderBy(Mileage),
        SortOption.HighestMileage => cars.OrderByDescending(c => c.Mileage),
        SortOption.NewestFirst => cars.OrderByDescending(c => c.ListedAt),
        _ => cars.OrderBy(ListedAt)
    };

    private static IOrderedEnumerable<Car> Next(IOrderedEnumerable<Car> cars, SortOption key) => key switch
    {
        SortOption.CheapestFirst => cars.ThenBy(Price),
        SortOption.MostExpensiveFirst => cars.ThenByDescending(PriceDesc),
        SortOption.LowestMileage => cars.ThenBy(Mileage),
        SortOption.HighestMileage => cars.ThenByDescending(c => c.Mileage),
        SortOption.NewestFirst => cars.ThenByDescending(c => c.ListedAt),
        _ => cars.ThenBy(ListedAt)
    };

    /// <summary>
    /// Prices are compared in EUR. A zloty amount left unconverted — the rates did not
    /// download, or the listing was saved while offline — is not a euro amount, and
    /// falling back to it ranks 14 900 zł above a 12 500 € car. Those sink to the bottom
    /// instead, the same treatment an unknown mileage gets.
    /// </summary>
    private static int Price(Car c)
    {
        if (c.PriceEur > 0) return c.PriceEur;
        var isEuro = string.IsNullOrEmpty(c.Currency) ||
                     c.Currency.Equals("EUR", System.StringComparison.OrdinalIgnoreCase);
        return isEuro && c.Price > 0 ? c.Price : int.MaxValue;
    }

    /// <summary>
    /// The same value for the descending order, where an unknown price has to be the
    /// smallest rather than the largest to end up at the bottom again.
    /// </summary>
    private static int PriceDesc(Car c)
    {
        var price = Price(c);
        return price == int.MaxValue ? 0 : price;
    }

    /// <summary>
    /// A zero here means the source published nothing, not a brand-new car or the oldest
    /// listing, so those sink to the bottom instead of heading the list.
    /// </summary>
    private static int Mileage(Car c) => c.Mileage > 0 ? c.Mileage : int.MaxValue;

    private static long ListedAt(Car c) => c.ListedAt > 0 ? c.ListedAt : long.MaxValue;
}
