using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CarHunt.Models;
using CarHunt.Services;
using CarHunt.Services.Ads;
using CarHunt.Services.Currency;
using CarHunt.Services.Database;
using CarHunt.Services.Diagnostics;
using CarHunt.Services.Scraping;
using CarHunt.Services.Translation;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CarHunt.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly ScraperAggregator _aggregator;
    private readonly ITranslationService _translator;
    private readonly SqliteService _db;
    private CancellationTokenSource? _cts;
    private bool _hasSearched;

    /// <summary>Pages fetched before the user has to ask for more by scrolling.</summary>
    private const int PagesUpFront = 3;

    /// <summary>
    /// Set while a search is holding, waiting for the reader to reach the end of the
    /// list. Fetching all forty pages regardless cost 160 requests and the best part of
    /// a minute for listings nobody had scrolled to yet.
    /// </summary>
    private TaskCompletionSource<bool>? _moreWanted;

    /// <summary>Called by the view when the list is scrolled near its end.</summary>
    public void RequestMore() => _moreWanted?.TrySetResult(true);

    // The message shown over the list when a search cannot deliver what was asked for.
    // Before this the app said nothing at all: no network, a site refusing us and a
    // genuinely empty result all looked identical — the previous list simply stayed put.
    [ObservableProperty] private bool _showAlert;
    [ObservableProperty] private string _alertTitle = "";
    [ObservableProperty] private string _alertText = "";

    [RelayCommand]
    private void DismissAlert() => ShowAlert = false;

    private void Alert(string title, string text)
    {
        AlertTitle = title;
        AlertText = text;
        ShowAlert = true;
        AppLog.Write($"сообщение: {title} — {text}");
    }

    /// <summary>
    /// Decides what, if anything, the user needs to be told once a search has finished.
    /// </summary>
    private void ReportOutcome(int found)
    {
        var failures = _aggregator.Failures;
        var offline = failures.Count > 0 && failures.All(f => f.Reason == ScrapeFailure.NoConnection);

        if (offline)
        {
            Alert("No connection",
                found > 0
                    ? "These are the results from before. Check your network and search again."
                    : "Check your network and try again.");
            return;
        }

        if (failures.Count > 0 && found == 0)
        {
            Alert("The sites did not answer", Names(failures) + " returned nothing. Try again in a minute.");
            return;
        }

        if (failures.Count > 0)
        {
            Alert("Some sites are missing", $"{Names(failures)} did not answer, so their cars are not in this list.");
            return;
        }

        if (found == 0)
            Alert("Nothing found", "No cars match these filters. Try a wider price range, or fewer of them.");
    }

    private static string Names(IReadOnlyList<(string Source, ScrapeFailure Reason)> failures) =>
        failures.Count == 1
            ? failures[0].Source
            : string.Join(", ", failures.Take(failures.Count - 1).Select(f => f.Source)) +
              " and " + failures[^1].Source;

    public MainViewModel()
    {
        // Cookies are dropped on purpose. Kleinanzeigen answers the first request with a
        // full results page and a tracking cookie; hand that cookie back and every later
        // request returns HTTP 200 with a different, listing-free page. Measured: 27, 0,
        // 0, 0, 0 with cookies kept, and 27 five times running without them. So from the
        // second search onwards the source silently contributed nothing.
        //
        // The default timeout is 100 seconds, long enough for one unresponsive site to
        // freeze the whole search; a source that has not answered in fifteen is not
        // going to.
        var http = new HttpClient(new HttpClientHandler { UseCookies = false })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _aggregator = new ScraperAggregator(new IScraperService[]
        {
            new AutoScout24Scraper(http),
            new OtoMotoScraper(http),
            new KleinanzeigenScraper(http),
            new MotogratkaScraper(http)
        }, new ExchangeRateService(http));
        _translator = new TitleDictionaryTranslator();
        _db = new SqliteService();

        Results.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowResultsList));
    }

    [ObservableProperty] private string _brand = "";
    [ObservableProperty] private string _model = "";
    [ObservableProperty] private string _yearFrom = "";
    [ObservableProperty] private string _yearTo = "";
    [ObservableProperty] private string _priceFrom = "";
    [ObservableProperty] private string _priceTo = "";
    [ObservableProperty] private string _mileageTo = "";
    [ObservableProperty] private string _fuelType = string.Empty;
    [ObservableProperty] private string _transmission = "Any";
    [ObservableProperty] private bool _searchAutoScout24 = true;
    [ObservableProperty] private bool _searchOtoMoto = true;
    [ObservableProperty] private bool _searchKleinanzeigen = true;
    [ObservableProperty] private bool _searchMotogratka = true;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusText = "Enter search parameters and tap Search";
    [ObservableProperty] private bool _showFavorites;

    // In-app browser state: listings open in an embedded web view instead of
    // handing the URL to the system browser.
    [ObservableProperty] private bool _showBrowser;
    [ObservableProperty] private Uri? _browserUrl;
    [ObservableProperty] private string _browserTitle = "";

    /// <summary>Ads exist on the phone only; on desktop the slots would be empty boxes.</summary>
    public bool ShowAds { get; } = OperatingSystem.IsAndroid();

    /// <summary>The ad above the saved list, which only exists while that list is open.</summary>
    public bool ShowFavoritesAd => ShowAds && ShowFavorites;

    /// <summary>
    /// Strips left free for the banners, which are laid over the app rather than drawn by
    /// it: one along the top, a pair along the bottom.
    ///
    /// They are laid over rather than placed in the page on purpose. A banner inside the
    /// page looked better but killed the app: results arriving re-lay the page out, the
    /// native ad view is moved with it, and it does not survive that.
    /// </summary>
    public Thickness AdBannerInset { get; } =
        OperatingSystem.IsAndroid() ? new Thickness(0, 84, 0, 60) : new Thickness(0);

    /// <summary>
    /// Whether the results area is worth any room. The list is a screen tall by design —
    /// that viewport is what makes it virtualise — so before the first search it stood
    /// there empty and gave the opening page a screenful of nothing to scroll through.
    /// </summary>
    public bool ShowResultsList => Results.Count > 0 && !ShowFavorites;

    /// <summary>
    /// Listings that have arrived since the reader last looked, held back rather than
    /// shown. A later page routinely brings a cheaper car than anything on screen, and
    /// putting it where it belongs moves everything below it down — cards jump and a tap
    /// meant for a heart lands on whatever took its place. So the list only changes when
    /// the reader asks it to, and then it is sorted as a whole.
    /// </summary>
    private readonly List<Car> _pending = new();

    /// <summary>
    /// Pages already turned, for the back gesture, and pages stepped back from, so Show
    /// more returns to them instead of building a different page out of what arrived since.
    /// </summary>
    private readonly Stack<List<Car>> _pagesBehind = new();
    private readonly Stack<List<Car>> _pagesAhead = new();

    public int PendingCount => _pagesAhead.Count > 0 ? _pagesAhead.Peek().Count : _pending.Count;
    public bool HasPending => PendingCount > 0 && !ShowFavorites;
    public string PendingLabel => "Next page";

    /// <summary>The pinned button sits just above the bottom banners, in line with the page.</summary>
    public Thickness PendingButtonMargin => new(16, 6, 16, AdBannerInset.Bottom + 6);

    /// <summary>
    /// The page keeps clear of the bottom banners itself — unless the pinned button is
    /// showing, which already stands between the page and the banners.
    /// </summary>
    public Thickness ScrollPadding => HasPending
        ? new Thickness(AdBannerInset.Left, AdBannerInset.Top, AdBannerInset.Right, 0)
        : AdBannerInset;

    public ObservableCollection<Car> Results { get; } = new();
    public ObservableCollection<Car> Favorites { get; } = new();
    public IReadOnlyList<string> FuelTypes { get; } = new[] { "Doesn't matter", "Petrol", "Diesel", "Hybrid", "Electric" };

    // One per sort category; empty means the category is left out. The view binds each
    // pair of tick boxes against these by name, which is what makes the two options
    // within a frame exclude each other while still being clearable.
    [ObservableProperty] private string _priceSort = "";
    [ObservableProperty] private string _mileageSort = "";
    [ObservableProperty] private string _dateSort = "";

    /// <summary>
    /// Picks a sort option, or clears its category when the same one is ticked again.
    /// Setting the category is what unticks the opposite option, so the two behave like
    /// a radio pair while the pair as a whole can still be emptied.
    /// </summary>
    [RelayCommand]
    private void SetSort(string? option)
    {
        switch (option)
        {
            case "Cheapest first":
            case "Most expensive first":
                PriceSort = PriceSort == option ? "" : option;
                break;
            case "Lowest mileage":
            case "Highest mileage":
                MileageSort = MileageSort == option ? "" : option;
                break;
            case "Newest":
            case "Oldest":
                DateSort = DateSort == option ? "" : option;
                break;
        }
    }

    /// <summary>What the collapsed sort field reads, so the choice is visible when shut.</summary>
    public string SortSummary
    {
        get
        {
            var chosen = new[] { PriceSort, MileageSort, DateSort }
                .Where(s => !string.IsNullOrEmpty(s))
                .ToArray();
            return chosen.Length == 0 ? "Sort" : string.Join(", ", chosen);
        }
    }

    partial void OnShowFavoritesChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowFavoritesAd));
        OnPropertyChanged(nameof(ShowResultsList));
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(ScrollPadding));
    }

    partial void OnPriceSortChanged(string value) => OnSortChanged();
    partial void OnMileageSortChanged(string value) => OnSortChanged();
    partial void OnDateSortChanged(string value) => OnSortChanged();

    // Ticking a site off or on changes which listings belong in the results, so the
    // search runs again by itself instead of leaving the previous site's results on
    // screen. Same debounce as the sort options: several boxes get tapped in a row.
    private CancellationTokenSource? _sortDebounce;

    private void OnSortChanged()
    {
        OnPropertyChanged(nameof(SortSummary));
        ResortFavorites();
        _ = SearchOnSortChangeAsync();
    }

    /// <summary>
    /// Re-runs the search when the sort changes, because part of the ordering is done by
    /// the sites themselves and only a fresh request reflects it.
    ///
    /// Held back briefly: picking options across two or three categories is a handful of
    /// changes in a row, and each one would otherwise fire its own search.
    /// </summary>
    private async Task SearchOnSortChangeAsync()
    {
        // Nothing searched yet, or the saved list is open — that one reorders in place.
        if (ShowFavorites || Results.Count == 0) return;

        await DebouncedSearchAsync();
    }

    /// <summary>
    /// Waits out a short run of changes before searching, so picking three options in a
    /// row is one request rather than three.
    /// </summary>
    private async Task DebouncedSearchAsync()
    {
        _sortDebounce?.Cancel();
        _sortDebounce = new CancellationTokenSource();
        var token = _sortDebounce.Token;

        try
        {
            await Task.Delay(500, token);
            if (!token.IsCancellationRequested) await SearchAsync();
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Sort keys in the order they are applied: price, then mileage, then date. The
    /// first key decides the order and the rest only separate listings it ties on.
    ///
    /// Measured over 119 live listings: 22 shared a price and 7 a mileage, while no two
    /// dated listings shared a timestamp. A date leading would therefore never tie, and
    /// the other two categories would never get to apply at all.
    /// </summary>
    private IReadOnlyList<SortOption> BuildSortKeys()
    {
        var keys = new List<SortOption>();

        if (PriceSort == "Cheapest first") keys.Add(SortOption.CheapestFirst);
        else if (PriceSort == "Most expensive first") keys.Add(SortOption.MostExpensiveFirst);

        if (MileageSort == "Lowest mileage") keys.Add(SortOption.LowestMileage);
        else if (MileageSort == "Highest mileage") keys.Add(SortOption.HighestMileage);

        if (DateSort == "Newest") keys.Add(SortOption.NewestFirst);
        else if (DateSort == "Oldest") keys.Add(SortOption.OldestFirst);

        return keys;
    }

    // Concurrent on purpose. A search never really finishes: after the first pages it waits
    // for the reader to scroll to the end before fetching more. As a plain async command
    // that kept the command busy, and the Search button stayed disabled for the rest of the
    // session. A new run cancels the old one first thing, so letting it start is safe.
    /// <summary>
    /// Whether a listing really answers what was asked. The sites are told the filters,
    /// but they honour different ones: Kleinanzeigen takes a price and ignores year and
    /// mileage entirely, so its answers routinely include cars outside the search. Price
    /// is compared in euro, which is the one figure comparable across countries.
    ///
    /// A missing figure passes. Sellers leave year or mileage blank all the time, and
    /// dropping those would throw away good cars over an empty field.
    /// </summary>
    private static bool Matches(Car car, SearchParameters p)
    {
        var price = car.PriceEur > 0 ? car.PriceEur : 0;
        if (price > 0)
        {
            if (p.PriceFrom is { } from && price < from) return false;
            if (p.PriceTo is { } to && price > to) return false;
        }

        if (car.Year > 0)
        {
            if (p.YearFrom is { } yearFrom && car.Year < yearFrom) return false;
            if (p.YearTo is { } yearTo && car.Year > yearTo) return false;
        }

        if (car.Mileage > 0 && p.MileageTo is { } mileageTo && car.Mileage > mileageTo)
            return false;

        return true;
    }

    /// <summary>
    /// A number as people type it here: "10 000", "10.000", "10000 €". Plain int.TryParse
    /// turned every one of those into no filter at all, without a word — a price floor that
    /// silently did nothing. Only the digits count; a field with none is left unset.
    /// </summary>
    private static int? Number(string? text)
    {
        var digits = new string((text ?? "").Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : null;
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SearchAsync()
    {
        // Ticking a site only records the choice; the search runs when Search is pressed.
        // It used to re-run on every tick, which showed a list before the reader had
        // finished choosing and went round the Search button altogether.
        if (!SearchAutoScout24 && !SearchOtoMoto && !SearchKleinanzeigen && !SearchMotogratka)
        {
            StatusText = "Pick at least one site";
            return;
        }

        _cts?.Cancel();
        // Held in a local as well as the field: the steps below await, and by the time
        // they resume the field may already point at a newer search. Reading _cts.Token
        // after that hands the older run the *new*, uncancelled token, so it carries on
        // and empties its own results into the list the newer search is filling — two
        // orderings interleaved, which reads as no sorting at all.
        var cts = new CancellationTokenSource();
        _cts = cts;
        var token = cts.Token;

        // Searching is about the results, so step off the saved list rather than
        // running a search the user cannot see.
        ShowFavorites = false;

        // The very first search of the session brings a full-screen ad; later ones do not.
        if (!_hasSearched) FullScreenAd.SearchStarted();

        StatusText = "Searching...";
        ShowAlert = false;
        Results.Clear();
        _pending.Clear();
        _pagesBehind.Clear();
        _pagesAhead.Clear();
        NotifyPending();

        // The first search of a run brings a full-screen ad, on its way to the results.
        if (!_hasSearched) FullScreenAd.SearchStarted();
        _hasSearched = true;

        var parameters = new SearchParameters
        {
            Brand = Brand,
            Model = Model,
            YearFrom = Number(YearFrom),
            YearTo = Number(YearTo),
            PriceFrom = Number(PriceFrom),
            PriceTo = Number(PriceTo),
            MileageTo = Number(MileageTo),
            FuelType = string.IsNullOrWhiteSpace(FuelType) || FuelType == "Doesn't matter" ? "" : FuelType,
            Transmission = Transmission == "Any" ? "" : Transmission,
            SearchAutoScout24 = SearchAutoScout24,
            SearchOtoMoto = SearchOtoMoto,
            SearchKleinanzeigen = SearchKleinanzeigen,
            SearchMotogratka = SearchMotogratka,
            SortBy = BuildSortKeys()
        };

        try
        {
            // Hold the overlay back until the button's press animation has played;
            // raising it immediately hides the button before the user sees it move.
            await Task.Delay(260, token);
            IsLoading = true;

            var favoriteKeys = await _db.GetFavoriteKeysAsync();
            var page = 0;

            // Each round brings only what that page added, already in order among itself.
            // The first is shown; the rest waits behind a button, because merging it in
            // is what moves the list under the reader's hands.
            await foreach (var cars in _aggregator.SearchAllPagesAsync(parameters, token))
            {
                token.ThrowIfCancellationRequested();
                page++;

                foreach (var car in cars)
                {
                    if (!Matches(car, parameters)) continue;

                    car.IsFavorite = favoriteKeys.Contains(SqliteService.BuildKey(car));
                    if (string.IsNullOrEmpty(car.TranslatedTitle))
                        car.TranslatedTitle = await _translator.TranslateToEnglishAsync(car.Title, "auto", token);

                    // The first round has nothing to disturb, so it goes straight on screen.
                    if (page == 1) Results.Add(car);
                    else _pending.Add(car);
                }

                NotifyPending();

                // The overlay comes down on the first page; the rest fills in underneath
                // a list the user can already read and scroll.
                IsLoading = false;
                StatusText = $"{Results.Count} cars, page {page} of each site...";

                // Judged on the first page and not at the end: the end only comes when the
                // reader stops asking for more, and a site that refused us is news now.
                if (page == 1) ReportOutcome(Results.Count);

                if (page < PagesUpFront) continue;

                // Hold here until the reader reaches the end of what is already loaded.
                _moreWanted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (token.Register(() => _moreWanted.TrySetResult(false)))
                    if (!await _moreWanted.Task) break;
                _moreWanted = null;
            }

            StatusText = $"Found {Results.Count} cars";

            // Nothing arrived at all, so the first page never got to speak.
            if (page == 0) ReportOutcome(0);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_cts, cts)) StatusText = "Search cancelled";
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_cts, cts)) return;
            StatusText = $"Error: {ex.Message}";
            AppLog.Write($"поиск не удался: {ex.GetType().Name} {ex.Message}");
            Alert("Search failed", "Something went wrong on our side. Try again.");
        }
        finally
        {
            // Only the search still in charge may take the overlay down; a superseded
            // one finishing later would otherwise clear it while the new one is running.
            if (ReferenceEquals(_cts, cts)) IsLoading = false;
        }
    }

    /// <summary>
    /// Turns to a new page made of everything that arrived while the reader was looking,
    /// sorted among itself. The cars already read are not mixed back in: the reader asked
    /// for what is new, not for the same list reshuffled. Done only on the reader's tap,
    /// so nothing moves under a finger on its own.
    /// </summary>
    [RelayCommand]
    private void ShowPending()
    {
        List<Car> page;
        if (_pagesAhead.Count > 0)
        {
            page = _pagesAhead.Pop();
        }
        else if (_pending.Count > 0)
        {
            page = CarSorting.Apply(_pending.ToList(), BuildSortKeys());
            _pending.Clear();
        }
        else return;

        _pagesBehind.Push(Results.ToList());
        ShowPage(page);
    }

    /// <summary>
    /// The back gesture on the results: the page before this one, exactly as it was left.
    /// Returns false when there is none, so the gesture can go on to do what it normally does.
    /// </summary>
    public bool GoToPreviousPage()
    {
        if (_pagesBehind.Count == 0) return false;

        _pagesAhead.Push(Results.ToList());
        ShowPage(_pagesBehind.Pop());
        return true;
    }

    private void ShowPage(List<Car> page)
    {
        Results.Clear();
        foreach (var car in page) Results.Add(car);

        NotifyPending();
        StatusText = $"{Results.Count} cars";
    }

    private void NotifyPending()
    {
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(PendingLabel));
        OnPropertyChanged(nameof(ScrollPadding));
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync(Car car)
    {
        await _db.ToggleFavoriteAsync(car);
        // Refresh rather than re-running the command, which would close the open list.
        if (ShowFavorites) await RefreshFavoritesAsync();
    }

    /// <summary>
    /// Tapping the notepad folds the list open and away. Deliberately synchronous: as an
    /// async command the toggle sat behind the database read, and AsyncRelayCommand drops
    /// a second invocation while the first is still running — so quick taps went missing
    /// and the list ended up out of step with them. Loading follows on its own.
    /// </summary>
    [RelayCommand]
    private void ToggleFavorites()
    {
        ShowFavorites = !ShowFavorites;
        if (ShowFavorites) _ = RefreshFavoritesAsync();
    }

    private async Task RefreshFavoritesAsync()
    {
        var favs = await _db.GetFavoritesAsync();

        Favorites.Clear();
        foreach (var f in CarSorting.Apply(favs, BuildSortKeys())) Favorites.Add(f);
    }

    /// <summary>
    /// Reorders the saved list in place. It is already in memory, so unlike the search
    /// results it does not need fetching again to answer a new sort choice.
    /// </summary>
    private void ResortFavorites()
    {
        if (!ShowFavorites || Favorites.Count == 0) return;

        var sorted = CarSorting.Apply(Favorites.ToList(), BuildSortKeys());
        Favorites.Clear();
        foreach (var f in sorted) Favorites.Add(f);
    }

    [ObservableProperty] private string _shareLabel = "Share list";

    // A clipboard carries plain text, which has no font and no bold. These are Unicode
    // bold letters, the only styling that survives into an arbitrary messenger.
    private const string BoldAppName = "\U0001D5D6\U0001D5EE\U0001D5FF\U0001D5DB\U0001D602\U0001D5FB\U0001D601"; // CarHunt
    private const string PlayStoreUrl = "https://play.google.com/store/apps/details?id=com.carhunt.app";

    /// <summary>
    /// The saved listings as plain text, so the list can be pasted into any messenger.
    /// Listings with no URL are skipped — a link is the whole point of sharing.
    /// </summary>
    public string BuildFavoritesShareText()
    {
        var listings = new StringBuilder();

        foreach (var car in Favorites)
        {
            if (string.IsNullOrWhiteSpace(car.SourceUrl)) continue;

            var title = string.IsNullOrWhiteSpace(car.TranslatedTitle) ? car.Title : car.TranslatedTitle;
            listings.Append("\n\n").Append(title);
            if (car.PriceEur > 0 || car.Price > 0) listings.Append(" — ").Append(car.PriceDisplay);
            listings.Append('\n').Append(car.SourceUrl);
        }

        if (listings.Length == 0) return "";

        return new StringBuilder(BoldAppName).Append(" — my picks")
            .Append(listings)
            .Append("\n\nFind your own car with CarHunt:\n").Append(PlayStoreUrl)
            .ToString();
    }

    /// <summary>Briefly confirms on the button itself, since there is no status area.</summary>
    public async Task FlashSharedAsync()
    {
        ShareLabel = "Copied!";
        await Task.Delay(1800);
        ShareLabel = "Share list";
    }

    [RelayCommand]
    private void ShowResults() => ShowFavorites = false;

    [RelayCommand]
    private void OpenCar(Car car)
    {
        if (!Uri.TryCreate(car.SourceUrl, UriKind.Absolute, out var uri)) return;

        BrowserTitle = string.IsNullOrWhiteSpace(car.TranslatedTitle) ? car.Title : car.TranslatedTitle;
        BrowserUrl = uri;
        ShowBrowser = true;

        // Every fifth listing brings a full-screen ad. It is asked for after the listing
        // is opened, so the page is already loading behind it and the user loses nothing
        // if no ad is ready.
        FullScreenAd.ListingOpened();
    }

    [RelayCommand]
    private void CloseBrowser()
    {
        ShowBrowser = false;
        BrowserUrl = null; // stop the page so it isn't left running behind the list
    }
}
