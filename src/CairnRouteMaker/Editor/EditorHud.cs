using CairnRouteMaker.Routes;
using UnityEngine;

namespace CairnRouteMaker.Editor;

/// <summary>On-screen help, status, gamepad reticle and the level select / launcher menu, drawn with IMGUI.</summary>
internal static class EditorHud
{
    private const float StatusSeconds = 4f;
    private const float RowHeight = 24f;
    private const int VisibleRows = 18;

    private const string KeyboardHelp =
        "WASD move   Q/E down/up   Shift fast   Alt medium\n" +
        "Hold RMB look   LMB paint   Scroll or [ ] brush size\n" +
        "Tab or 1/2/3 allow/deny/erase   G set start (facing = camera)\n" +
        "F set finish (brush size, at least 1 m)\n" +
        "Ctrl+Z undo   Ctrl+S save   P play-test   L level select   Esc exit\n" +
        "F8 freecam";

    private const string GamepadHelp =
        "LS move   RS look   LB/RB down/up   hold LT fast\n" +
        "RT paint   D-pad up/down brush size   X allow/deny/erase\n" +
        "Y set start   A set finish (brush size)   View undo\n" +
        "D-pad right save   Menu play-test\n" +
        "D-pad left level select   B or L3+R3 exit   R3 freecam";

    private const string FreecamKeyboardHelp =
        "WASD move   Q/E down/up   Shift fast   Alt medium\n" +
        "Hold RMB look   LMB move Ava to the marker\n" +
        "P play-test   L level select   Esc exit   F8 route editor";

    private const string FreecamGamepadHelp =
        "LS move   RS look   LB/RB down/up   hold LT fast\n" +
        "A move Ava to the marker   Menu play-test\n" +
        "D-pad left level select   B or L3+R3 exit   R3 route editor";

    public static void Draw(RouteEditor editor)
    {
        if (editor.IsOpen)
        {
            if (editor.LevelSelect.IsOpen)
            {
                DrawMenu(editor.LevelSelect, editor.Input.UsingGamepad);
            }
            else if (editor.Freecam)
            {
                DrawFreecamPanel(editor);
            }
            else
            {
                DrawEditorPanel(editor);
            }
        }

        DrawStatus(editor.Status, editor.StatusTime);
    }

    /// <summary>A status line near the bottom of the screen, shown for a few seconds after it was set.</summary>
    public static void DrawStatus(string status, float since)
    {
        if (status != null && Time.unscaledTime - since < StatusSeconds)
        {
            GUI.Box(new Rect(Screen.width / 2f - 250, Screen.height - 90, 500, 28), status);
        }
    }

    private static void DrawEditorPanel(RouteEditor editor)
    {
        var route = editor.Route;
        var stamps = 0;
        foreach (var stroke in route.Strokes)
        {
            stamps += stroke.Stamps.Count;
        }

        var mode = editor.Mode switch { StrokeMode.Allow => "ALLOW", StrokeMode.Deny => "DENY", _ => "ERASE" };
        var text =
            $"ROUTE EDITOR - {route.ZoneKey}\n" +
            $"Brush: {mode}  radius {editor.Radius:0.00} m" +
            $"{(editor.HasBrushTarget ? "" : "  (no rock under brush)")}\n" +
            $"Strokes: {route.Strokes.Count}  stamps: {stamps}  start: {(route.Start == null ? "not set" : "set")}" +
            $"  finish: {(route.Finish == null ? "not set" : "set")}\n\n" +
            (editor.Input.UsingGamepad ? GamepadHelp : KeyboardHelp);
        GUI.Box(new Rect(Screen.width - 450, 20, 430, 184), text);
        DrawReticle(editor);
    }

    private static void DrawFreecamPanel(RouteEditor editor)
    {
        var text =
            $"FREECAM - {editor.Route.ZoneKey}\n" +
            $"{(editor.HasBrushTarget ? "" : "No rock under the marker")}\n\n" +
            (editor.Input.UsingGamepad ? FreecamGamepadHelp : FreecamKeyboardHelp);
        GUI.Box(new Rect(Screen.width - 450, 20, 430, 104), text);
        DrawReticle(editor);
    }

    private static void DrawReticle(RouteEditor editor)
    {
        if (editor.Input.UsingGamepad && !editor.Looking)
        {
            // With a gamepad the brush aims at the middle of the screen.
            GUI.Label(new Rect(Screen.width * 0.5f - 6, Screen.height * 0.5f - 11, 20, 22), "+");
        }
    }

    public static void DrawMenu(LevelSelect menu, bool usingGamepad)
    {
        var entries = menu.Entries;
        const float width = 460f;
        var height = RowHeight * (VisibleRows + 2) + 10;
        var left = Screen.width / 2f - width / 2f;
        var top = Screen.height / 2f - height / 2f;
        var hint = usingGamepad ? "D-pad choose   A go   B back" : "Arrows choose   Enter or click go   Esc back";
        GUI.Box(new Rect(left, top, width, height), $"{menu.Title}   ({hint})");

        // Keep the highlighted entry in view.
        var first = Mathf.Clamp(menu.Selected - VisibleRows / 2, 0, Mathf.Max(0, entries.Count - VisibleRows));
        for (var row = 0; row < VisibleRows && first + row < entries.Count; row++)
        {
            var index = first + row;
            var entry = entries[index];
            var rect = new Rect(left + 10, top + RowHeight * (row + 1) + 4, width - 20, RowHeight - 2);
            if (entry.IsHeader)
            {
                GUI.Label(rect, $"-- {entry.Label} --");
            }
            else if (!entry.Enabled)
            {
                GUI.Label(rect, $"      {entry.Label}");
            }
            else if (GUI.Button(rect, (index == menu.Selected ? "> " : "   ") + entry.Label))
            {
                menu.ClickedIndex = index;
            }
        }

        if (entries.Count > VisibleRows)
        {
            GUI.Label(
                new Rect(left + 10, top + height - RowHeight - 2, width - 20, RowHeight),
                $"{first + 1}-{Mathf.Min(first + VisibleRows, entries.Count)} of {entries.Count}");
        }
    }
}
