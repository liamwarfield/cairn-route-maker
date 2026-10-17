using CairnRouteMaker.Routes;
using UnityEngine;

namespace CairnRouteMaker.Editor;

/// <summary>On-screen help, status, gamepad reticle and the level select, drawn with IMGUI.</summary>
internal static class EditorHud
{
    private const float StatusSeconds = 4f;
    private const float RowHeight = 24f;
    private const int VisibleRows = 18;

    private const string KeyboardHelp =
        "WASD move   Q/E down/up   Shift fast   Alt slow\n" +
        "Hold RMB look   LMB paint   Scroll or [ ] brush size\n" +
        "Tab or 1/2/3 allow/deny/erase   G set start (facing = camera)\n" +
        "Ctrl+Z undo   Ctrl+S save   P play-test   L level select   Esc exit";

    private const string GamepadHelp =
        "LS move   RS look   LB/RB down/up   hold LT fast\n" +
        "RT paint   D-pad up/down brush size   X allow/deny/erase\n" +
        "Y set start   View undo   D-pad right save   Menu play-test\n" +
        "D-pad left level select   B or L3+R3 exit";

    public static void Draw(RouteEditor editor)
    {
        if (editor.IsOpen)
        {
            if (editor.LevelSelect.IsOpen)
            {
                DrawLevelSelect(editor);
            }
            else
            {
                DrawEditorPanel(editor);
            }
        }

        if (editor.Status != null && Time.unscaledTime - editor.StatusTime < StatusSeconds)
        {
            GUI.Box(new Rect(Screen.width / 2f - 250, Screen.height - 90, 500, 28), editor.Status);
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
            $"Strokes: {route.Strokes.Count}  stamps: {stamps}  start: {(route.Start == null ? "not set" : "set")}\n\n" +
            (editor.Input.UsingGamepad ? GamepadHelp : KeyboardHelp);
        GUI.Box(new Rect(Screen.width - 450, 20, 430, 150), text);

        if (editor.Input.UsingGamepad && !editor.Looking)
        {
            // With a gamepad the brush aims at the middle of the screen.
            GUI.Label(new Rect(Screen.width * 0.5f - 6, Screen.height * 0.5f - 11, 20, 22), "+");
        }
    }

    private static void DrawLevelSelect(RouteEditor editor)
    {
        var menu = editor.LevelSelect;
        var entries = menu.Entries;
        const float width = 460f;
        var height = RowHeight * (VisibleRows + 2) + 10;
        var left = Screen.width / 2f - width / 2f;
        var top = Screen.height / 2f - height / 2f;
        var hint = editor.Input.UsingGamepad ? "D-pad choose   A go   B back" : "Arrows choose   Enter or click go   Esc back";
        GUI.Box(new Rect(left, top, width, height), $"LEVEL SELECT   ({hint})");

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
