using Android.App;
using Android.Content.PM;
using Google.Android.Gms.Ads;
using Google.Android.Gms.Ads.Interstitial;
using Google.Android.Gms.Ads.NativeAd;
using CarHunt.Services.Ads;
using CarHunt.Views;
using Android.OS;
using Android.Views;
using Android.Widget;
using Avalonia;
using System.Collections.Generic;
using Avalonia.Android;
using Avalonia.Platform;

namespace CarHunt.Android;

[Activity(
    Label = "CarHunt",
    Theme = "@style/MyTheme.NoActionBar",
    // The launcher shows the activity's own icon when it has one, so this has to be the
    // adaptive icon too — otherwise the square drawable wins and lands in a white circle.
    Icon = "@mipmap/ic_launcher",
    RoundIcon = "@mipmap/ic_launcher_round",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    /// <summary>
    /// The ad units the banners and the full-screen ad are filled from.
    ///
    /// Debug builds use Google's test units and nothing else: serving live ads to your own
    /// device — or worse, clicking one — is what gets an AdMob account suspended. They
    /// behave identically in every other way.
    ///
    /// Slot 0 is the strip along the top; slots 1 and 2 share the strip along the bottom,
    /// half the width each. All are laid over the app, so they stay put on every screen —
    /// including the built-in browser.
    /// </summary>
#if DEBUG
    private static string BannerUnit(int slot) => "ca-app-pub-3940256099942544/6300978111";
    private static string FullScreenUnit(FullScreenSlot slot) => "ca-app-pub-3940256099942544/1033173712";
    private const string NativeUnit = "ca-app-pub-3940256099942544/2247696110";
#else
    private static string BannerUnit(int slot) => slot switch
    {
        0 => "ca-app-pub-3940256099942544/6300978111",   // полоса сверху
        1 => "ca-app-pub-3940256099942544/6300978111",   // низ слева
        _ => "ca-app-pub-3940256099942544/6300978111"    // низ справа
    };
    private static string FullScreenUnit(FullScreenSlot slot) => slot == FullScreenSlot.FirstSearch
        ? "ca-app-pub-3940256099942544/1033173712"   // после первого поиска
        : "ca-app-pub-3940256099942544/1033173712";  // каждое пятое объявление
    private const string NativeUnit = "ca-app-pub-3940256099942544/2247696110"; // карточка в избранном
#endif

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        MobileAds.Initialize(this);

        AdSlot.Factory = slot => new AndroidViewControlHandle(
            slot == NativeSlot ? NativeCard() : BannerFor(slot));

        // Two banners, built once and handed to the two slots the page keeps under the
        // search button. Nothing recycles them: they live as long as the screen does.
        foreach (var slot in new[] { FullScreenSlot.FirstSearch, FullScreenSlot.Listing })
            LoadFullScreen(slot);

        FullScreenAd.Show = slot => RunOnUiThread(() => ShowFullScreen(slot));

        // Slot 10 is the ad among the saved listings — a native one, built from its parts.
        // It is the only ad that lives inside the page, and only because that list is
        // short and does not recycle its rows.
        AdSlot.Factory = slot => slot == 10
            ? new AndroidViewControlHandle(new NativeAdSlot(this, NativeUnit))
            : null;

        // Two banners side by side along the bottom, laid over the app: Avalonia draws
        // itself into one surface, so they cannot be part of the page.
        var content = FindViewById<FrameLayout>(global::Android.Resource.Id.Content);
        if (content is null) return;

        // Sizes are spelled out in pixels rather than left to layout weights: a banner
        // measured at zero width reports zero height too, and the whole strip collapsed.
        var density = Resources!.DisplayMetrics!.Density;
        var heightPx = (int)(50 * density);

        // A banner pinned along the top. Laid over the app like the pair below: nothing
        // moves it, which is what the ones inside the page could not survive. It starts
        // below the status bar, or it would cover the clock and the battery.
        var statusBar = Resources.GetIdentifier("status_bar_height", "dimen", "android") is var id and > 0
            ? Resources.GetDimensionPixelSize(id)
            : (int)(24 * density);

        content.AddView(BannerFor(0), new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, heightPx)
        {
            Gravity = GravityFlags.Top | GravityFlags.CenterHorizontal,
            TopMargin = statusBar
        });

        // Two banners sharing the bottom, half the width each. Sizes are spelled out in
        // pixels rather than left to layout weights: a banner measured at zero width
        // reports zero height too, and the strip collapsed.
        var halfPx = Resources.DisplayMetrics.WidthPixels / 2;

        var strip = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        foreach (var slot in new[] { 1, 2 })
            strip.AddView(BannerFor(slot), new LinearLayout.LayoutParams(halfPx, heightPx));

        content.AddView(strip, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, heightPx)
        {
            Gravity = GravityFlags.Bottom | GravityFlags.CenterHorizontal
        });
    }

    /// <summary>
    /// The banner for one slot, created on first use and kept for as long as the screen.
    /// </summary>
    private AdView BannerFor(int slot)
    {
        if (_banners.TryGetValue(slot, out var existing)) return existing;

        var screen = (int)(Resources!.DisplayMetrics!.WidthPixels / Resources.DisplayMetrics.Density);

        // Slot 0 spans the page; slots 1 and 2 share the bottom strip, so each takes half.
        // A half-width banner has to be a size of our own: the adaptive helper refuses
        // anything under 320 points and hands back a banner that never fills.
        // The bottom pair needs a size of our own: the adaptive helper refuses anything
        // under 320 points and hands back a banner that never fills.
        var size = slot == 0
            ? AdSize.GetCurrentOrientationAnchoredAdaptiveBannerAdSize(this, screen)
            : new AdSize(screen / 2 - 2, 50);

        var banner = new AdView(this) { AdSize = size, AdUnitId = BannerUnit(slot) };
        banner.LoadAd(new AdRequest.Builder().Build());

        _banners[slot] = banner;
        return banner;
    }

    private readonly Dictionary<int, AdView> _banners = new();

    /// <summary>
    /// Keeps one full-screen ad ready. It has to be loaded well before it is wanted —
    /// asking at the moment of showing means nothing appears — so a new one is fetched as
    /// soon as the last was dismissed.
    ///
    /// The close button and its delay belong to Google, not to us: the app cannot set them.
    /// </summary>
    private readonly Dictionary<FullScreenSlot, InterstitialAd?> _fullScreen = new();

    private void LoadFullScreen(FullScreenSlot slot) =>
        InterstitialAd.Load(this, FullScreenUnit(slot), new AdRequest.Builder().Build(),
            new FullScreenLoader(this, slot));

    private void ShowFullScreen(FullScreenSlot slot)
    {
        if (!_fullScreen.TryGetValue(slot, out var ad) || ad is null) return;

        ad.Show(this);
        _fullScreen[slot] = null;

        // The next one starts loading straight away, ready for when this slot comes round
        // again — asking at the moment of showing means nothing appears.
        LoadFullScreen(slot);
    }

    private sealed class FullScreenLoader : InterstitialAdLoadCallback
    {
        private readonly MainActivity _activity;
        private readonly FullScreenSlot _slot;

        public FullScreenLoader(MainActivity activity, FullScreenSlot slot)
        {
            _activity = activity;
            _slot = slot;
        }

        public override void OnAdLoaded(InterstitialAd ad) =>
            _activity._fullScreen[_slot] = ad;

        public override void OnAdFailedToLoad(LoadAdError error) =>
            _activity._fullScreen[_slot] = null;
    }

    /// <summary>The slot number the page uses for the native card in the saved list.</summary>
    private const int NativeSlot = 10;

    private NativeAdView? _nativeCard;

    /// <summary>
    /// A native ad, laid out here rather than in the page: Google measures views and
    /// clicks itself, and only counts them inside its own NativeAdView, so the pieces
    /// have to be its children. The card is built empty and filled when an ad arrives.
    /// </summary>
    private NativeAdView NativeCard()
    {
        if (_nativeCard is not null) return _nativeCard;

        var density = Resources!.DisplayMetrics!.Density;
        int Dp(double value) => (int)(value * density);

        var card = new NativeAdView(this);
        card.SetBackgroundColor(global::Android.Graphics.Color.ParseColor("#141414"));

        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.SetPadding(Dp(10), Dp(10), Dp(10), Dp(10));

        var icon = new ImageView(this);
        row.AddView(icon, new LinearLayout.LayoutParams(Dp(56), Dp(56)));

        var column = new LinearLayout(this) { Orientation = Orientation.Vertical };
        column.SetPadding(Dp(10), 0, 0, 0);

        // Google requires the word "Ad" on a native placement, so nobody mistakes it for
        // one of the listings around it.
        var label = new TextView(this) { Text = "Ad", TextSize = 10 };
        label.SetTextColor(global::Android.Graphics.Color.ParseColor("#111111"));
        label.SetBackgroundColor(global::Android.Graphics.Color.ParseColor("#F2C94C"));
        label.SetPadding(Dp(5), Dp(1), Dp(5), Dp(1));

        var headline = new TextView(this) { TextSize = 15 };
        headline.SetTextColor(global::Android.Graphics.Color.White);
        headline.SetMaxLines(2);

        var body = new TextView(this) { TextSize = 12 };
        body.SetTextColor(global::Android.Graphics.Color.ParseColor("#C8C8C8"));
        body.SetMaxLines(2);

        var action = new Button(this) { TextSize = 12 };
        action.SetAllCaps(false);

        column.AddView(label, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        column.AddView(headline);
        column.AddView(body);
        column.AddView(action, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, Dp(34)));

        row.AddView(column, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        card.AddView(row);

        card.IconView = icon;
        card.HeadlineView = headline;
        card.BodyView = body;
        card.CallToActionView = action;

        _nativeCard = card;
        LoadNative();
        return card;
    }

    private void LoadNative() =>
        new AdLoader.Builder(this, NativeUnit)
            .ForNativeAd(new NativeLoaded(this))
            .Build()
            .LoadAd(new AdRequest.Builder().Build());

    private sealed class NativeLoaded : Java.Lang.Object, NativeAd.IOnNativeAdLoadedListener
    {
        private readonly MainActivity _activity;

        public NativeLoaded(MainActivity activity) => _activity = activity;

        public void OnNativeAdLoaded(NativeAd ad)
        {
            var card = _activity._nativeCard;
            if (card is null) { ad.Destroy(); return; }

            if (card.HeadlineView is TextView headline) headline.Text = ad.Headline ?? "";
            if (card.BodyView is TextView body) body.Text = ad.Body ?? "";
            if (card.CallToActionView is Button action) action.Text = ad.CallToAction ?? "Open";
            if (card.IconView is ImageView icon) icon.SetImageDrawable(ad.Icon?.Drawable);

            card.SetNativeAd(ad);
        }
    }

    protected override void OnResume()
    {
        base.OnResume();
        foreach (var banner in _banners.Values) banner.Resume();
    }

    protected override void OnPause()
    {
        foreach (var banner in _banners.Values) banner.Pause();
        base.OnPause();
    }

    protected override void OnDestroy()
    {
        foreach (var banner in _banners.Values) banner.Destroy();
        _banners.Clear();
        base.OnDestroy();
    }
}
