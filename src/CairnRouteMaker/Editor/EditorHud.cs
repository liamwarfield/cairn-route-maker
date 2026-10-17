using CairnRouteMaker.Routes;
using UnityEngine;

namespace CairnRouteMaker.Editor;

/// <summary>On-screen help and status, drawn with IMGUI.</summary>
internal static class EditorHud
{
    private const float StatusSeconds = 4f;

    private const string Help =
        "WASD move   Q/E down/up   Shift fast   Alt slow\n" +
        "Hold RMB look   LMB paint   Scroll brush size\n" +
        "Tab allow/deny   G set start (facing = camera)\n" +
        "Ctrl+Z undo   Ctrl+S save   P play-test   F6/Esc exit";

    public static void Draw(RouteEditor editor)
    {
        if (editor.IsOpen)
        {
            var route = editor.Route;
            var stamps = 0;
            foreach (var stroke in route.Strokes)
            {
                stamps += stroke.Stamps.Count;
            }

            var text =
                $"ROUTE EDITOR - {route.ZoneKey}\n" +
                $"Brush: {(editor.Mode == StrokeMode.Allow ? "ALLOW" : "DENY")}  radius {editor.Radius:0.00} m" +
                $"{(editor.HasBrushTarget ? "" : "  (no rock under cursor)")}\n" +
                $"Strokes: {route.Strokes.Count}  stamps: {stamps}  start: {(route.Start == null ? "not set" : "set")}\n\n" +
                Help;
            GUI.Box(new Rect(Screen.width - 430, 20, 410, 150), text);
        }

        if (editor.Status != null && Time.unscaledTime - editor.StatusTime < StatusSeconds)
        {
            GUI.Box(new Rect(Screen.width / 2f - 250, Screen.height - 90, 500, 28), editor.Status);
        }
    }
}
