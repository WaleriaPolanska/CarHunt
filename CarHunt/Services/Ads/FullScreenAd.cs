using System;

namespace CarHunt.Services.Ads;

/// <summary>Which of the two full-screen ads is wanted.</summary>
public enum FullScreenSlot
{
    /// <summary>Shown once a run, after the first search.</summary>
    FirstSearch,

    /// <summary>Shown on every fifth listing opened.</summary>
    Listing
}

/// <summary>
/// The full-screen ads.
///
/// The app itself knows nothing about ads — it reports what the user did, and the platform
/// head decides whether anything appears. On desktop nothing does.
/// </summary>
public static class FullScreenAd
{
    /// <summary>Listings opened between one full-screen ad and the next.</summary>
    private const int EveryListings = 5;

    private static int _opened;
    private static bool _searchShown;

    /// <summary>Shows the ad for a slot. Set by the app head; null everywhere else.</summary>
    public static Action<FullScreenSlot>? Show { get; set; }

    /// <summary>
    /// Called when a search starts. Only the first search of a run brings an ad: a full
    /// screen in the way of every search would be a good way to lose the user.
    /// </summary>
    public static void SearchStarted()
    {
        if (_searchShown) return;
        _searchShown = true;
        Show?.Invoke(FullScreenSlot.FirstSearch);
    }

    /// <summary>
    /// Called when the user opens a listing. Every fifth one asks for the ad; the listing
    /// opens either way, so a missing or unloaded ad costs the user nothing.
    /// </summary>
    public static void ListingOpened()
    {
        if (++_opened % EveryListings != 0) return;
        Show?.Invoke(FullScreenSlot.Listing);
    }
}
