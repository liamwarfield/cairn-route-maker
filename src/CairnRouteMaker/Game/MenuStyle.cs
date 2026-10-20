using System;
using Il2CppTheGameBakers.Cairn.UI;
using Il2CppTMPro;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CairnRouteMaker.Game;

/// <summary>
/// The title screen's look, read from its menu so the mod's menu can copy it: the buttons' font,
/// material (which carries the drop shadow) and size, the selected / not-selected colours, how far a
/// selected button slides right, and the bouncing arrow's sprite. Read on the title screen and kept
/// for the rest of the session, so the in-game level select matches too. Anything that can't be
/// read falls back to a close guess.
/// </summary>
internal sealed class MenuStyle
{
    private static MenuStyle _captured;
    private static MenuStyle _fallback;

    public TMP_FontAsset Font { get; private set; }
    public Material Material { get; private set; }
    public float FontSize { get; private set; } = 40f;
    public Color Selected { get; private set; } = Color.white;
    public Color NotSelected { get; private set; } = new(0.86f, 0.86f, 0.86f, 1f);
    public float SelectedOffsetX { get; private set; } = 75f;
    public float TransitionSeconds { get; private set; } = 0.15f;
    public Sprite Arrow { get; private set; }
    public Vector2 ArrowSize { get; private set; } = new(30f, 30f);
    public Color ArrowColor { get; private set; } = Color.white;
    public float ArrowBounce { get; private set; } = 8f;

    /// <summary>False (the title screen's setting): the arrow bounces once each time the selection moves.</summary>
    public bool ArrowBounceAlways { get; private set; }

    /// <summary>True when this came from the title screen, not the fallback.</summary>
    public bool FromGame { get; private set; }

    /// <summary>
    /// The title screen's style if it has been seen, otherwise the best available guess. Until it has
    /// been seen this searches the scene, so read it once per page, not every frame.
    /// </summary>
    public static MenuStyle Current
    {
        get
        {
            if (_captured == null || _captured.Font == null)
            {
                _captured = TryCapture();
            }

            return _captured ?? (_fallback ??= Fallback());
        }
    }

    private static MenuStyle TryCapture()
    {
        try
        {
            var element = Object.FindFirstObjectByType<MainMenuModeSelectElement>(FindObjectsInactive.Include);
            var text = element?.storyModeButton?.GetComponentInChildren<TextMeshProUGUI>(true);
            if (text == null || text.font == null)
            {
                return null;
            }

            var style = new MenuStyle
            {
                Font = Keep(text.font),
                Material = Keep(text.fontSharedMaterial),
                FontSize = text.fontSize > 0f ? text.fontSize : 40f,
                FromGame = true,
            };

            if (element.applySelectionColor)
            {
                style.Selected = element.colorWhenSelected;
                style.NotSelected = element.colorWhenNotSelected;
            }

            if (element.applySelectionOffset && element.offsetXWhenSelected > 0f)
            {
                style.SelectedOffsetX = element.offsetXWhenSelected;
            }

            if (element.selectTransitionDuration > 0f)
            {
                style.TransitionSeconds = element.selectTransitionDuration;
            }

            var arrow = element.mainMenu?.bouncingArrow;
            if (arrow?.image?.sprite != null)
            {
                style.Arrow = CopySprite(arrow.image.sprite);
                var size = arrow.rect != null ? arrow.rect.rect.size : Vector2.zero;
                if (size.x > 1f && size.y > 1f)
                {
                    style.ArrowSize = size;
                }

                style.ArrowColor = arrow.activeColor.a > 0f ? arrow.activeColor : Color.white;
                style.ArrowBounceAlways = arrow.bounceAlways;
                if (arrow.bouncingArrowXAmplitude > 0f)
                {
                    style.ArrowBounce = Mathf.Min(arrow.bouncingArrowXAmplitude, 40f);
                }
            }

            MelonLogger.Msg($"Menu style from the title screen: font {style.Font.name} {style.FontSize:0.#}, "
                + $"material {(style.Material == null ? "(none)" : style.Material.name)}, selected {style.Selected}, "
                + $"not selected {style.NotSelected}, offset {style.SelectedOffsetX:0.#}, transition {style.TransitionSeconds:0.##} s, "
                + $"arrow {(style.Arrow == null ? "(none)" : $"{style.Arrow.name} {style.ArrowSize}")}, bounce {style.ArrowBounce:0.#}{(style.ArrowBounceAlways ? " always" : " once")}.");
            return style;
        }
        catch (Exception e)
        {
            MelonLogger.Warning($"Couldn't read the title screen's menu style: {e.Message}");
            return null;
        }
    }

    /// <summary>Not on the title screen yet: the font of some text on screen, else TextMeshPro's default.</summary>
    private static MenuStyle Fallback()
    {
        var style = new MenuStyle();
        try
        {
            var text = Object.FindFirstObjectByType<TextMeshProUGUI>();
            style.Font = text != null && text.font != null ? text.font : TMP_Settings.defaultFontAsset;
        }
        catch (Exception e)
        {
            MelonLogger.Warning($"Couldn't find a font for the menu: {e.Message}");
        }

        style.Material = style.Font?.material;
        MelonLogger.Msg($"Menu style: title screen not seen yet, using font {(style.Font == null ? "(none)" : style.Font.name)}.");
        return style;
    }

    /// <summary>
    /// A copy of a sprite in our own texture. The game's own sprite is destroyed with the title
    /// screen (it comes from an Addressables bundle, which keeping the asset doesn't stop), and an
    /// Image with a destroyed sprite draws a white square. Returns null if it can't be read.
    /// </summary>
    private static Sprite CopySprite(Sprite source)
    {
        var previous = RenderTexture.active;
        RenderTexture target = null;
        try
        {
            var area = source.textureRect;
            var texture = source.texture;
            target = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(texture, target);
            RenderTexture.active = target;
            var width = Mathf.RoundToInt(area.width);
            var height = Mathf.RoundToInt(area.height);
            var copy = Keep(new Texture2D(width, height, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear });
            copy.ReadPixels(new Rect(area.x, area.y, width, height), 0, 0);
            copy.Apply();
            var sprite = Keep(Sprite.Create(copy, new Rect(0f, 0f, width, height), new Vector2(0.5f, 0.5f), source.pixelsPerUnit));
            sprite.name = source.name;
            return sprite;
        }
        catch (Exception e)
        {
            MelonLogger.Warning($"Couldn't copy the title screen's arrow: {e.Message}");
            return null;
        }
        finally
        {
            RenderTexture.active = previous;
            if (target != null)
            {
                RenderTexture.ReleaseTemporary(target);
            }
        }
    }

    /// <summary>Survive the game's asset unloading on scene changes (see DecalPaint.Keep).</summary>
    private static T Keep<T>(T asset)
        where T : Object
    {
        if (asset != null)
        {
            asset.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        }

        return asset;
    }
}
