using System.Collections.Generic;
using CairnRouteMaker.Controls;
using CairnRouteMaker.Game;
using CairnRouteMaker.Routes;
using Il2CppTMPro;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace CairnRouteMaker.Editor;

/// <summary>
/// The route editor's and freecam's on-screen help, drawn the way the game draws its own control
/// hints: a column of button pictures on the left, each with what it does beside it in the game's
/// font, for the device in use and the current bindings. Above it in the top left are the mode, the
/// route and what the editor is doing. It's a uGUI canvas, so it's driven from Update: call
/// <see cref="Show"/> every frame the editor is open, and <see cref="EndFrame"/> hides it on the
/// first frame nobody did.
/// </summary>
internal static class ControlsHud
{
    // Layout, in a 1920x1080 reference space; the canvas is scaled to the screen height.
    private const float ReferenceHeight = 1080f;
    private const float Left = 72f;
    private const float Top = 44f;

    /// <summary>The control column ends here, clear of the status line at the bottom middle.</summary>
    private const float ColumnBottom = ReferenceHeight - 104f;

    /// <summary>The control column doesn't go above this, so it stays clear of the text in the top left.</summary>
    private const float ColumnTop = 236f;

    private const float GlyphHeight = 32f;
    private const float GlyphGap = 5f;
    private const float JoinerWidth = 20f;
    private const float LabelGap = 16f;
    private const float RowPitch = 40f;
    private const float GroupGap = 18f;

    /// <summary>Labels are this many times the menu font size.</summary>
    private const float LabelSize = 0.55f;

    private const float FadeSeconds = 0.2f;
    private const float GlyphCheckSeconds = 1f;

    /// <summary>How many times to look again for button pictures that weren't loaded.</summary>
    private const int GlyphRetries = 30;

    /// <summary>The colour of the letters on the game's key pictures.</summary>
    private static readonly Color KeyLetters = Rgb(57, 60, 57);

    /// <summary>The colour of the game's key pictures, for the key cap drawn when they can't be found.</summary>
    private static readonly Color KeyFace = Rgb(244, 244, 222);

    private static readonly Color Warning = new(1f, 0.72f, 0.45f, 1f);

    private static GameObject _root;
    private static Canvas _canvas;
    private static CanvasGroup _group;
    private static RectTransform _column;
    private static TextMeshProUGUI _mode;
    private static TextMeshProUGUI _title;
    private static TextMeshProUGUI _tool;
    private static TextMeshProUGUI _counts;
    private static TextMeshProUGUI _warning;
    private static readonly List<Image> Glyphs = new();
    private static readonly Dictionary<int, Sprite> Stretchable = new();

    private static MenuStyle _style;
    private static int _shownFrame = -1;
    private static float _shownSince;
    private static (Mode Mode, bool Gamepad, int Version, int Pad)? _built;
    private static bool _glyphsMissing;
    private static int _glyphRetries;
    private static float _nextGlyphCheck;
    private static bool _failed;
    private static Sprite _keyCap;
    private static Sprite _gradient;

    private enum Mode
    {
        Freecam,
        Brush,
        Line,
    }

    /// <summary>One thing drawn before a hint's label: a control's button, a fixed control, or a "/" or "+" between them.</summary>
    private readonly struct Part
    {
        private Part(Control control, bool pad, string path, string name, string joiner)
        {
            Control = control;
            Pad = pad;
            Path = path;
            Name = name;
            Joiner = joiner;
        }

        public Control Control { get; }
        public bool Pad { get; }

        /// <summary>A control that isn't one of the mod's bindings, e.g. "&lt;Gamepad&gt;/leftStick".</summary>
        public string Path { get; }

        /// <summary>What to write on a blank key cap for <see cref="Path"/>.</summary>
        public string Name { get; }

        public string Joiner { get; }

        public static Part Of(Control control, bool pad) => new(control, pad, null, null, null);

        public static Part Fixed(string path, string name) => new(null, false, path, name, null);

        public static Part Join(string joiner) => new(null, false, null, null, joiner);
    }

    private sealed class Hint
    {
        public Hint(string label, Part[] parts)
        {
            Label = label;
            Parts = parts;
        }

        public string Label { get; }
        public Part[] Parts { get; }
    }

    private static readonly Part Or = Part.Join("/");
    private static readonly Part Plus = Part.Join("+");
    private static readonly Part Ctrl = Part.Fixed("<Keyboard>/ctrl", "Ctrl");

    /// <summary>Show the help this frame, for the editor's mode and the device in use.</summary>
    public static void Show(RouteEditor editor)
    {
        if (_failed)
        {
            return;
        }

        try
        {
            ShowUnchecked(editor);
        }
        catch (System.Exception e)
        {
            // It would fail the same way every frame; the editor still works without its help.
            _failed = true;
            MelonLogger.Error($"Couldn't draw the controls help: {e}");
            if (_root != null)
            {
                _root.SetActive(false);
            }
        }
    }

    private static void ShowUnchecked(RouteEditor editor)
    {
        if (_root == null)
        {
            Build();
        }

        if (_shownFrame != Time.frameCount - 1 || !_root.activeSelf)
        {
            _shownSince = Time.unscaledTime;
            _root.SetActive(true);
        }

        _shownFrame = Time.frameCount;
        _canvas.scaleFactor = Screen.height / ReferenceHeight;
        _group.alpha = Mathf.Clamp01((Time.unscaledTime - _shownSince) / FadeSeconds);

        var mode = editor.Freecam ? Mode.Freecam : editor.LineTool ? Mode.Line : Mode.Brush;
        var gamepad = editor.Input.UsingGamepad;
        var built = (mode, gamepad, ModControls.Version, gamepad ? Gamepad.current?.deviceId ?? 0 : 0);
        if (_built != built)
        {
            _built = built;
            _glyphRetries = 0;
            BuildColumn(Hints(mode, gamepad), gamepad, log: true);
        }
        else if (NeedsGlyphCheck())
        {
            _glyphRetries++;
            BuildColumn(Hints(mode, gamepad), gamepad, log: !_glyphsMissing);
        }

        ShowStatus(editor, mode);
    }

    /// <summary>Hide the help if <see cref="Show"/> wasn't called this frame. Call from LateUpdate.</summary>
    public static void EndFrame()
    {
        if (_root != null && _shownFrame != Time.frameCount && _root.activeSelf)
        {
            _root.SetActive(false);
        }
    }

    /// <summary>
    /// Every so often, see whether a button picture couldn't be found (the game's pictures weren't
    /// loaded yet) or has been unloaded since, and if so build the column again.
    /// </summary>
    private static bool NeedsGlyphCheck()
    {
        if (Time.unscaledTime < _nextGlyphCheck)
        {
            return false;
        }

        _nextGlyphCheck = Time.unscaledTime + GlyphCheckSeconds;
        if (_glyphRetries >= GlyphRetries)
        {
            return false;
        }

        if (_glyphsMissing)
        {
            return true;
        }

        foreach (var image in Glyphs)
        {
            if (image.sprite == null)
            {
                return true;
            }
        }

        return false;
    }

    private static List<Hint[]> Hints(Mode mode, bool gamepad)
    {
        Part P(Control control) => Part.Of(control, gamepad);
        Hint H(string label, params Part[] parts) => new(label, parts);

        var groups = new List<Hint[]>
        {
            gamepad
                ? new[]
                {
                    H("Move", Part.Fixed("<Gamepad>/leftStick", "LS")),
                    H("Look", Part.Fixed("<Gamepad>/rightStick", "RS")),
                    H("Down / up", P(ModControls.MoveDown), Or, P(ModControls.MoveUp)),
                    H("Fast (hold)", P(ModControls.Fast)),
                }
                : new[]
                {
                    H("Move", P(ModControls.MoveForward), P(ModControls.MoveLeft), P(ModControls.MoveBack), P(ModControls.MoveRight)),
                    H("Down / up", P(ModControls.MoveDown), Or, P(ModControls.MoveUp)),
                    H("Fast / medium speed (hold)", P(ModControls.Fast), Or, P(ModControls.Medium)),
                    H("Look (hold)", P(ModControls.Look)),
                },
        };

        switch (mode)
        {
            case Mode.Freecam:
                groups.Add(new[] { H("Move Ava to the marker", P(ModControls.Teleport)) });
                break;
            case Mode.Brush:
                groups.Add(gamepad
                    ? new[]
                    {
                        H("Paint", P(ModControls.Paint)),
                        H("Brush size", P(ModControls.BrushSmaller), Or, P(ModControls.BrushBigger)),
                        H("Allow / deny / erase / line", P(ModControls.CycleBrush)),
                    }
                    : new[]
                    {
                        H("Paint", P(ModControls.Paint)),
                        H("Brush size", Part.Fixed("<Mouse>/scroll", "Scroll"), Or, P(ModControls.BrushSmaller), P(ModControls.BrushBigger)),
                        H("Next tool", P(ModControls.CycleBrush)),
                        H("Allow / deny / erase / line", P(ModControls.AllowBrush), P(ModControls.DenyBrush), P(ModControls.EraseBrush), P(ModControls.LineTool)),
                    });
                break;
            case Mode.Line:
                groups.Add(gamepad
                    ? new[]
                    {
                        H("Add a point, or drag a point or the line", P(ModControls.Paint)),
                        H("Rappel", P(ModControls.Rappel)),
                        H("Line colour (hold: new line)", P(ModControls.LineColor)),
                        H("Delete point (hold: delete line)", P(ModControls.DeletePoint)),
                        H("Next tool", P(ModControls.CycleBrush)),
                    }
                    : new[]
                    {
                        H("Add a point, or drag a point or the line", P(ModControls.Paint)),
                        H("Rappel", P(ModControls.Rappel)),
                        H("New line", P(ModControls.NewLine)),
                        H("Line colour", P(ModControls.LineColor)),
                        H("Delete point / line", P(ModControls.DeletePoint), Or, Ctrl, Plus, P(ModControls.DeletePoint)),
                        H("Next tool", P(ModControls.CycleBrush)),
                        H("Allow / deny / erase / line", P(ModControls.AllowBrush), P(ModControls.DenyBrush), P(ModControls.EraseBrush), P(ModControls.LineTool)),
                    });
                break;
        }

        if (mode != Mode.Freecam)
        {
            groups.Add(gamepad
                ? new[]
                {
                    H("Set start (facing the camera's way)", P(ModControls.SetStart)),
                    H("Set finish (brush size)", P(ModControls.SetFinish)),
                    H("Undo", P(ModControls.Undo)),
                    H("Save", P(ModControls.Save)),
                }
                : new[]
                {
                    H("Set start (facing the camera's way)", P(ModControls.SetStart)),
                    H("Set finish (brush size, at least 1 m)", P(ModControls.SetFinish)),
                    H("Show / hide route lines", P(ModControls.ShowLine)),
                    H("Undo", Ctrl, Plus, P(ModControls.Undo)),
                    H("Save", Ctrl, Plus, P(ModControls.Save)),
                });
        }

        groups.Add(new[]
        {
            H("Play-test", P(ModControls.PlayTest)),
            H("Level select", P(ModControls.LevelSelect)),
            H(mode == Mode.Freecam ? "Route editor" : "Freecam", P(ModControls.ToggleFreecam)),
            gamepad
                ? H("Exit", P(ModControls.Exit), Or, Part.Fixed("<Gamepad>/leftStickPress", "L3"), Plus, Part.Fixed("<Gamepad>/rightStickPress", "R3"))
                : H("Exit", P(ModControls.Exit)),
        });
        return groups;
    }

    /// <summary>
    /// Lay the hints out bottom up from <see cref="ColumnBottom"/>, the button pictures right-aligned
    /// to one edge and the labels left-aligned after it. A long list is squeezed to fit.
    /// </summary>
    private static void BuildColumn(List<Hint[]> groups, bool gamepad, bool log)
    {
        for (var i = _column.childCount - 1; i >= 0; i--)
        {
            Object.Destroy(_column.GetChild(i).gameObject);
        }

        Glyphs.Clear();
        _glyphsMissing = false;

        var rows = 0;
        foreach (var group in groups)
        {
            rows += group.Length;
        }

        var height = rows * RowPitch + (groups.Count - 1) * GroupGap;
        var squeeze = Mathf.Min(1f, (ColumnBottom - ColumnTop) / height);
        var pitch = RowPitch * squeeze;
        var gap = GroupGap * squeeze;
        var glyphHeight = Mathf.Min(GlyphHeight, pitch - 4f);
        var labelSize = _style.FontSize * LabelSize * Mathf.Lerp(0.85f, 1f, squeeze);

        // Build every row's pictures first, so the labels can line up after the widest.
        var built = new List<(RectTransform Row, float Width, string Label)>();
        foreach (var group in groups)
        {
            foreach (var hint in group)
            {
                var row = NewRect("Row", _column);
                var width = BuildParts(row, hint.Parts, glyphHeight, pitch, labelSize);
                built.Add((row, width, hint.Label));
            }
        }

        var edge = 0f;
        foreach (var (_, width, _) in built)
        {
            edge = Mathf.Max(edge, width);
        }

        var y = ColumnBottom - height * squeeze;
        var index = 0;
        foreach (var group in groups)
        {
            for (var i = 0; i < group.Length; i++)
            {
                var (row, width, label) = built[index++];
                Place(row, Left + edge - width, y, width, pitch);
                var text = NewText("Label", _column);
                Place(text.rectTransform, Left + edge + LabelGap, y, 900f, pitch);
                text.alignment = TextAlignmentOptions.MidlineLeft;
                SetText(text, label, labelSize, _style.Selected);
                y += pitch;
            }

            y += gap;
        }

        if (log)
        {
            MelonLogger.Msg($"Controls help: {(gamepad ? "gamepad" : "keyboard")}, {rows} rows{(_glyphsMissing ? ", some button pictures missing" : "")}.");
        }
    }

    /// <summary>Lay out one hint's pictures left to right in its row; returns their width.</summary>
    private static float BuildParts(RectTransform row, Part[] parts, float height, float pitch, float labelSize)
    {
        var x = 0f;
        var previousWasGlyph = false;
        foreach (var part in parts)
        {
            if (part.Joiner != null)
            {
                var joiner = NewText("Joiner", row);
                Place(joiner.rectTransform, x, 0f, JoinerWidth, pitch);
                joiner.alignment = TextAlignmentOptions.Center;
                SetText(joiner, part.Joiner, labelSize, WithAlpha(_style.NotSelected, 0.8f));
                x += JoinerWidth;
                previousWasGlyph = false;
                continue;
            }

            if (!TryResolve(part, out var layout, out var path, out var name))
            {
                continue;
            }

            if (previousWasGlyph)
            {
                x += GlyphGap;
            }

            x += BuildGlyph(row, x, height, pitch, layout, path, name);
            previousWasGlyph = true;
        }

        return x;
    }

    /// <summary>The device layout and control path of a part, and the name to write on a blank key cap.</summary>
    private static bool TryResolve(Part part, out string layout, out string path, out string name)
    {
        layout = path = name = null;
        if (part.Control == null)
        {
            // "<Gamepad>/leftStick" → "Gamepad", "leftStick".
            var close = part.Path.IndexOf('>');
            layout = part.Path.Substring(1, close - 1);
            path = part.Path.Substring(close + 1).TrimStart('/');
            name = part.Name;
            return true;
        }

        var action = part.Pad ? part.Control.Gamepad : part.Control.Keyboard;
        if (action == null)
        {
            return false;
        }

        try
        {
            name = InputActionRebindingExtensions.GetBindingDisplayString(action, 0, out layout, out path, default(InputBinding.DisplayStringOptions));
        }
        catch (System.Exception)
        {
            name = part.Pad ? part.Control.PadName : part.Control.KeyName;
        }

        return true;
    }

    /// <summary>One button picture at x in the row, vertically centred; returns its width.</summary>
    private static float BuildGlyph(RectTransform row, float x, float height, float pitch, string layout, string path, string name)
    {
        var sprite = ButtonGlyphs.Find(layout, path, blank: true, out var blank);
        if (sprite == null)
        {
            // The game's pictures aren't loaded: draw a key cap of our own and look again soon.
            _glyphsMissing = true;
            sprite = _keyCap ??= KeyCapSprite();
            blank = true;
        }

        var rect = NewRect("Glyph", row);
        var image = rect.gameObject.AddComponent<Image>();
        image.raycastTarget = false;
        var aspect = sprite.rect.height > 0f ? sprite.rect.width / sprite.rect.height : 1f;
        var width = height * aspect;
        TextMeshProUGUI letters = null;
        if (blank)
        {
            letters = NewText("Letters", rect);
            letters.alignment = TextAlignmentOptions.Center;
            letters.richText = false;
            letters.fontStyle = FontStyles.SmallCaps | FontStyles.Bold;
            StyleKeyLetters(letters, height * 0.5f);
            letters.text = string.IsNullOrEmpty(name) ? "?" : name;

            // Wider than a square key (e.g. "Page Up"): stretch the cap's middle, keeping its corners.
            var textWidth = letters.GetPreferredValues(letters.text).x;
            var needed = textWidth + height * 0.55f;
            if (needed > width)
            {
                width = needed;
                var stretchable = StretchableSprite(sprite);
                if (stretchable != null)
                {
                    image.sprite = stretchable;
                    image.type = Image.Type.Sliced;
                    image.pixelsPerUnitMultiplier = stretchable.rect.height / height;
                }
            }
        }

        if (image.sprite == null)
        {
            image.sprite = sprite;
            image.preserveAspect = !blank;
        }

        image.color = sprite == _keyCap ? KeyFace : Color.white;
        Place(rect, x, (pitch - height) / 2f, width, height);
        if (letters != null)
        {
            Place(letters.rectTransform, 0f, 0f, width, height);
        }

        if (sprite != _keyCap)
        {
            Glyphs.Add(image);
        }

        return width;
    }

    /// <summary>A copy of a key cap picture that a sliced Image can stretch, or null if it can't be made.</summary>
    private static Sprite StretchableSprite(Sprite source)
    {
        var id = source.GetInstanceID();
        if (Stretchable.TryGetValue(id, out var cached) && cached != null)
        {
            return cached;
        }

        try
        {
            var area = source.textureRect;
            var border = Mathf.Floor(Mathf.Min(area.width, area.height) * 0.4f);
            var sprite = RuntimeAssets.Keep(Sprite.Create(source.texture, area, new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(border, border, border, border)));
            sprite.name = source.name + " (stretchable)";
            Stretchable[id] = sprite;
            return sprite;
        }
        catch (System.Exception e)
        {
            MelonLogger.Warning($"Couldn't make a wide key cap from {source.name}: {e.Message}");
            Stretchable[id] = null;
            return null;
        }
    }

    /// <summary>The mode, route and what the editor is doing, in the top left.</summary>
    private static void ShowStatus(RouteEditor editor, Mode mode)
    {
        var route = editor.Route;
        SetIfChanged(_mode, mode == Mode.Freecam ? "Freecam" : "Route editor");
        SetIfChanged(_title, route == null ? "No route" : $"{route.Name} · {ZoneNames.PrettyKey(route.ZoneKey)}");
        SetIfChanged(_tool, mode switch
        {
            Mode.Freecam => null,
            Mode.Line => $"Line tool: {LineSummary(route, editor.ActiveLine, editor.SelectedPoint)}",
            _ => $"{BrushName(editor.Mode)} brush, {editor.Radius:0.00} m",
        });
        SetIfChanged(_counts, mode == Mode.Freecam || route == null ? null : Counts(route));
        SetIfChanged(_warning, editor.HasBrushTarget ? null : mode == Mode.Freecam ? "No rock under the marker" : "No rock under the brush");
    }

    private static string BrushName(StrokeMode mode) => mode switch
    {
        StrokeMode.Allow => $"<color=#{ColorUtility.ToHtmlStringRGB(PaintColors.Allow)}>Allow</color>",
        StrokeMode.Deny => $"<color=#{ColorUtility.ToHtmlStringRGB(PaintColors.Deny)}>Deny</color>",
        _ => "Erase",
    };

    private static string Counts(Route route)
    {
        var stamps = 0;
        foreach (var stroke in route.Strokes)
        {
            stamps += stroke.Stamps.Count;
        }

        return $"{Plural(route.Strokes.Count, "stroke")} · {Plural(stamps, "stamp")} · {Plural(route.Lines.Count, "line")} · "
            + $"start {(route.Start == null ? "not set" : "set")} · finish {(route.Finish == null ? "not set" : "set")}";
    }

    private static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    private static string LineSummary(Route route, int active, int selected)
    {
        if (route == null || active < 0 || active >= route.Lines.Count)
        {
            return "no lines yet";
        }

        var line = route.Lines[active];
        var colour = ColorUtility.ToHtmlStringRGB(LineColors.Of(line.Grade));
        var name = $"line {active + 1} of {route.Lines.Count} (<color=#{colour}>{LineColors.NameOf(line.Grade).ToLowerInvariant()}</color>)";
        var points = line.Points.Count;
        if (points == 0)
        {
            return $"{name}, no points yet";
        }

        var which = selected >= 0 && selected < points ? selected + 1 : points;
        return $"{name}, {Plural(points, "point")}, point {which} selected";
    }

    private static void Build()
    {
        _style = MenuStyle.Current;
        _built = null;

        _root = new GameObject("CairnRouteMaker.ControlsHud");
        Object.DontDestroyOnLoad(_root);
        _root.layer = 5; // UI
        _canvas = _root.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        // Under the level select, which takes the screen over.
        _canvas.sortingOrder = 31000;
        _group = _root.AddComponent<CanvasGroup>();
        _group.interactable = false;
        _group.blocksRaycasts = false;

        // A soft shade down the left keeps the text readable over snow and sky.
        var shade = NewRect("Shade", _root.transform);
        shade.anchorMin = new Vector2(0f, 0f);
        shade.anchorMax = new Vector2(0f, 1f);
        shade.pivot = new Vector2(0f, 0.5f);
        shade.sizeDelta = new Vector2(820f, 0f);
        var shadeImage = shade.gameObject.AddComponent<Image>();
        shadeImage.sprite = _gradient ??= GradientSprite();
        shadeImage.color = Color.black;
        shadeImage.raycastTarget = false;

        _mode = NewText("Mode", _root.transform);
        Place(_mode.rectTransform, Left, Top, 900f, 26f);
        _mode.fontStyle = FontStyles.UpperCase;
        _mode.characterSpacing = 12f;
        Style(_mode, _style.FontSize * 0.45f, WithAlpha(_style.NotSelected, 0.75f));

        _title = NewText("Title", _root.transform);
        Place(_title.rectTransform, Left, Top + 26f, 900f, 52f);
        // Route names are typed by players.
        _title.richText = false;
        Style(_title, _style.FontSize * 0.85f, _style.Selected);

        _tool = NewText("Tool", _root.transform);
        Place(_tool.rectTransform, Left, Top + 80f, 900f, 30f);
        Style(_tool, _style.FontSize * 0.55f, _style.Selected);

        _counts = NewText("Counts", _root.transform);
        Place(_counts.rectTransform, Left, Top + 110f, 900f, 28f);
        Style(_counts, _style.FontSize * 0.48f, WithAlpha(_style.NotSelected, 0.7f));

        _warning = NewText("Warning", _root.transform);
        Place(_warning.rectTransform, Left, Top + 138f, 900f, 28f);
        Style(_warning, _style.FontSize * 0.48f, Warning);

        _column = NewRect("Controls", _root.transform);
        Place(_column, 0f, 0f, 0f, 0f);

        _root.SetActive(false);
    }

    /// <summary>Set a status line's text, hiding it when there's none; touches TextMeshPro only on a change.</summary>
    private static void SetIfChanged(TextMeshProUGUI text, string value)
    {
        value ??= "";
        if (text.text != value)
        {
            text.text = value;
        }

        var show = value.Length > 0;
        if (text.gameObject.activeSelf != show)
        {
            text.gameObject.SetActive(show);
        }
    }

    private static void SetText(TextMeshProUGUI text, string value, float size, Color color)
    {
        Style(text, size, color);
        text.text = value ?? "";
    }

    /// <summary>The menu's font and drop-shadow material, in a size and colour.</summary>
    private static void Style(TextMeshProUGUI text, float size, Color color)
    {
        if (_style.Font != null && text.font != _style.Font)
        {
            text.font = _style.Font;
        }

        if (_style.Material != null)
        {
            text.fontSharedMaterial = _style.Material;
        }

        text.fontSize = size;
        text.color = color;
    }

    /// <summary>The menu's font without its drop shadow, dark like the letters on the game's key pictures.</summary>
    private static void StyleKeyLetters(TextMeshProUGUI text, float size)
    {
        if (_style.Font != null)
        {
            text.font = _style.Font;
            text.fontSharedMaterial = _style.Font.material;
        }

        text.fontSize = size;
        text.color = KeyLetters;
    }

    private static Color WithAlpha(Color color, float alpha) => new(color.r, color.g, color.b, alpha);

    private static Color Rgb(byte r, byte g, byte b) => new Color32(r, g, b, 255);

    private static RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name) { layer = 5 };
        var rect = go.AddComponent<RectTransform>();
        rect.SetParent(parent, false);
        return rect;
    }

    private static TextMeshProUGUI NewText(string name, Transform parent)
    {
        var text = NewRect(name, parent).gameObject.AddComponent<TextMeshProUGUI>();
        text.raycastTarget = false;
        text.richText = true;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Overflow;
        text.alignment = TextAlignmentOptions.MidlineLeft;
        return text;
    }

    /// <summary>Anchor to the parent's top left; y counts down from the top.</summary>
    private static void Place(RectTransform rect, float x, float y, float width, float height)
    {
        rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(x, -y);
        rect.sizeDelta = new Vector2(width, height);
    }

    /// <summary>Darkest on the left, fading to clear on the right, tinted by the image colour.</summary>
    private static Sprite GradientSprite()
    {
        const int width = 256;
        var texture = RuntimeAssets.Keep(new Texture2D(width, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear });
        for (var x = 0; x < width; x++)
        {
            var fade = 1f - Mathf.SmoothStep(0f, 1f, x / (width - 1f));
            texture.SetPixel(x, 0, new Color(1f, 1f, 1f, 0.35f * fade * fade));
        }

        texture.Apply();
        return RuntimeAssets.Keep(Sprite.Create(texture, new Rect(0f, 0f, width, 1f), new Vector2(0.5f, 0.5f)));
    }

    /// <summary>
    /// A white rounded square with stretchable corners, tinted like a key cap, for when the game's
    /// pictures can't be found.
    /// </summary>
    private static Sprite KeyCapSprite()
    {
        const int size = 64;
        const float radius = 14f;
        var texture = RuntimeAssets.Keep(new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear });
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                // Distance outside a rounded square inset by the corner radius.
                var dx = Mathf.Max(Mathf.Abs(x + 0.5f - size / 2f) - (size / 2f - radius), 0f);
                var dy = Mathf.Max(Mathf.Abs(y + 0.5f - size / 2f) - (size / 2f - radius), 0f);
                var alpha = Mathf.Clamp01(radius - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
                texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
        }

        texture.Apply();
        return RuntimeAssets.Keep(Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(20f, 20f, 20f, 20f)));
    }
}
