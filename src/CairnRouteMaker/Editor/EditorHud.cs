using System.Linq;
using CairnRouteMaker.Controls;
using CairnRouteMaker.Routes;
using UnityEngine;

namespace CairnRouteMaker.Editor;

/// <summary>On-screen help, status and gamepad reticle, drawn with IMGUI. The level select is <see cref="LevelSelectView"/>.</summary>
internal static class EditorHud
{
    private const float StatusSeconds = 4f;

    // Help text names the current bindings, which can be changed on the game's remap screen, so
    // it's rebuilt whenever they change.
    private static int _helpVersion = -1;
    private static string _keyboardHelp;
    private static string _gamepadHelp;
    private static string _freecamKeyboardHelp;
    private static string _freecamGamepadHelp;

    private static void RefreshHelp()
    {
        if (_helpVersion == ModControls.Version)
        {
            return;
        }

        _helpVersion = ModControls.Version;
        var move = $"{Keys(ModControls.MoveForward, ModControls.MoveLeft, ModControls.MoveBack, ModControls.MoveRight)} move   " +
            $"{Keys(ModControls.MoveDown, ModControls.MoveUp)} down/up   {ModControls.Fast.KeyName} fast   {ModControls.Medium.KeyName} medium\n";
        var padMove = $"LS move   RS look   {Pads(ModControls.MoveDown, ModControls.MoveUp)} down/up   hold {ModControls.Fast.PadName} fast\n";

        _keyboardHelp = move +
            $"Hold {ModControls.Look.KeyName} look   {ModControls.Paint.KeyName} paint   " +
            $"Scroll or {Keys(ModControls.BrushSmaller, ModControls.BrushBigger)} brush size\n" +
            $"{ModControls.CycleBrush.KeyName} or {Keys(ModControls.AllowBrush, ModControls.DenyBrush, ModControls.EraseBrush)} allow/deny/erase   " +
            $"{ModControls.SetStart.KeyName} set start (facing = camera)\n" +
            $"{ModControls.SetFinish.KeyName} set finish (brush size, at least 1 m)\n" +
            $"Ctrl+{ModControls.Undo.KeyName} undo   Ctrl+{ModControls.Save.KeyName} save   {ModControls.PlayTest.KeyName} play-test   " +
            $"{ModControls.LevelSelect.KeyName} level select   {ModControls.Exit.KeyName} exit\n" +
            $"{ModControls.ToggleFreecam.KeyName} freecam";

        _gamepadHelp = padMove +
            $"{ModControls.Paint.PadName} paint   {Pads(ModControls.BrushBigger, ModControls.BrushSmaller)} brush size   " +
            $"{ModControls.CycleBrush.PadName} allow/deny/erase\n" +
            $"{ModControls.SetStart.PadName} set start   {ModControls.SetFinish.PadName} set finish (brush size)   {ModControls.Undo.PadName} undo\n" +
            $"{ModControls.Save.PadName} save   {ModControls.PlayTest.PadName} play-test\n" +
            $"{ModControls.LevelSelect.PadName} level select   {ModControls.Exit.PadName} or L3+R3 exit   {ModControls.ToggleFreecam.PadName} freecam";

        _freecamKeyboardHelp = move +
            $"Hold {ModControls.Look.KeyName} look   {ModControls.Teleport.KeyName} move Ava to the marker\n" +
            $"{ModControls.PlayTest.KeyName} play-test   {ModControls.LevelSelect.KeyName} level select   " +
            $"{ModControls.Exit.KeyName} exit   {ModControls.ToggleFreecam.KeyName} route editor";

        _freecamGamepadHelp = padMove +
            $"{ModControls.Teleport.PadName} move Ava to the marker   {ModControls.PlayTest.PadName} play-test\n" +
            $"{ModControls.LevelSelect.PadName} level select   {ModControls.Exit.PadName} or L3+R3 exit   {ModControls.ToggleFreecam.PadName} route editor";
    }

    private static string Keys(params Control[] controls) => string.Join("/", controls.Select(c => c.KeyName));

    private static string Pads(params Control[] controls) => string.Join("/", controls.Select(c => c.PadName));

    public static void Draw(RouteEditor editor)
    {
        RefreshHelp();
        if (editor.IsOpen && !editor.LevelSelect.IsOpen)
        {
            if (editor.Freecam)
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
            $"ROUTE EDITOR - {RouteName(route)}\n" +
            $"Brush: {mode}  radius {editor.Radius:0.00} m" +
            $"{(editor.HasBrushTarget ? "" : "  (no rock under brush)")}\n" +
            $"Strokes: {route.Strokes.Count}  stamps: {stamps}  start: {(route.Start == null ? "not set" : "set")}" +
            $"  finish: {(route.Finish == null ? "not set" : "set")}\n\n" +
            (editor.Input.UsingGamepad ? _gamepadHelp : _keyboardHelp);
        GUI.Box(new Rect(Screen.width - 490, 20, 470, 184), text);
        DrawReticle(editor);
    }

    private static void DrawFreecamPanel(RouteEditor editor)
    {
        var text =
            $"FREECAM - {RouteName(editor.Route)}\n" +
            $"{(editor.HasBrushTarget ? "" : "No rock under the marker")}\n\n" +
            (editor.Input.UsingGamepad ? _freecamGamepadHelp : _freecamKeyboardHelp);
        GUI.Box(new Rect(Screen.width - 490, 20, 470, 104), text);
        DrawReticle(editor);
    }

    /// <summary>"Route 2 · Well", or "no route".</summary>
    private static string RouteName(Route route) => route == null ? "no route" : $"{route.Name} · {ZoneNames.PrettyKey(route.ZoneKey)}";

    private static void DrawReticle(RouteEditor editor)
    {
        if (editor.Input.UsingGamepad && !editor.Looking)
        {
            // With a gamepad the brush aims at the middle of the screen.
            GUI.Label(new Rect(Screen.width * 0.5f - 6, Screen.height * 0.5f - 11, 20, 22), "+");
        }
    }
}
