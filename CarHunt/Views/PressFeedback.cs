using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;

namespace CarHunt.Views;

/// <summary>
/// The dip-and-rebound a button plays when pressed.
///
/// Run explicitly rather than from a ":pressed" style: a click holds that state for only
/// a few dozen milliseconds, so a style-driven transition is cut off long before it is
/// visible. Button also marks PointerPressed as handled in its own handler, so the
/// subscription has to ask for handled events too or it is never called.
/// </summary>
public static class PressFeedback
{
    /// <summary>
    /// How far the finger may travel and still count as a tap. Every one of these buttons
    /// sits inside the page's ScrollViewer, and the Favorites pad is full width and 104
    /// tall, so a scroll almost always starts on top of one. Playing the dip on press made
    /// the pad flash on every flick as if it were being hammered.
    /// </summary>
    private const double TapSlack = 12;

    public static Animation Quick { get; } = Create(240);

    /// <summary>A large surface looks jerky at the quick speed, so it settles at half of it.</summary>
    public static Animation Gentle { get; } = Create(480);

    /// <summary>Plays <paramref name="animation"/> when the control is tapped, not dragged.</summary>
    public static void Attach(Control control, Animation animation)
    {
        var origin = default(Point);

        control.AddHandler(InputElement.PointerPressedEvent,
            (_, e) => origin = e.GetPosition(control),
            RoutingStrategies.Bubble, handledEventsToo: true);

        control.AddHandler(InputElement.PointerReleasedEvent,
            (_, e) =>
            {
                var travelled = e.GetPosition(control) - origin;
                if (Math.Abs(travelled.X) <= TapSlack && Math.Abs(travelled.Y) <= TapSlack)
                    animation.RunAsync(control);
            },
            RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private static Animation Create(int milliseconds) => new()
    {
        Duration = TimeSpan.FromMilliseconds(milliseconds),
        Easing = new CubicEaseOut(),
        FillMode = FillMode.None,
        Children =
        {
            Frame(0d, 1d),
            Frame(0.4d, 0.88d),
            Frame(1d, 1d)
        }
    };

    private static KeyFrame Frame(double cue, double scale) => new()
    {
        Cue = new Cue(cue),
        Setters =
        {
            new Setter(ScaleTransform.ScaleXProperty, scale),
            new Setter(ScaleTransform.ScaleYProperty, scale)
        }
    };
}
