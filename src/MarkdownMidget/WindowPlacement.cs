using System;
using System.Collections.Generic;
using System.Windows;

namespace MarkdownMidget;

/// <summary>
/// Validates a remembered window rectangle before it is applied.
///
/// Restoring saved bounds blindly is how an app becomes unreachable: the monitor
/// it was on gets disconnected, the resolution drops, or a laptop leaves its dock,
/// and the window reopens somewhere no mouse can follow. Every restore is checked
/// against the monitors that exist *now*, and the size is clamped to the monitor it
/// lands on — a 3400px-wide window restored onto a 1920px laptop screen would
/// otherwise put its close button past the right edge.
/// </summary>
internal static class WindowPlacement
{
    /// <summary>Enough of the title bar must be visible to grab and drag.</summary>
    private const double MinVisible = 80;

    /// <summary>Roughly a caption bar's height, in physical pixels. Only this strip
    /// can be dragged, so only this strip decides whether a window is recoverable —
    /// testing the whole window would pass a rect whose body is visible but whose
    /// title bar sits above the top of the screen.</summary>
    private const double TitleBar = 32;

    /// <summary>
    /// The bounds to actually use, or null to fall back to the default placement.
    /// Returns null when nothing was saved, the size is unusable, or no monitors
    /// were reported.
    /// </summary>
    /// <param name="screens">Monitor work areas, primary first.</param>
    public static Rect? Sanitize(Rect? saved, IReadOnlyList<Rect> screens, Size minSize)
    {
        if (saved is not { } r) return null;
        if (double.IsNaN(r.Width) || double.IsNaN(r.Height) ||
            double.IsNaN(r.X) || double.IsNaN(r.Y)) return null;
        if (r.Width < minSize.Width || r.Height < minSize.Height) return null;
        if (screens.Count == 0) return null;

        // The monitor the window sits on is the one it overlaps most.
        var best = Rect.Empty;
        var bestArea = 0.0;
        foreach (var s in screens)
        {
            var overlap = Rect.Intersect(r, s);
            if (overlap.IsEmpty) continue;
            var area = overlap.Width * overlap.Height;
            if (area <= bestArea) continue;
            bestArea = area;
            best = s;
        }

        // Reachable = a graspable piece of the TITLE BAR is on SOME monitor. The
        // window's body being visible is not enough: with the caption above the top
        // edge there is nothing to drag, and the placement re-saves itself on close,
        // so the window is stranded every launch until the user finds Alt+Space.
        // Checked against every screen, not just `best` — a window straddling a
        // horizontal seam can have its caption on the upper monitor and its bulk on
        // the lower one, and that is a perfectly usable window.
        var caption = new Rect(r.X, r.Y, r.Width, Math.Min(TitleBar, r.Height));
        var reachable = false;
        foreach (var s in screens)
        {
            var visible = Rect.Intersect(caption, s);
            if (visible.IsEmpty) continue;
            if (visible.Width >= Math.Min(MinVisible, r.Width) &&
                visible.Height >= Math.Min(TitleBar, r.Height)) { reachable = true; break; }
        }

        // best is non-empty whenever reachable (the caption is part of the window),
        // but never hand Rect.Empty to the sizing maths — its Width is -infinity.
        if (!reachable || best.IsEmpty) return Centre(r, screens[0], minSize);

        // It's reachable, so the position is the user's business — a window may
        // legitimately straddle two monitors or hang off an edge, and shoving it
        // onto one monitor every launch would be its own bug. Only a window too
        // big for the monitor it's on needs help.
        if (r.Width <= best.Width && r.Height <= best.Height) return r;
        return Shrink(r, best, minSize);
    }

    /// <summary>Too big for its monitor: shrink to fit and pull fully inside.</summary>
    private static Rect Shrink(Rect r, Rect screen, Size minSize)
    {
        var (w, h) = ClampSize(r, screen, minSize);
        var x = Math.Min(Math.Max(r.X, screen.X), screen.X + screen.Width - w);
        var y = Math.Min(Math.Max(r.Y, screen.Y), screen.Y + screen.Height - h);
        return new Rect(x, y, w, h);
    }

    /// <summary>Unreachable: put it back in the middle of the primary monitor.</summary>
    private static Rect Centre(Rect r, Rect screen, Size minSize)
    {
        var (w, h) = ClampSize(r, screen, minSize);
        return new Rect(screen.X + (screen.Width - w) / 2,
                        screen.Y + (screen.Height - h) / 2, w, h);
    }

    /// <summary>Never wider/taller than the monitor; never below the app minimum
    /// unless the monitor itself is smaller than that.</summary>
    private static (double W, double H) ClampSize(Rect r, Rect screen, Size minSize) =>
        (Math.Max(Math.Min(r.Width, screen.Width), Math.Min(minSize.Width, screen.Width)),
         Math.Max(Math.Min(r.Height, screen.Height), Math.Min(minSize.Height, screen.Height)));
}
