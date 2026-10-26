using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CarHunt.ViewModels;

namespace CarHunt.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();

        foreach (var button in new[] { SearchButton, ShareButton, PendingButton })
            PressFeedback.Attach(button, PressFeedback.Quick);

        PressFeedback.Attach(FavoritesButton, PressFeedback.Gentle);

        DataContextChanged += (_, _) => WatchViewModel();
        WatchViewModel();

        // Paging follows the reader: the search loads three pages and then waits, and
        // this is what tells it to fetch more. Without it the app pulled forty pages from
        // every site whether or not anyone scrolled that far.
        ResultsList.AddHandler(ScrollViewer.ScrollChangedEvent, OnResultsScrolled);
    }

    /// <summary>
    /// Asks for the next pages once the end of the loaded list is within two screens, so
    /// the next batch is already arriving by the time the reader gets there.
    /// </summary>
    private void OnResultsScrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || ResultsList.Scroll is not { } scroll) return;

        var remaining = scroll.Extent.Height - scroll.Offset.Y - scroll.Viewport.Height;
        if (remaining <= scroll.Viewport.Height * 2) vm.RequestMore();
    }

    /// <summary>
    /// Takes over the platform's own back gesture — on Android that is the swipe in from
    /// the left edge. An earlier attempt drew a strip along that edge to catch the swipe
    /// itself, but the system claims that area first and closed the whole app instead.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is { } top) top.BackRequested += OnBackRequested;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is { } top) top.BackRequested -= OnBackRequested;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        // Step back through what is open; leaving it unhandled is what lets the platform
        // close the app, which should only happen from the search screen itself.
        if (vm.ShowBrowser) vm.CloseBrowserCommand.Execute(null);
        else if (vm.ShowFavorites) vm.ToggleFavoritesCommand.Execute(null);
        else if (vm.GoToPreviousPage()) ScrollResultsToTop();
        else return;

        e.Handled = true;
    }

    private MainViewModel? _watched;
    private double _anchorBefore;
    private int _anchorGeneration;

    private void WatchViewModel()
    {
        if (_watched is not null) _watched.PropertyChanged -= OnViewModelPropertyChanged;
        _watched = DataContext as MainViewModel;
        if (_watched is not null) _watched.PropertyChanged += OnViewModelPropertyChanged;
    }

    /// <summary>
    /// Both lists sit below the notepad, so nothing above it changes when one replaces
    /// the other. Whichever list is hidden takes its height with it, though, and the
    /// ScrollViewer then clamps the scroll position — throwing the view up or down.
    /// Holding the notepad at the same place on screen cancels that out in either
    /// direction. The property change runs before layout, so this reading is the
    /// position the user is still looking at.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.ShowFavorites) || _watched is null) return;
        if (this.FindControl<ScrollViewer>("MainScroll") is not { } scroll) return;
        if (NotepadOffsetInViewport(scroll) is not { } before) return;

        _anchorBefore = before;
        var generation = ++_anchorGeneration;

        void Reanchor(int triesLeft)
        {
            if (generation != _anchorGeneration) return; // a newer toggle took over
            if (NotepadOffsetInViewport(scroll) is not { } after) return;

            var drift = after - _anchorBefore;
            if (Math.Abs(drift) > 0.5)
            {
                var corrected = Math.Max(0, scroll.Offset.Y + drift);
                scroll.Offset = scroll.Offset.WithY(corrected);
                if (triesLeft > 0)
                    Dispatcher.UIThread.Post(() => Reanchor(triesLeft - 1), DispatcherPriority.Background);
            }
        }

        // Retried on posted callbacks, never on LayoutUpdated: that fires on every
        // layout pass — image loads, clicks — and a handler left attached kept
        // yanking the scroll around long after the toggle was over.
        Dispatcher.UIThread.Post(() => Reanchor(6), DispatcherPriority.Loaded);
    }

    private double? NotepadOffsetInViewport(ScrollViewer scroll) =>
        FavoritesButton.TranslatePoint(default, scroll)?.Y;

    /// <summary>
    /// A new page is read from the top. Click fires before the command runs, so the jump is
    /// posted to land once the list has been rebuilt.
    /// </summary>
    private void OnShowPendingClick(object? sender, RoutedEventArgs e) => ScrollResultsToTop();

    /// <summary>Every page is read from its first card, whichever way the reader turned to it.</summary>
    private void ScrollResultsToTop() =>
        Dispatcher.UIThread.Post(() =>
        {
            if (ResultsList.ItemCount == 0) return;
            ResultsList.ScrollIntoView(0);
            ResultsList.BringIntoView();
        }, DispatcherPriority.Background);

    /// <summary>
    /// Copies the saved listings so they can be pasted into any messenger. Avalonia has
    /// no cross-platform share sheet, and the clipboard is the one route that works on
    /// every target the app builds for.
    /// </summary>
    private async void OnShareFavorites(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        var text = vm.BuildFavoritesShareText();
        if (string.IsNullOrWhiteSpace(text)) return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;

        // Avalonia 12 replaced SetTextAsync with the data-transfer API.
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(text));
        await clipboard.SetDataAsync(data);

        await vm.FlashSharedAsync();
    }

    private void OnLogoClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            // Return to the main search state so the action is always visible to user.
            vm.ShowBrowser = false;
            vm.ShowFavorites = false;
            vm.Brand = string.Empty;
            vm.Model = string.Empty;
            vm.YearFrom = string.Empty;
            vm.YearTo = string.Empty;
            vm.PriceFrom = string.Empty;
            vm.PriceTo = string.Empty;
            vm.MileageTo = string.Empty;
            vm.FuelType = string.Empty;
            vm.StatusText = "Enter search parameters and tap Search";
            vm.Results.Clear();
            vm.Favorites.Clear();

            if (vm.ShowResultsCommand.CanExecute(null))
            {
                vm.ShowResultsCommand.Execute(null);
            }
        }

        if (this.FindControl<ScrollViewer>("MainScroll") is { } scroll)
        {
            scroll.Offset = scroll.Offset.WithY(0);
        }

        e.Handled = true;
    }
}