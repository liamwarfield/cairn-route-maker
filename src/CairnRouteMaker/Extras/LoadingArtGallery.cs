using System;
using System.Collections.Generic;
using System.Linq;
using CairnRouteMaker.Controls;
using CairnRouteMaker.Game;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTheGameBakers.Cairn.UI;
using CairnRouteMaker.Routes;
using Il2CppTMPro;
using MelonLoader;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace CairnRouteMaker.Extras;

/// <summary>
/// The Extras page's Loading Screen Art viewer, one picture at a time and full screen, in three sets:
/// Story (the paintings the game shows while it loads), On the Trail (each crag's own loading screen)
/// and Tenzen to Kami (the pictures of that trip). Left / right flips through the pictures, up / down
/// (LB / RB on a gamepad) jumps to the next set, and the sets' names sit in a row along the bottom so
/// the later ones aren't missed. The game keeps them as Addressables references on its LoadingScreen
/// (<c>possibleSpritesReferences</c>, <c>worldLoadingScreens</c> and <c>tenzenToKamiSprites</c>); the
/// viewer loads the picture on show and its neighbours, and releases them all when it closes. The sets,
/// counter and hints fade out when left alone, leaving just the picture. While it's open the title
/// screen's input is blocked and devices are read directly.
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

    private const string StoryTitle = "Story";
    private const string OnTheTrailTitle = "On the Trail";
    private const string TenzenToKamiTitle = "Tenzen to Kami";

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
    /// <summary>The colour of the sets not on show, as a rich text colour.</summary>
    private string _otherSetColor = "FFFFFF80";

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
        MelonLogger.Msg($"Opening the loading screen art: {Count(StoryTitle)} story loading screens, {Count(OnTheTrailTitle)} On the Trail screens, {Count(TenzenToKamiTitle)} Tenzen to Kami pictures.");
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
        LoadAround(_index);
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
        _above.sprite = _below.sprite = null;
        // Let the game unload what we loaded.
        foreach (var picture in _art)
        {
            picture.Release();
        }

        _art.Clear();
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

        ShowLoaded();
        Animate();
    }

    private void HandleInput()
    {
        var step = 0;
        var jump = 0;
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
            if (keyboard.upArrowKey.wasPressedThisFrame || keyboard.wKey.wasPressedThisFrame || keyboard.pageUpKey.wasPressedThisFrame) jump -= 1;
            if (keyboard.downArrowKey.wasPressedThisFrame || keyboard.sKey.wasPressedThisFrame
                || keyboard.pageDownKey.wasPressedThisFrame || keyboard.tabKey.wasPressedThisFrame) jump += 1;
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

            var pressed = pad.dpad.left.wasPressedThisFrame;
            var pressedNext = pad.dpad.right.wasPressedThisFrame || pad.buttonSouth.wasPressedThisFrame;
            var pressedSet = pad.dpad.up.wasPressedThisFrame || pad.leftShoulder.wasPressedThisFrame;
            var pressedNextSet = pad.dpad.down.wasPressedThisFrame || pad.rightShoulder.wasPressedThisFrame;
            var pressedBack = pad.buttonEast.wasPressedThisFrame;
            if (pressed || pressedNext || pressedSet || pressedNextSet || pressedBack || stickStep != 0
                || pad.buttonNorth.wasPressedThisFrame || pad.buttonWest.wasPressedThisFrame)
            {
                any = true;
                _usingGamepad = true;
            }

            step += stickStep + (pressed ? -1 : 0) + (pressedNext ? 1 : 0);
            jump += (pressedSet ? -1 : 0) + (pressedNextSet ? 1 : 0);
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

        if (jump != 0)
        {
            JumpSet(jump > 0 ? 1 : -1);
        }
        else if (step != 0 && _art.Count > 1)
        {
            GoTo((_index + (step > 0 ? 1 : -1) + _art.Count) % _art.Count);
        }
    }

    /// <summary>Go to the first picture of the next or previous set, wrapping round.</summary>
    private void JumpSet(int direction)
    {
        var sets = Sets();
        if (sets.Count < 2)
        {
            return;
        }

        var set = sets[(sets.IndexOf(_art[_index].Section) + direction + sets.Count) % sets.Count];
        GoTo(_art.FindIndex(picture => picture.Section == set));
    }

    /// <summary>Show the picture at <paramref name="index"/>, cross-fading to it.</summary>
    private void GoTo(int index)
    {
        if (index == _index)
        {
            return;
        }

        _index = index;
        LoadAround(_index);
        _below.sprite = _above.sprite;
        _above.sprite = _art[_index].Sprite;
        _fade = 0f;
        ShowPicture();
    }

    /// <summary>Start loading the picture at <paramref name="index"/> and the ones either side, for flipping.</summary>
    private void LoadAround(int index)
    {
        for (var offset = -1; offset <= 1 && _art.Count > 0; offset++)
        {
            _art[(index + offset + _art.Count) % _art.Count].Load();
        }
    }

    /// <summary>The picture on show has finished loading since it was turned to: put it up.</summary>
    private void ShowLoaded()
    {
        if (_art.Count == 0)
        {
            return;
        }

        var sprite = _art[_index].Sprite;
        if (sprite != null && (_above.sprite == null || _above.sprite.Pointer != sprite.Pointer))
        {
            _above.sprite = sprite;
            ShowPicture();
        }
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
            var picture = _art[_index];
            var section = picture.Section;
            var first = _art.FindIndex(other => other.Section == section);
            var count = $"{_index - first + 1} / {Count(section)}";
            _counter.text = (picture.Caption == null ? count : $"<color=#FFFFFF>{picture.Caption}</color><space=1em>{count}")
                + (picture.Failed ? "  (couldn't load)" : picture.Sprite == null ? "  (loading)" : "");
            _title.text = string.Join("<space=1.5em>", Sets().Select(set => set == section ? set : $"<size=70%><color=#{_otherSetColor}>{set}</color></size>"));
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
                ? Hint("D-pad", "Browse") + Hint("LB / RB", "Sets") + Hint("B", "Back")
                : Hint("Left / Right", "Browse") + Hint("Up / Down", "Sets") + Hint("Esc", "Back");
        }
    }

    /// <summary>
    /// Every distinct picture on the game's loading screens, in the game's order: the story's loading
    /// art, each crag's own screens, then the Tenzen to Kami pictures, in the player's language.
    /// Nothing is loaded yet.
    /// </summary>
    private void FindArt()
    {
        _art.Clear();
        var screens = Resources.FindObjectsOfTypeAll<LoadingScreen>();
        foreach (var screen in screens)
        {
            foreach (var reference in screen.possibleSpritesReferences ?? new Il2CppReferenceArray<AssetReference>(0))
            {
                Add(reference, StoryTitle);
            }
        }

        foreach (var screen in screens)
        {
            var worlds = (screen.worldLoadingScreens ?? new Il2CppReferenceArray<LoadingScreen.WorldSpecificLoadingScreen>(0))
                .Where(world => world != null)
                .ToList();
            foreach (var world in worlds)
            {
                // Named after the crag, and the climber when the crag has a screen for each (Ruka does).
                var caption = ZoneNames.PrettyWorld(world.world.ToString());
                if (world.character != PawnManager.Character.Invalid && worlds.Count(other => other.world == world.world) > 1)
                {
                    caption += $" ({world.character})";
                }

                Add(InLanguage(world.sprite), OnTheTrailTitle, caption);
            }
        }

        foreach (var screen in screens)
        {
            foreach (var localized in screen.tenzenToKamiSprites ?? new Il2CppReferenceArray<LocalizedSprite>(0))
            {
                Add(InLanguage(localized), TenzenToKamiTitle);
            }
        }
    }

    /// <summary>The player's language's version of a picture, or the default one.</summary>
    private static AssetReference InLanguage(LocalizedSprite localized)
    {
        if (localized == null)
        {
            return null;
        }

        try
        {
            return localized.Get() ?? localized.defaultSpriteReference;
        }
        catch (Exception e)
        {
            MelonLogger.Warning($"Couldn't get {localized.name} in the player's language: {e.Message}");
            return localized.defaultSpriteReference;
        }
    }

    private void Add(AssetReference reference, string section, string caption = null)
    {
        var key = reference == null ? null : reference.RuntimeKey?.ToString();
        if (!string.IsNullOrEmpty(key) && !_art.Exists(known => known.Key == key))
        {
            _art.Add(new Picture(reference, key, section, caption));
        }
    }

    private int Count(string section) => _art.FindAll(picture => picture.Section == section).Count;

    /// <summary>The sets that have pictures, in order.</summary>
    private List<string> Sets() => _art.Select(picture => picture.Section).Distinct().ToList();

    /// <summary>One picture: where the game keeps it, and our own load of it once asked for.</summary>
    private sealed class Picture
    {
        private readonly AssetReference _reference;
        private AsyncOperationHandle<Sprite> _handle;
        private bool _loading;
        private Sprite _sprite;

        public Picture(AssetReference reference, string key, string section, string caption)
        {
            _reference = reference;
            Key = key;
            Section = section;
            Caption = caption;
        }

        public string Key { get; }
        public string Section { get; }
        /// <summary>What the picture is of, when its set doesn't say: the crag, for On the Trail.</summary>
        public string Caption { get; }
        public bool Failed { get; private set; }

        /// <summary>The picture, or null while it's loading (or if it failed).</summary>
        public Sprite Sprite
        {
            get
            {
                if (_loading && _handle.IsDone)
                {
                    _loading = false;
                    if (_handle.Status == AsyncOperationStatus.Succeeded)
                    {
                        _sprite = _handle.Result;
                    }
                    else
                    {
                        Failed = true;
                        MelonLogger.Warning($"Couldn't load the loading screen picture {Key}: {_handle.OperationException?.Message ?? _handle.Status.ToString()}");
                    }
                }

                return _sprite;
            }
        }

        /// <summary>
        /// Our own load by key, not the reference's LoadAssetAsync: that keeps one handle on the
        /// reference, which the game's loading screen uses too.
        /// </summary>
        public void Load()
        {
            if (_loading || _sprite != null || Failed)
            {
                return;
            }

            try
            {
                _handle = Addressables.LoadAssetAsync<Sprite>(_reference.RuntimeKey);
                _loading = true;
            }
            catch (Exception e)
            {
                Failed = true;
                MelonLogger.Warning($"Couldn't start loading the loading screen picture {Key}: {e.Message}");
            }
        }

        public void Release()
        {
            if (_handle != null && (_loading || _sprite != null))
            {
                try
                {
                    Addressables.Release<Sprite>(_handle);
                }
                catch (Exception e)
                {
                    MelonLogger.Warning($"Couldn't release the loading screen picture {Key}: {e.Message}");
                }
            }

            _loading = false;
            _sprite = null;
        }
    }

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
        Color32 other = WithAlpha(style.NotSelected, 0.6f);
        _otherSetColor = $"{other.r:X2}{other.g:X2}{other.b:X2}{other.a:X2}";
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
