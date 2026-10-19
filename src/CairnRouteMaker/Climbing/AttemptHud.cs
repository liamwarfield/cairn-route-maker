using CairnRouteMaker.Routes;
using UnityEngine;

namespace CairnRouteMaker.Climbing;

/// <summary>The route timer in the top left, drawn with IMGUI.</summary>
internal static class AttemptHud
{
    private static readonly Color Running = Color.white;
    private static readonly Color Failed = new(1f, 0.25f, 0.2f);
    private static readonly Color Complete = new(0.35f, 1f, 0.45f);
    private static readonly Color Shadow = new(0f, 0f, 0f, 0.75f);

    private static GUIStyle _timerStyle;
    private static GUIStyle _labelStyle;

    public static void Draw(AttemptTracker tracker)
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
