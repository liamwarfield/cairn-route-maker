using System.Collections.Generic;
using CairnRouteMaker.Controls;
using CairnRouteMaker.Game;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace CairnRouteMaker.Editor;

/// <summary>
/// Draws the level select / free-climb launcher the way the title screen draws its menu: big
/// left-aligned labels in the game's font, the selected one sliding right in the selected colour,
/// with the bouncing arrow beside it. A soft dark gradient on the left keeps it readable over the
/// world. It's a uGUI canvas, so it's driven from Update: call <see cref="Show"/> every frame the menu
/// is open, and <see cref="EndFrame"/> hides it on the first frame nobody did.
/// A page with a <see cref="TextEntry"/> shows its text in place of the list, and with a gamepad an
/// on-screen keyboard under it.
/// </summary>
internal static class LevelSelectView
{
    // Layout, in a 1920x1080 reference space; the canvas is scaled to the screen height.
    private const float ReferenceHeight = 1080f;
    private const float Left = 150f;
    private const float ListTop = 350f;
    private const float ListBottomMargin = 130f;
    private const float ListWidth = 1100f;
    private const float RowHeightPerFontSize = 1.55f;
    private const float HeaderHeight = 46f;
    private const float HeaderGap = 18f;
    private const float NoteGap = 18f;

    /// <summary>Colour swatch side and the gap after each, as fractions of the font size.</summary>
    private const float SwatchSize = 0.55f;
    private const float SwatchGap = 0.25f;
    private const int MaxSwatches = 3;

    /// <summary>The list fades out over this much at its top and bottom edges.</summary>
    private const float Fade = 44f;
    private const float PageFadeSeconds = 0.18f;
    /// <summary>One bounce: out and back.</summary>
    private const float BounceSeconds = 0.45f;
    private const float DisabledAlpha = 0.4f;

    // The text box and on-screen keyboard of a page with a text entry.
    private const float FieldHeightPerFontSize = 1.6f;
    private const float KeyboardTop = ListTop + 140f;
    private const float KeyWidth = 64f;
    private const float KeyHeight = 54f;
    private const float KeyGap = 8f;
    private const float CaretBlinkSeconds = 0.53f;
    private static readonly Color KeyLabelOnSelected = new(0.1f, 0.1f, 0.13f, 1f);
    private static readonly Color ErrorColor = new(1f, 0.6f, 0.55f, 1f);

    private static GameObject _root;
    private static Canvas _canvas;
    private static CanvasGroup _group;
    private static RectTransform _viewport;
    private static RectTransform _content;
    private static RectTransform _arrow;
    private static Image _arrowImage;
    private static TextMeshProUGUI _breadcrumb;
    private static TextMeshProUGUI _title;
    private static TextMeshProUGUI _description;
    private static TextMeshProUGUI _hints;
    private static TextMeshProUGUI _field;
    private static Image _fieldLine;
    private static TextMeshProUGUI _fieldNote;
    private static RectTransform _keyboard;
    private static readonly List<KeyView> KeyViews = new();
    private static readonly List<Row> Rows = new();
    private static int _rowCount;
    private static float _contentHeight;

    private static MenuStyle _style;
    private static LevelSelect _shownMenu;
    private static int _shownPage = -1;
    private static int _shownFrame = -1;
    private static (bool Gamepad, int Version, bool Typing, bool Editable)? _shownHints;
    private static string _shownDescription;
    private static float _scroll;
    private static float _arrowY;
    private static int _bouncedFor = -1;
    private static float _bounceStart;
    private static float _pageTime;
    private static Vector2 _lastPointer;
    private static Sprite _fallbackArrow;
    private static Sprite _gradient;

    private sealed class Row
    {
        public RectTransform Rect;
        public TextMeshProUGUI Label;
        public TextMeshProUGUI Note;
        public Image[] Swatches;
        public float Top;
        public float Height;
        public bool Header;
        public bool Enabled;

        /// <summary>0 = not selected, 1 = selected; eased between the two.</summary>
        public float Selection;
    }

    private sealed class KeyView
    {
        public TextEntry.Key Key;
        public Image Background;
        public TextMeshProUGUI Label;
    }

    private static float ViewportHeight => ReferenceHeight - ListTop - ListBottomMargin + Fade * 2f;

    /// <summary>
    /// Show the menu this frame. Pointer hover selects an entry and a click sets
    /// <see cref="LevelSelect.ClickedIndex"/>, so call this before handling the menu's input.
    /// </summary>
    public static void Show(LevelSelect menu, EditorInput input)
    {
        if (!menu.IsOpen)
        {
            return;
        }

        if (_root == null)
        {
            Build();
        }

        _shownFrame = Time.frameCount;
        if (!_root.activeSelf)
        {
            _root.SetActive(true);
            _shownPage = -1;
        }

        _canvas.scaleFactor = Screen.height / ReferenceHeight;
        if (menu != _shownMenu || menu.Page != _shownPage)
        {
            Rebuild(menu, input);
        }

        ShowHints(menu, input.UsingGamepad);
        ShowDescription(menu);
        HandlePointer(menu, input);
        Animate(menu);
        ShowTextEntry(menu.TextEntry, input.UsingGamepad);
    }

    /// <summary>Hide the menu if <see cref="Show"/> wasn't called this frame. Call from LateUpdate.</summary>
    public static void EndFrame()
    {
        if (_root != null && _shownFrame != Time.frameCount && _root.activeSelf)
        {
            _root.SetActive(false);
            _shownMenu = null;
        }
    }

    /// <summary>The line under the title: the highlighted entry's detail if it has one, such as a route's description, or else the page's.</summary>
    private static void ShowDescription(LevelSelect menu)
    {
        var description = menu.Current?.Detail ?? menu.Description;
        if (description != _shownDescription)
        {
            _shownDescription = description;
            SetText(_description, description, _style.FontSize * 0.6f, WithAlpha(_style.NotSelected, 0.8f));
        }
    }

    /// <summary>The controls line at the bottom, for the device in use and what the page can do.</summary>
    private static void ShowHints(LevelSelect menu, bool gamepad)
    {
        var typing = menu.TextEntry != null;
        var editable = !typing && menu.Current?.Edit != null;
        var shown = (gamepad, ModControls.Version, typing, editable);
        if (_shownHints == shown)
        {
            return;
        }

        _shownHints = shown;
        string hints;
        if (typing)
        {
            hints = gamepad
                ? Hint("A", "Type") + Hint("X", "Delete") + Hint("Y", "Space") + Hint("LT", "Shift") + Hint("LB/RB", "Move cursor")
                    + Hint("Menu", "Done") + Hint(ModControls.Exit.PadName, "Cancel")
                : Hint("Enter", "Done") + Hint("Escape", "Cancel");
        }
        else
        {
            hints = (gamepad ? Hint("D-pad", "Choose") + Hint("A", "Select") : Hint("Arrows", "Choose") + Hint("Enter", "Select"))
                + (editable ? Hint(gamepad ? ModControls.EditRoute.PadName : ModControls.EditRoute.KeyName, "Edit route") : "")
                + Hint(gamepad ? ModControls.Exit.PadName : ModControls.Exit.KeyName, "Back");
        }

        SetText(_hints, hints, _style.FontSize * 0.5f, WithAlpha(_style.NotSelected, 0.7f));
    }

    /// <summary>The page's text with its caret and how long it is, and the on-screen keyboard with a gamepad.</summary>
    private static void ShowTextEntry(TextEntry entry, bool gamepad)
    {
        var typing = entry != null;
        _field.gameObject.SetActive(typing);
        _fieldLine.gameObject.SetActive(typing);
        _fieldNote.gameObject.SetActive(typing);
        _keyboard.gameObject.SetActive(typing && gamepad);
        if (!typing)
        {
            return;
        }

        SetIfChanged(_field, FieldText(entry), _style.Selected);
        _fieldLine.color = WithAlpha(_style.Selected, 0.5f);
        SetIfChanged(_fieldNote, entry.Error ?? $"{entry.Text.Length}/{entry.MaxLength}",
            entry.Error != null ? ErrorColor : WithAlpha(_style.NotSelected, 0.6f));
        if (!gamepad)
        {
            return;
        }

        var highlighted = entry.Highlighted;
        foreach (var view in KeyViews)
        {
            var selected = view.Key == highlighted;
            var lit = view.Key.Kind == TextEntry.KeyKind.Shift && entry.Shift;
            view.Background.color = selected ? WithAlpha(_style.Selected, 0.9f) : new Color(0f, 0f, 0f, lit ? 0.6f : 0.35f);
            SetIfChanged(view.Label, view.Key.Label(entry.Shift), selected ? KeyLabelOnSelected : lit ? _style.Selected : _style.NotSelected);
        }
    }

    /// <summary>
    /// The text with a blinking caret; the user's text isn't read as rich text. Hidden, the caret
    /// is drawn clear rather than left out, so the text doesn't shift as it blinks.
    /// </summary>
    private static string FieldText(TextEntry entry)
    {
        var text = entry.Text;
        if (entry.AllSelected)
        {
            return $"<mark=#FFFFFF40><noparse>{text}</noparse></mark>";
        }

        var on = (Time.unscaledTime - entry.LastChange) % (CaretBlinkSeconds * 2f) < CaretBlinkSeconds;
        var caret = on ? "|" : "<color=#00000000>|</color>";
        return $"<noparse>{text[..entry.Caret]}</noparse>{caret}<noparse>{text[entry.Caret..]}</noparse>";
    }

    private static void SetIfChanged(TextMeshProUGUI text, string value, Color color)
    {
        if (text.text != value)
        {
            text.text = value;
        }

        text.color = color;
    }

    private static void HandlePointer(LevelSelect menu, EditorInput input)
    {
        var pointer = input.PointerPosition;
        var moved = (pointer - _lastPointer).sqrMagnitude > 1f;
        _lastPointer = pointer;
        if (input.UsingGamepad || (!moved && !input.MenuClick))
        {
            return;
        }

        var index = RowAt(pointer);
        if (index < 0)
        {
            return;
        }

        menu.Select(index);
        if (input.MenuClick)
        {
            menu.ClickedIndex = index;
        }
    }

    /// <summary>The choosable entry under a screen point (origin bottom left), or -1.</summary>
    private static int RowAt(Vector2 screen)
    {
        var scale = _canvas.scaleFactor;
        var x = screen.x / scale;
        var y = (Screen.height - screen.y) / scale;
        if (x < Left - 40f || x > Left + ListWidth || y < ListTop || y > ReferenceHeight - ListBottomMargin)
        {
            return -1;
        }

        var inContent = y - (ListTop - Fade) + _scroll;
        for (var i = 0; i < _rowCount; i++)
        {
            var row = Rows[i];
            if (!row.Header && row.Enabled && inContent >= row.Top && inContent < row.Top + row.Height)
            {
                return i;
            }
        }

        return -1;
    }

    private static void Animate(LevelSelect menu)
    {
        var dt = Time.unscaledDeltaTime;
        _pageTime += dt;
        _group.alpha = Mathf.Clamp01(_pageTime / PageFadeSeconds);

        var selected = menu.Selected >= 0 && menu.Selected < _rowCount ? menu.Selected : -1;
        var step = dt / Mathf.Max(_style.TransitionSeconds, 0.01f);
        for (var i = 0; i < _rowCount; i++)
        {
            var row = Rows[i];
            if (row.Header)
            {
                continue;
            }

            row.Selection = Mathf.MoveTowards(row.Selection, i == selected ? 1f : 0f, step);
            var eased = Mathf.SmoothStep(0f, 1f, row.Selection);
            row.Label.rectTransform.anchoredPosition = new Vector2(eased * _style.SelectedOffsetX, 0f);
            var color = row.Enabled ? Color.Lerp(_style.NotSelected, _style.Selected, eased) : WithAlpha(_style.NotSelected, DisabledAlpha);
            row.Label.color = color;
            row.Note.color = WithAlpha(color, color.a * 0.65f);
        }

        _scroll = Mathf.Lerp(_scroll, TargetScroll(selected), 1f - Mathf.Exp(-14f * dt));
        _content.anchoredPosition = new Vector2(0f, _scroll);

        if (selected < 0)
        {
            _arrow.gameObject.SetActive(false);
            return;
        }

        var target = RowCenter(Rows[selected]);
        _arrowY = _arrow.gameObject.activeSelf ? Mathf.Lerp(_arrowY, target, 1f - Mathf.Exp(-25f * dt)) : target;
        _arrow.gameObject.SetActive(true);
        if (selected != _bouncedFor)
        {
            _bouncedFor = selected;
            _bounceStart = Time.unscaledTime;
        }

        // Like the title screen: one bounce when the selection moves, unless it's set to bounce always.
        var bounceTime = Time.unscaledTime - _bounceStart;
        var bouncing = _style.ArrowBounceAlways || bounceTime < BounceSeconds;
        var bounce = bouncing ? _style.ArrowBounce * (0.5f - 0.5f * Mathf.Cos(bounceTime * (2f * Mathf.PI / BounceSeconds))) : 0f;
        _arrow.anchoredPosition = new Vector2(Left + bounce, -_arrowY);

        // Fade the arrow out with its row when the row scrolls into the list's faded edge.
        var outside = Mathf.Max(ListTop - _arrowY, _arrowY - (ReferenceHeight - ListBottomMargin), 0f);
        _arrowImage.color = WithAlpha(_style.ArrowColor, _style.ArrowColor.a * Mathf.Clamp01(1f - outside / Fade));
    }

    /// <summary>A row's vertical centre on screen, in reference units from the top.</summary>
    private static float RowCenter(Row row) => ListTop - Fade + row.Top + row.Height / 2f - _scroll;

    /// <summary>Scroll that keeps the selected row near the middle, without going past either end.</summary>
    private static float TargetScroll(int selected)
    {
        var max = Mathf.Max(0f, _contentHeight - ViewportHeight);
        if (selected < 0 || max == 0f)
        {
            return Mathf.Clamp(_scroll, 0f, max);
        }

        var row = Rows[selected];
        return Mathf.Clamp(row.Top + row.Height / 2f - ViewportHeight / 2f, 0f, max);
    }

    private static void Rebuild(LevelSelect menu, EditorInput input)
    {
        _style = MenuStyle.Current;
        _shownMenu = menu;
        _shownPage = menu.Page;
        _pageTime = 0f;
        _bouncedFor = -1;
        _lastPointer = input.PointerPosition;

        var size = _style.FontSize;
        SetText(_breadcrumb, menu.Breadcrumb, size * 0.5f, WithAlpha(_style.NotSelected, 0.6f));
        SetText(_title, menu.Title, size * 1.6f, _style.Selected);
        _shownDescription = null;
        SetText(_description, null, size * 0.6f, WithAlpha(_style.NotSelected, 0.8f));
        SetText(_hints, _hints.text, size * 0.5f, WithAlpha(_style.NotSelected, 0.7f));
        Style(_field, size);
        _field.fontSizeMax = size;
        _field.fontSizeMin = size * 0.45f;
        Style(_fieldNote, size * 0.5f);
        foreach (var view in KeyViews)
        {
            Style(view.Label, view.Key.Kind == TextEntry.KeyKind.Char ? size * 0.6f : size * 0.42f);
        }

        // Checked with Unity's null too: a destroyed sprite would draw as a white square.
        var useGameArrow = _style.Arrow != null;
        _arrowImage.sprite = useGameArrow ? _style.Arrow : _fallbackArrow ??= ArrowSprite();
        _arrow.sizeDelta = useGameArrow ? _style.ArrowSize : new Vector2(30f, 30f);

        var entries = menu.Entries;
        var y = Fade;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var row = RowFor(i);
            row.Header = entry.IsHeader;
            row.Enabled = entry.Enabled;
            row.Selection = 0f;
            if (row.Header && i > 0)
            {
                y += HeaderGap;
            }

            row.Top = y;
            row.Height = row.Header ? HeaderHeight : size * RowHeightPerFontSize;
            y += row.Height;
            Place(row.Rect, 0f, row.Top, ListWidth, row.Height);

            var label = row.Label;
            label.rectTransform.anchoredPosition = Vector2.zero;
            if (row.Header)
            {
                SetText(label, entry.Label, size * 0.5f, WithAlpha(_style.NotSelected, 0.55f));
                label.fontStyle = FontStyles.UpperCase;
                label.characterSpacing = 12f;
                label.alignment = TextAlignmentOptions.BottomLeft;
                row.Note.gameObject.SetActive(false);
                PlaceSwatches(row, null, 0f, size);
                continue;
            }

            SetText(label, entry.Label, size, _style.NotSelected);
            label.fontStyle = FontStyles.Normal;
            label.characterSpacing = 0f;
            label.alignment = TextAlignmentOptions.MidlineLeft;

            var note = row.Note;
            SetText(note, entry.Note, size * 0.5f, _style.NotSelected);
            var afterLabel = entry.Note != null || entry.Swatches != null
                ? Mathf.Min(label.GetPreferredValues(entry.Label).x, label.rectTransform.rect.width) + NoteGap
                : 0f;
            note.rectTransform.anchoredPosition = new Vector2(PlaceSwatches(row, entry.Swatches, afterLabel, size), 0f);
        }

        _contentHeight = y + Fade;
        _rowCount = entries.Count;
        for (var i = 0; i < Rows.Count; i++)
        {
            Rows[i].Rect.gameObject.SetActive(i < _rowCount);
        }

        _content.sizeDelta = new Vector2(ListWidth, _contentHeight);
        var selected = menu.Selected >= 0 && menu.Selected < _rowCount ? menu.Selected : -1;
        _scroll = TargetScroll(selected);
        _arrow.gameObject.SetActive(false);
    }

    private static Row RowFor(int index)
    {
        while (Rows.Count <= index)
        {
            var rect = NewRect($"Row{Rows.Count}", _content);
            var label = NewText("Label", rect);
            var labelRect = label.rectTransform;
            labelRect.anchorMin = new Vector2(0f, 0f);
            labelRect.anchorMax = new Vector2(0f, 1f);
            labelRect.pivot = new Vector2(0f, 0.5f);
            labelRect.sizeDelta = new Vector2(ListWidth - 260f, 0f);

            // A child of the label, so it slides with it.
            var note = NewText("Note", labelRect);
            note.alignment = TextAlignmentOptions.MidlineLeft;
            var noteRect = note.rectTransform;
            noteRect.anchorMin = new Vector2(0f, 0f);
            noteRect.anchorMax = new Vector2(0f, 1f);
            noteRect.pivot = new Vector2(0f, 0.5f);
            noteRect.sizeDelta = new Vector2(600f, 0f);

            // Also children of the label: plain squares tinted with the entry's colours.
            var swatches = new Image[MaxSwatches];
            for (var i = 0; i < swatches.Length; i++)
            {
                var swatchRect = NewRect($"Swatch{i}", labelRect);
                swatchRect.anchorMin = swatchRect.anchorMax = new Vector2(0f, 0.5f);
                swatchRect.pivot = new Vector2(0f, 0.5f);
                swatches[i] = swatchRect.gameObject.AddComponent<Image>();
                swatches[i].raycastTarget = false;
                swatchRect.gameObject.SetActive(false);
            }

            Rows.Add(new Row { Rect = rect, Label = label, Note = note, Swatches = swatches });
        }

        return Rows[index];
    }

    /// <summary>Show a row's colour swatches in a line starting at <paramref name="x"/>; returns where the line ends.</summary>
    private static float PlaceSwatches(Row row, IReadOnlyList<Color32> colors, float x, float fontSize)
    {
        for (var i = 0; i < row.Swatches.Length; i++)
        {
            var swatch = row.Swatches[i];
            var shown = colors != null && i < colors.Count;
            swatch.gameObject.SetActive(shown);
            if (!shown)
            {
                continue;
            }

            swatch.color = colors[i];
            swatch.rectTransform.sizeDelta = Vector2.one * (fontSize * SwatchSize);
            swatch.rectTransform.anchoredPosition = new Vector2(x, 0f);
            x += fontSize * (SwatchSize + SwatchGap);
        }

        return x;
    }

    private static void Build()
    {
        Rows.Clear();
        _rowCount = 0;
        _shownMenu = null;
        _shownHints = null;
        _style = MenuStyle.Current;

        _root = new GameObject("CairnRouteMaker.LevelSelectView");
        Object.DontDestroyOnLoad(_root);
        _root.layer = 5; // UI
        _canvas = _root.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 32000;
        _group = _root.AddComponent<CanvasGroup>();
        _group.interactable = false;
        _group.blocksRaycasts = false;

        var shade = NewRect("Shade", _root.transform);
        shade.anchorMin = new Vector2(0f, 0f);
        shade.anchorMax = new Vector2(0f, 1f);
        shade.pivot = new Vector2(0f, 0.5f);
        shade.sizeDelta = new Vector2(ListWidth + 150f, 0f);
        var shadeImage = shade.gameObject.AddComponent<Image>();
        shadeImage.sprite = _gradient ??= GradientSprite();
        shadeImage.color = Color.black;
        shadeImage.raycastTarget = false;

        _breadcrumb = NewText("Breadcrumb", _root.transform);
        Place(_breadcrumb.rectTransform, Left, 150f, ListWidth, 34f);
        _breadcrumb.fontStyle = FontStyles.UpperCase;
        _breadcrumb.characterSpacing = 12f;
        _breadcrumb.alignment = TextAlignmentOptions.BottomLeft;

        _title = NewText("Title", _root.transform);
        Place(_title.rectTransform, Left, 184f, ListWidth, 84f);
        _title.alignment = TextAlignmentOptions.MidlineLeft;

        _description = NewText("Description", _root.transform);
        // Two lines, for a route's description; the list starts below them.
        Place(_description.rectTransform, Left, 268f, ListWidth, 76f);
        _description.alignment = TextAlignmentOptions.TopLeft;
        _description.textWrappingMode = TextWrappingModes.Normal;

        _hints = NewText("Hints", _root.transform);
        Place(_hints.rectTransform, Left, ReferenceHeight - 80f, ListWidth, 40f);
        _hints.alignment = TextAlignmentOptions.MidlineLeft;

        BuildTextEntry();

        _viewport = NewRect("List", _root.transform);
        Place(_viewport, Left, ListTop - Fade, ListWidth + 200f, ViewportHeight);
        var mask = _viewport.gameObject.AddComponent<RectMask2D>();
        mask.softness = new Vector2Int(0, (int)Fade);

        _content = NewRect("Content", _viewport);
        Place(_content, 0f, 0f, ListWidth, 0f);

        _arrow = NewRect("Arrow", _root.transform);
        _arrow.anchorMin = _arrow.anchorMax = new Vector2(0f, 1f);
        _arrow.pivot = new Vector2(0f, 0.5f);
        _arrow.sizeDelta = new Vector2(30f, 30f);
        _arrowImage = _arrow.gameObject.AddComponent<Image>();
        _arrowImage.sprite = _fallbackArrow ??= ArrowSprite();
        _arrowImage.preserveAspect = true;
        _arrowImage.raycastTarget = false;
        _arrow.gameObject.SetActive(false);

        _root.SetActive(false);
    }

    private static void BuildTextEntry()
    {
        var fieldHeight = _style.FontSize * FieldHeightPerFontSize;
        _field = NewText("Field", _root.transform);
        Place(_field.rectTransform, Left, ListTop, ListWidth, fieldHeight);
        _field.alignment = TextAlignmentOptions.MidlineLeft;
        // A long description shrinks to fit rather than running off the end.
        _field.enableAutoSizing = true;

        var line = NewRect("FieldLine", _root.transform);
        Place(line, Left, ListTop + fieldHeight, ListWidth * 0.75f, 2f);
        _fieldLine = line.gameObject.AddComponent<Image>();
        _fieldLine.raycastTarget = false;

        _fieldNote = NewText("FieldNote", _root.transform);
        Place(_fieldNote.rectTransform, Left, ListTop + fieldHeight + 10f, ListWidth, 34f);
        _fieldNote.alignment = TextAlignmentOptions.TopLeft;

        _keyboard = NewRect("Keyboard", _root.transform);
        Place(_keyboard, Left, KeyboardTop, TextEntry.Columns * (KeyWidth + KeyGap), TextEntry.Layout.Count * (KeyHeight + KeyGap));
        KeyViews.Clear();
        for (var row = 0; row < TextEntry.Layout.Count; row++)
        {
            foreach (var key in TextEntry.Layout[row])
            {
                // A plain Image with no sprite draws a filled rectangle.
                var rect = NewRect($"Key{row}.{key.Column}", _keyboard);
                Place(rect, key.Column * (KeyWidth + KeyGap), row * (KeyHeight + KeyGap), key.Span * KeyWidth + (key.Span - 1) * KeyGap, KeyHeight);
                var background = rect.gameObject.AddComponent<Image>();
                background.raycastTarget = false;
                var label = NewText("Label", rect);
                Place(label.rectTransform, 0f, 0f, rect.sizeDelta.x, KeyHeight);
                label.alignment = TextAlignmentOptions.Center;
                KeyViews.Add(new KeyView { Key = key, Background = background, Label = label });
            }
        }

        _field.gameObject.SetActive(false);
        _fieldLine.gameObject.SetActive(false);
        _fieldNote.gameObject.SetActive(false);
        _keyboard.gameObject.SetActive(false);
    }

    private static string Hint(string key, string action) => $"<color=#FFFFFF>{key}</color>  {action}<space=2em>";

    private static void SetText(TextMeshProUGUI text, string value, float size, Color color)
    {
        Style(text, size);
        text.color = color;
        text.text = value ?? "";
        text.gameObject.SetActive(!string.IsNullOrEmpty(value));
    }

    /// <summary>The menu's font, in a size.</summary>
    private static void Style(TextMeshProUGUI text, float size)
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
    }

    private static Color WithAlpha(Color color, float alpha) => new(color.r, color.g, color.b, alpha);

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
        text.overflowMode = TextOverflowModes.Ellipsis;
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

    /// <summary>White on the left fading to clear on the right, tinted by the image colour.</summary>
    private static Sprite GradientSprite()
    {
        const int width = 256;
        var texture = RuntimeAssets.Keep(new Texture2D(width, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear });
        for (var x = 0; x < width; x++)
        {
            var fade = 1f - Mathf.SmoothStep(0f, 1f, x / (width - 1f));
            texture.SetPixel(x, 0, new Color(1f, 1f, 1f, 0.6f * fade * fade));
        }

        texture.Apply();
        return RuntimeAssets.Keep(Sprite.Create(texture, new Rect(0f, 0f, width, 1f), new Vector2(0.5f, 0.5f)));
    }

    /// <summary>A right-pointing triangle, for when the title screen's arrow hasn't been seen.</summary>
    private static Sprite ArrowSprite()
    {
        const int size = 64;
        var texture = RuntimeAssets.Keep(new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear });
        var a = new Vector2(10f, 6f);
        var b = new Vector2(10f, 58f);
        var c = new Vector2(58f, 32f);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                var inside = Mathf.Min(EdgeDistance(a, b, p), Mathf.Min(EdgeDistance(b, c, p), EdgeDistance(c, a, p)));
                texture.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(inside + 0.5f)));
            }
        }

        texture.Apply();
        return RuntimeAssets.Keep(Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f)));
    }

    /// <summary>Signed distance from the line a→b, positive to its right (inside, for a clockwise triangle).</summary>
    private static float EdgeDistance(Vector2 a, Vector2 b, Vector2 p)
    {
        var edge = (b - a).normalized;
        return edge.y * (p.x - a.x) - edge.x * (p.y - a.y);
    }
}
