using System;
using System.Collections.Generic;
using CairnRouteMaker.Controls;
using CairnRouteMaker.Game;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTheGameBakers.Cairn.UI;
using Il2CppTMPro;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace CairnRouteMaker.Extras;

/// <summary>
/// The Extras page's Loading Screen Art viewer: the paintings the game shows while it loads, then the
/// pictures of the trip from Tenzing to Kami, one at a time and full screen, flipped through with
/// left / right. The pictures are the game's own sprites, read from its LoadingScreen
/// (<c>possibleSprites</c> and <c>tenzenToKamiSprites</c>) when the viewer opens, so nothing is copied.
/// The page counter and hints fade out when left alone, leaving just the picture. While it's open the
/// title screen's input is blocked and devices are read directly.
/// </summary>
internal sealed class LoadingArtGallery
{
    private const float ReferenceHeight = 1080f;
    private const float OpenFadeSeconds = 0.25f;
    private const float CrossFadeSeconds = 0.35f;
    /// <summary>The counter and hints fade out after this long without input.</summary>
    private const float ChromeIdleSeconds = 3f;
    private const float ChromeFadeSeconds = 0.6f;
    private const float StickThreshold = 0.6f;

    private const string LoadingArtTitle = "Loading Screen Art";
    private const string TenzingToKamiTitle = "Tenzing to Kami";

    private readonly List<Picture> _art = new();
    /// <summary>The title screen's menu the viewer opened over; null when it wasn't found.</summary>
    private MainMenu _menu;
    private bool _watchMenu;
    private bool _open;
    private int _openedFrame;
    private int _unblockAtFrame = -1;
    /// <summary>Kept between visits, so the viewer reopens where it was left.</summary>
    private int _index;
    private float _openTime;
    private float _fade = 1f;
    private float _lastInput;
    private bool _usingGamepad;
    private bool _stickHeld;
    private Vector2 _lastPointer;

    private GameObject _root;
    private Canvas _canvas;
    private CanvasGroup _group;
    private Image _below;
    private Image _above;
    private CanvasGroup _chrome;
    private TextMeshProUGUI _title;
    private TextMeshProUGUI _counter;
    private TextMeshProUGUI _hints;
    private RectTransform _leftArrow;
    private RectTransform _rightArrow;
    private bool? _shownGamepad;

    public LoadingArtGallery()
    {
        MainMenuPatches.SuppressCancelWhile(() => _open || _unblockAtFrame >= 0);
    }

    public void Open()
    {
        if (_open)
        {
            return;
        }

        _menu = Object.FindFirstObjectByType<MainMenu>();
        _watchMenu = _menu != null;
        FindArt();
        MelonLogger.Msg($"Opening the loading screen art: {Count(LoadingArtTitle)} loading screens, {Count(TenzingToKamiTitle)} Tenzing to Kami pictures.");
        if (_root == null)
        {
            Build();
        }

        _open = true;
        _openedFrame = Time.frameCount;
        _openTime = Time.unscaledTime;
        _lastInput = Time.unscaledTime;
        _unblockAtFrame = -1;
        _shownGamepad = null;
        _lastPointer = Mouse.current?.position.ReadValue() ?? Vector2.zero;
        _index = _art.Count == 0 ? 0 : Mathf.Clamp(_index, 0, _art.Count - 1);
        _above.sprite = _art.Count > 0 ? _art[_index].Sprite : null;
        _below.sprite = null;
        _fade = 1f;
        ApplyStyle();
        ShowPicture();
        _root.SetActive(true);
        GameApi.BlockMenuInput(true);
    }

    public void Close()
    {
        if (!_open)
        {
            return;
        }

        _open = false;
        _menu = null;
        _watchMenu = false;
        _root.SetActive(false);
        // Our sprites belong to the game; don't hold on to them.
        _art.Clear();
        _above.sprite = _below.sprite = null;
        // Next frame: the key that closed the viewer mustn't also reach the Extras page (B there goes back).
        _unblockAtFrame = Time.frameCount + 1;
    }

    public void Update()
    {
        if (_unblockAtFrame >= 0 && Time.frameCount >= _unblockAtFrame)
        {
            _unblockAtFrame = -1;
            GameApi.BlockMenuInput(false);
        }

        if (!_open)
        {
            return;
        }

        // The main menu is gone (a game is loading): our canvas outlives scenes, so close it by hand.
        if (_watchMenu && _menu == null)
        {
            Close();
            return;
        }

        _canvas.scaleFactor = Screen.height / ReferenceHeight;
        // Skip the frame the viewer opened on, or the press that opened it would turn the page.
        if (Time.frameCount > _openedFrame)
        {
            HandleInput();
        }

        if (!_open)
        {
            return;
        }

        Animate();
    }

    private void HandleInput()
    {
        var step = 0;
        var back = ModControls.Exit.KeyPressed || ModControls.Exit.PadPressed;
        var keyboard = Keyboard.current;
        var mouse = Mouse.current;
        var pad = Gamepad.current;
        var any = false;

        if (keyboard != null && keyboard.anyKey.wasPressedThisFrame)
        {
            any = true;
            _usingGamepad = false;
            if (keyboard.leftArrowKey.wasPressedThisFrame || keyboard.aKey.wasPressedThisFrame) step -= 1;
            if (keyboard.rightArrowKey.wasPressedThisFrame || keyboard.dKey.wasPressedThisFrame
                || keyboard.spaceKey.wasPressedThisFrame || keyboard.enterKey.wasPressedThisFrame) step += 1;
            back |= keyboard.escapeKey.wasPressedThisFrame || keyboard.backspaceKey.wasPressedThisFrame;
        }

        if (mouse != null)
        {
            var pointer = mouse.position.ReadValue();
            if ((pointer - _lastPointer).sqrMagnitude > 4f)
            {
                any = true;
                _usingGamepad = false;
            }

            _lastPointer = pointer;
            if (mouse.leftButton.wasPressedThisFrame)
            {
                any = true;
                _usingGamepad = false;
                step += pointer.x < Screen.width / 3f ? -1 : 1;
            }

            if (mouse.rightButton.wasPressedThisFrame)
            {
                any = true;
                back = true;
            }

            var scroll = mouse.scroll.ReadValue().y;
            if (scroll != 0f)
            {
                any = true;
                step += scroll > 0f ? -1 : 1;
            }
        }

        if (pad != null)
        {
            // The stick turns one page per push, not one per frame.
            var stick = pad.leftStick.ReadValue().x;
            var stickStep = 0;
            if (Mathf.Abs(stick) < StickThreshold * 0.5f)
            {
                _stickHeld = false;
            }
            else if (!_stickHeld && Mathf.Abs(stick) > StickThreshold)
            {
                _stickHeld = true;
                stickStep = stick > 0f ? 1 : -1;
            }

            var pressed = pad.dpad.left.wasPressedThisFrame || pad.leftShoulder.wasPressedThisFrame;
            var pressedNext = pad.dpad.right.wasPressedThisFrame || pad.rightShoulder.wasPressedThisFrame || pad.buttonSouth.wasPressedThisFrame;
            var pressedBack = pad.buttonEast.wasPressedThisFrame;
            if (pressed || pressedNext || pressedBack || stickStep != 0 || pad.buttonNorth.wasPressedThisFrame || pad.buttonWest.wasPressedThisFrame)
            {
                any = true;
                _usingGamepad = true;
            }

            step += stickStep + (pressed ? -1 : 0) + (pressedNext ? 1 : 0);
            back |= pressedBack;
        }

        if (back)
        {
            Close();
            return;
        }

        if (any)
        {
            _lastInput = Time.unscaledTime;
        }

        if (step != 0 && _art.Count > 1)
        {
            Turn(step > 0 ? 1 : -1);
        }
    }

    /// <summary>Go to the next or previous picture, wrapping round, and cross-fade to it.</summary>
    private void Turn(int direction)
    {
        _index = (_index + direction + _art.Count) % _art.Count;
        _below.sprite = _above.sprite;
        _above.sprite = _art[_index].Sprite;
        _fade = 0f;
        ShowPicture();
    }

    private void ShowPicture()
    {
        _below.gameObject.SetActive(_below.sprite != null);
        _above.gameObject.SetActive(_above.sprite != null);
        if (_art.Count == 0)
        {
            _counter.text = "";
            _title.text = "The loading screen art isn't loaded yet.";
        }
        else
        {
            // Counted within the picture's own set.
            var section = _art[_index].Section;
            var first = _art.FindIndex(picture => picture.Section == section);
            _counter.text = $"{_index - first + 1} / {Count(section)}";
            _title.text = section;
        }

        var arrows = _art.Count > 1;
        _leftArrow.gameObject.SetActive(arrows);
        _rightArrow.gameObject.SetActive(arrows);
    }

    private void Animate()
    {
        var now = Time.unscaledTime;
        _group.alpha = Mathf.Clamp01((now - _openTime) / OpenFadeSeconds);

        _fade = Mathf.MoveTowards(_fade, 1f, Time.unscaledDeltaTime / CrossFadeSeconds);
        _above.color = new Color(1f, 1f, 1f, Mathf.SmoothStep(0f, 1f, _fade));
        if (_fade >= 1f && _below.gameObject.activeSelf)
        {
            _below.gameObject.SetActive(false);
        }

        // Fade the chrome out when left alone; any input brings it straight back.
        var idle = now - _lastInput - ChromeIdleSeconds;
        _chrome.alpha = _art.Count == 0 ? 1f : 1f - Mathf.Clamp01(idle / ChromeFadeSeconds);

        if (_shownGamepad != _usingGamepad)
        {
            _shownGamepad = _usingGamepad;
            _hints.text = _usingGamepad
                ? Hint("D-pad", "Browse") + Hint("B", "Back")
                : Hint("Arrows", "Browse") + Hint("Esc", "Back");
        }
    }

    /// <summary>
    /// Every distinct picture on the game's loading screens, in the game's order: the loading art,
    /// then the Tenzing to Kami pictures in the player's language.
    /// </summary>
    private void FindArt()
    {
        _art.Clear();
        var screens = Resources.FindObjectsOfTypeAll<LoadingScreen>();
        foreach (var screen in screens)
        {
            foreach (var sprite in screen.possibleSprites ?? new Il2CppReferenceArray<Sprite>(0))
            {
                Add(sprite, LoadingArtTitle);
            }
        }

        foreach (var screen in screens)
        {
            foreach (var localized in screen.tenzenToKamiSprites ?? new Il2CppReferenceArray<LocalizedSprite>(0))
            {
                if (localized == null)
                {
                    continue;
                }

                Sprite sprite;
                try
                {
                    sprite = localized.Get() ?? localized.defaultSprite;
                }
                catch (Exception e)
                {
                    MelonLogger.Warning($"Couldn't get {localized.name} in the player's language: {e.Message}");
                    sprite = localized.defaultSprite;
                }

                Add(sprite, TenzingToKamiTitle);
            }
        }
    }

    private void Add(Sprite sprite, string section)
    {
        if (sprite != null && !_art.Exists(known => known.Sprite.Pointer == sprite.Pointer))
        {
            _art.Add(new Picture(sprite, section));
        }
    }

    private int Count(string section) => _art.FindAll(picture => picture.Section == section).Count;

    private readonly record struct Picture(Sprite Sprite, string Section);

    private void Build()
    {
        _root = new GameObject("CairnRouteMaker.LoadingArtGallery");
        Object.DontDestroyOnLoad(_root);
        _root.layer = 5; // UI
        _canvas = _root.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 32000;
        _group = _root.AddComponent<CanvasGroup>();
        _group.interactable = false;
        _group.blocksRaycasts = false;

        var backdrop = Stretch(NewRect("Backdrop", _root.transform)).gameObject.AddComponent<Image>();
        backdrop.color = Color.black;
        backdrop.raycastTarget = false;

        _below = NewPicture("Below");
        _above = NewPicture("Above");

        var chrome = Stretch(NewRect("Chrome", _root.transform));
        _chrome = chrome.gameObject.AddComponent<CanvasGroup>();
        _chrome.interactable = false;
        _chrome.blocksRaycasts = false;

        // A dark band along the bottom keeps the text readable over bright pictures.
        var band = NewRect("Band", chrome);
        band.anchorMin = new Vector2(0f, 0f);
        band.anchorMax = new Vector2(1f, 0f);
        band.pivot = new Vector2(0.5f, 0f);
        band.sizeDelta = new Vector2(0f, 220f);
        var bandImage = band.gameObject.AddComponent<Image>();
        bandImage.sprite = BandSprite();
        bandImage.color = Color.black;
        bandImage.raycastTarget = false;

        _title = NewText("Title", chrome, new Vector2(0f, 0f), new Vector2(120f, 92f), TextAlignmentOptions.BottomLeft);
        _counter = NewText("Counter", chrome, new Vector2(0f, 0f), new Vector2(120f, 52f), TextAlignmentOptions.BottomLeft);
        _hints = NewText("Hints", chrome, new Vector2(1f, 0f), new Vector2(-120f, 56f), TextAlignmentOptions.BottomRight);

        _leftArrow = NewArrow("Left", chrome, new Vector2(0f, 0.5f), new Vector2(60f, 0f), flip: true);
        _rightArrow = NewArrow("Right", chrome, new Vector2(1f, 0.5f), new Vector2(-60f, 0f), flip: false);

        _root.SetActive(false);
    }

    /// <summary>Fonts and the arrow come from the title screen's menu, which may load after we're built.</summary>
    private void ApplyStyle()
    {
        var style = MenuStyle.Current;
        Style(_title, style, style.FontSize * 0.9f, style.Selected);
        Style(_counter, style, style.FontSize * 0.55f, WithAlpha(style.NotSelected, 0.75f));
        Style(_hints, style, style.FontSize * 0.5f, WithAlpha(style.NotSelected, 0.7f));
        foreach (var arrow in new[] { _leftArrow, _rightArrow })
        {
            var image = arrow.GetComponent<Image>();
            image.sprite = style.Arrow;
            image.color = WithAlpha(style.ArrowColor, style.ArrowColor.a * 0.8f);
            arrow.sizeDelta = style.ArrowSize;
            // No game arrow to copy: hide ours rather than draw white squares.
            image.enabled = style.Arrow != null;
        }
    }

    private Image NewPicture(string name)
    {
        var image = Stretch(NewRect(name, _root.transform)).gameObject.AddComponent<Image>();
        image.preserveAspect = true;
        image.raycastTarget = false;
        image.gameObject.SetActive(false);
        return image;
    }

    private static TextMeshProUGUI NewText(string name, Transform parent, Vector2 anchor, Vector2 position, TextAlignmentOptions alignment)
    {
        var rect = NewRect(name, parent);
        rect.anchorMin = rect.anchorMax = rect.pivot = anchor;
        rect.anchoredPosition = position;
        rect.sizeDelta = new Vector2(900f, 60f);
        var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
        text.raycastTarget = false;
        text.richText = true;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.alignment = alignment;
        return text;
    }

    private static RectTransform NewArrow(string name, Transform parent, Vector2 anchor, Vector2 position, bool flip)
    {
        var rect = NewRect(name, parent);
        rect.anchorMin = rect.anchorMax = rect.pivot = anchor;
        rect.anchoredPosition = position;
        rect.localScale = new Vector3(flip ? -1f : 1f, 1f, 1f);
        var image = rect.gameObject.AddComponent<Image>();
        image.preserveAspect = true;
        image.raycastTarget = false;
        return rect;
    }

    private static void Style(TextMeshProUGUI text, MenuStyle style, float size, Color color)
    {
        if (style.Font != null)
        {
            text.font = style.Font;
        }

        if (style.Material != null)
        {
            text.fontSharedMaterial = style.Material;
        }

        text.fontSize = size;
        text.color = color;
    }

    private static string Hint(string key, string action) => $"<color=#FFFFFF>{key}</color>  {action}<space=2em>";

    private static Color WithAlpha(Color color, float alpha) => new(color.r, color.g, color.b, alpha);

    private static RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name) { layer = 5 };
        var rect = go.AddComponent<RectTransform>();
        rect.SetParent(parent, false);
        return rect;
    }

    private static RectTransform Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.sizeDelta = Vector2.zero;
        return rect;
    }

    /// <summary>Clear at the top fading to half-dark at the bottom, tinted by the image colour.</summary>
    private static Sprite BandSprite()
    {
        const int height = 128;
        var texture = RuntimeAssets.Keep(new Texture2D(1, height, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear });
        for (var y = 0; y < height; y++)
        {
            var dark = 1f - Mathf.SmoothStep(0f, 1f, y / (height - 1f));
            texture.SetPixel(0, y, new Color(1f, 1f, 1f, 0.6f * dark));
        }

        texture.Apply();
        return RuntimeAssets.Keep(Sprite.Create(texture, new Rect(0f, 0f, 1f, height), new Vector2(0.5f, 0.5f)));
    }
}
