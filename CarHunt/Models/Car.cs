using SQLite;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CarHunt.Models;

public class Car : INotifyPropertyChanged
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }
    public string Title { get; set; } = "";
    private string _translatedTitle = "";
    /// <summary>Filled in after the list is already on screen, so it has to notify.</summary>
    public string TranslatedTitle
    {
        get => _translatedTitle;
        set
        {
            if (_translatedTitle == value) return;
            _translatedTitle = value;
            OnPropertyChanged();
        }
    }
    public string Description { get; set; } = "";
    public string TranslatedDescription { get; set; } = "";
    public string Brand { get; set; } = "";
    public string Model { get; set; } = "";
    public int Year { get; set; }
    public int Price { get; set; }
    public string Currency { get; set; } = "EUR";
    /// <summary>Price converted to EUR, or 0 when no rate was available.</summary>
    public int PriceEur { get; set; }

    /// <summary>Main price shown in the list: EUR, so all sources are comparable.</summary>
    public string PriceDisplay => PriceEur > 0 ? $"€ {PriceEur:N0}" : FormatOriginalPrice();

    /// <summary>The listing's own price, shown alongside when it isn't already EUR.</summary>
    public string OriginalPriceDisplay =>
        PriceEur > 0 && !Currency.Equals("EUR", System.StringComparison.OrdinalIgnoreCase)
            ? FormatOriginalPrice()
            : "";

    private string FormatOriginalPrice() => Currency switch
    {
        "PLN" => $"{Price:N0} zł",
        "EUR" or "" => $"€ {Price:N0}",
        _ => $"{Price:N0} {Currency}"
    };
    /// <summary>
    /// The year as shown in the list. Not every site states one — Motogratka publishes it
    /// on about half its cards — and an unknown year printed as "0" reads as a fact about
    /// the car rather than a gap in the listing.
    /// </summary>
    public string YearDisplay => Year > 0 ? Year.ToString() : "";

    public int Mileage { get; set; }
    public string FuelType { get; set; } = "";
    public string Transmission { get; set; } = "";
    public string Country { get; set; } = "";
    public string City { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string Source { get; set; } = ""; // AutoScout24, OtoMoto
    public string ImageUrl { get; set; } = "";
    public string ImageUrls { get; set; } = ""; // JSON array
    [Indexed]
    public string DedupeKey { get; set; } = ""; // stable identity used to de-duplicate favourites
    private bool _isFavorite;
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value) return;
            _isFavorite = value;
            OnPropertyChanged();
        }
    }
    public long DateScraped { get; set; }
    /// <summary>When the ad was published, as a unix timestamp. 0 when the source doesn't publish it.</summary>
    public long ListedAt { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

