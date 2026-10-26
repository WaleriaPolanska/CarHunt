using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace CarHunt.Views;

/// <summary>
/// A place in the page for a platform-drawn ad.
///
/// Avalonia paints its whole interface into one surface, so an ad — which is a system
/// view — has to be handed to the platform and placed over the spot this control
/// occupies. That is what a native control host does.
///
/// Every previous attempt to put one inside the page ended with the process dying:
/// inside the results list, over the loading screen, and under the search button. This
/// slot exists for the saved list, which is short and does not recycle its rows — an
/// experiment, and the reason the factory is allowed to return nothing.
/// </summary>
public class AdSlot : NativeControlHost
{
    /// <summary>Which ad this slot wants; the head decides what that means.</summary>
    public static readonly StyledProperty<int> SlotProperty =
        AvaloniaProperty.Register<AdSlot, int>(nameof(Slot));

    public int Slot
    {
        get => GetValue(SlotProperty);
        set => SetValue(SlotProperty, value);
    }

    /// <summary>Creates the platform's view for a slot. Set once, at startup, by the head.</summary>
    public static Func<int, IPlatformHandle?>? Factory { get; set; }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent) =>
        Factory?.Invoke(Slot) ?? base.CreateNativeControlCore(parent);
}
