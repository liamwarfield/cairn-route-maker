using UnityEngine;

namespace CairnRouteMaker.Editor;

/// <summary>
/// The status line and gamepad reticle, drawn with IMGUI. The controls help is
/// <see cref="ControlsHud"/>, and the level select is <see cref="LevelSelectView"/>.
/// </summary>
internal static class EditorHud
{
    private const float StatusSeconds = 4f;

    public static void Draw(RouteEditor editor)
    {
        if (editor.IsOpen && !editor.LevelSelect.IsOpen)
        {
            DrawReticle(editor);
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

    private static void DrawReticle(RouteEditor editor)
    {
        if (editor.Input.UsingGamepad && !editor.Looking)
        {
            // With a gamepad the brush aims at the middle of the screen.
            GUI.Label(new Rect(Screen.width * 0.5f - 6, Screen.height * 0.5f - 11, 20, 22), "+");
        }
    }
}
