using Avalonia;
using Avalonia.Controls;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using System.Threading;

namespace TarkovAtlas.Services;

/// <summary>
/// Shared UI micro-animations. Everything is opt-in and cheap: opacity /
/// width fades on existing controls, 150-220 ms, ease-out. Kept in one place
/// so call sites stay one-liners and the timing stays consistent.
/// </summary>
public static class UiFx
{
    /// <summary>Standard duration; short enough to feel instant, long enough
    /// to remove the "pops in" harshness.</summary>
    public static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(150);

    public static readonly TimeSpan Normal = TimeSpan.FromMilliseconds(220);

    private static readonly Easing Ease = new SineEaseOut();

    /// <summary>Fade a control in. Call where IsVisible = true used to be set.</summary>
    public static void FadeIn(Control c, TimeSpan? duration = null)
    {
        var target = c.Opacity == 0 ? 1d : c.Opacity;
        c.IsVisible = true;
        c.Opacity = 0;
        new Animation
        {
            Duration = duration ?? Fast,
            Easing = Ease,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0.0), Setters = { new Setter(Visual.OpacityProperty, 0d) } },
                new KeyFrame { Cue = new Cue(1.0), Setters = { new Setter(Visual.OpacityProperty, target) } },
            },
        }.RunAsync(c);
    }

    /// <summary>Fade out then collapse. Safe to call on already-hidden controls.</summary>
    public static void FadeOutAndCollapse(Control c, TimeSpan? duration = null)
    {
        if (!c.IsVisible) { c.Opacity = 1; return; }
        var from = c.Opacity;
        var done = new CancellationTokenSource();
        new Animation
        {
            Duration = duration ?? Fast,
            Easing = Ease,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0.0), Setters = { new Setter(Visual.OpacityProperty, from) } },
                new KeyFrame { Cue = new Cue(1.0), Setters = { new Setter(Visual.OpacityProperty, 0d) } },
            },
        }.RunAsync(c, done.Token).ContinueWith(_ =>
        {
            if (done.IsCancellationRequested) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                c.IsVisible = false;
                c.Opacity = 1;
            });
        });
    }

    /// <summary>Animate Width from its current value to <paramref name="target"/>.
    /// Used for the quest drawer; the caller keeps IsVisible in sync.</summary>
    public static void AnimateWidth(Control c, double target, TimeSpan? duration = null)
    {
        var from = c.Width is double w && !double.IsNaN(w) ? w : 0;
        new Animation
        {
            Duration = duration ?? Normal,
            Easing = Ease,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0.0), Setters = { new Setter(Layoutable.WidthProperty, from) } },
                new KeyFrame { Cue = new Cue(1.0), Setters = { new Setter(Layoutable.WidthProperty, target) } },
            },
        }.RunAsync(c);
    }
}
