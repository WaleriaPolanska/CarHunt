using System;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using Google.Android.Gms.Ads;
using Google.Android.Gms.Ads.NativeAd;

namespace CarHunt.Android;

/// <summary>
/// The ad shown among the saved listings, built from a native ad's parts rather than
/// dropped in as a finished banner.
///
/// Google hands over the text and the button and lets the app draw them, but they have
/// to sit inside its own NativeAdView or the impression is never counted. So the layout
/// below is ours; the container is theirs.
///
/// The view is returned empty and fills itself when the ad arrives — loading takes a
/// second or two and nothing should wait for it.
/// </summary>
public sealed class NativeAdSlot : FrameLayout
{
    private readonly NativeAdView _container;
    private readonly TextView _headline;
    private readonly TextView _body;
    private readonly Button _action;
    private readonly MediaView _media;

    private NativeAd? _ad;

    public NativeAdSlot(Context context, string unitId) : base(context)
    {
        var density = context.Resources!.DisplayMetrics!.Density;
        int Dp(float value) => (int)(value * density);

        _headline = new TextView(context) { TextSize = 15 };
        _headline.SetTextColor(Color.White);
        _headline.SetMaxLines(2);

        _body = new TextView(context) { TextSize = 12 };
        _body.SetTextColor(Color.ParseColor("#B9B9B9"));
        _body.SetMaxLines(2);

        _action = new Button(context) { TextSize = 13 };
        _action.SetTextColor(Color.White);
        _action.SetBackgroundColor(Color.ParseColor("#3A9E8C"));

        var sponsored = new TextView(context) { Text = "Ad", TextSize = 10 };
        sponsored.SetTextColor(Color.ParseColor("#111111"));
        sponsored.SetBackgroundColor(Color.ParseColor("#F5C518"));
        sponsored.SetPadding(Dp(4), Dp(1), Dp(4), Dp(1));

        // Google insists the main image or video go through its own MediaView — its
        // validator flags anything else, and the ad is not credited properly without it.
        _media = new MediaView(context);

        var column = new LinearLayout(context) { Orientation = Orientation.Vertical };
        column.SetPadding(Dp(10), Dp(8), Dp(10), Dp(8));
        column.AddView(sponsored, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        column.AddView(_headline);
        column.AddView(_media, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(120)));
        column.AddView(_body);
        column.AddView(_action, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

        _container = new NativeAdView(context);
        _container.AddView(column);
        _container.HeadlineView = _headline;
        _container.BodyView = _body;
        _container.CallToActionView = _action;
        _container.MediaView = _media;

        AddView(_container, new LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        Load(context, unitId);
    }

    private void Load(Context context, string unitId) =>
        new AdLoader.Builder(context, unitId)
            .ForNativeAd(new Arrived(this))
            .Build()
            .LoadAd(new AdRequest.Builder().Build());

    private void Fill(NativeAd ad)
    {
        // The previous one is released: an ad held after it is replaced leaks the whole
        // view tree behind it.
        _ad?.Destroy();
        _ad = ad;

        _headline.Text = ad.Headline ?? "";
        _body.Text = ad.Body ?? "";
        _action.Text = ad.CallToAction ?? "";
        _action.Visibility = string.IsNullOrEmpty(ad.CallToAction) ? ViewStates.Gone : ViewStates.Visible;

        _container.SetNativeAd(ad);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _ad?.Destroy();
            _ad = null;
        }

        base.Dispose(disposing);
    }

    private sealed class Arrived : Java.Lang.Object, NativeAd.IOnNativeAdLoadedListener
    {
        private readonly NativeAdSlot _slot;

        public Arrived(NativeAdSlot slot) => _slot = slot;

        public void OnNativeAdLoaded(NativeAd ad) => _slot.Fill(ad);
    }
}
