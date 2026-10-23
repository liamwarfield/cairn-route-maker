using CairnRouteMaker.Editor;
using CairnRouteMaker.Routes;
using UnityEngine;

namespace CairnRouteMaker.Climbing;

/// <summary>
/// The route timer in the top left and the next-grab indicator in the bottom right, drawn with IMGUI.
/// </summary>
internal static class AttemptHud
{
    private static readonly Color Running = Color.white;
    private static readonly Color Failed = new(1f, 0.25f, 0.2f);
    private static readonly Color Complete = new(0.35f, 1f, 0.45f);
    private static readonly Color Shadow = new(0f, 0f, 0f, 0.75f);
    private static readonly Color NoGrab = new(0.6f, 0.6f, 0.6f, 0.5f);

    private const int IndicatorSize = 56;
    private const int IndicatorMargin = 32;
    private const int IndicatorBorder = 4;

    private static GUIStyle _timerStyle;
    private static GUIStyle _labelStyle;
    private static Texture2D _disc;

    public static void Draw(AttemptTracker tracker)
    {
        DrawNextGrab(tracker.NextGrab);
        DrawTimer(tracker);
    }

    /// <summary>
    /// The allow paint colour if the hold the moving limb is heading for is on-route, the deny paint
    /// colour if it isn't, so it reads like the paint on the rock.
    /// </summary>
    private static void DrawNextGrab(NextGrabStatus status)
    {
        if (status == NextGrabStatus.Hidden)
        {
            return;
        }

        Color color = status switch
        {
            NextGrabStatus.Valid => PaintColors.Allow,
            NextGrabStatus.Invalid => PaintColors.Deny,
            _ => NoGrab,
        };
        var disc = Disc();
        var outer = new Rect(
            Screen.width - IndicatorMargin - IndicatorSize,
            Screen.height - IndicatorMargin - IndicatorSize,
            IndicatorSize,
            IndicatorSize);
        var inner = new Rect(
            outer.x + IndicatorBorder,
            outer.y + IndicatorBorder,
            IndicatorSize - IndicatorBorder * 2,
            IndicatorSize - IndicatorBorder * 2);
        var previous = GUI.color;
        GUI.color = Shadow;
        GUI.DrawTexture(outer, disc);
        GUI.color = color;
        GUI.DrawTexture(inner, disc);
        GUI.color = previous;
    }

    private static void DrawTimer(AttemptTracker tracker)
    {
        var attempt = tracker.Attempt;
        if (attempt.State == AttemptState.Idle || tracker.Route == null)
        {
            return;
        }

        EnsureStyles();
        var color = attempt.OffRoute ? Failed
            : attempt.State == AttemptState.Finished ? Complete
            : Running;
        var label = (attempt.State, attempt.OffRoute) switch
        {
            (AttemptState.Ready, _) => "Ready - start climbing",
            (AttemptState.Finished, false) => "Route complete!",
            (AttemptState.Finished, true) => "Finished, but failed to stay on route",
            (_, true) => "Failed to stay on route",
            _ when tracker.Route.Finish == null => "This route has no finish yet",
            _ => null,
        };

        Outlined(new Rect(24, 16, 400, 48), Attempt.FormatTime(attempt.Elapsed(Time.timeAsDouble)), _timerStyle, color);
        if (label != null)
        {
            Outlined(new Rect(26, 62, 400, 28), label, _labelStyle, color);
        }
    }

    private static void Outlined(Rect rect, string text, GUIStyle style, Color color)
    {
        // A drop shadow keeps the text readable against snow and sky.
        style.normal.textColor = Shadow;
        GUI.Label(new Rect(rect.x + 2, rect.y + 2, rect.width, rect.height), text, style);
        style.normal.textColor = color;
        GUI.Label(rect, text, style);
    }

    /// <summary>A white anti-aliased disc, tinted with GUI.color when drawn.</summary>
    private static Texture2D Disc()
    {
        if (_disc != null)
        {
            return _disc;
        }

        const int size = 64;
        _disc = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
        // Survive the game's asset unloading on zone changes (see DecalPaint.Keep).
        _disc.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        var radius = size / 2f;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = x + 0.5f - radius;
                var dy = y + 0.5f - radius;
                var alpha = System.Math.Clamp(radius - System.MathF.Sqrt(dx * dx + dy * dy), 0f, 1f);
                _disc.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
        }

        _disc.Apply();
        return _disc;
    }

    private static void EnsureStyles()
    {
        if (_timerStyle != null)
        {
            return;
        }

        _timerStyle = new GUIStyle(GUI.skin.label) { fontSize = 40, fontStyle = FontStyle.Bold };
        _labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold };
    }
}
