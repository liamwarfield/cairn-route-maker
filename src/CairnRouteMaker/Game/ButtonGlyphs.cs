using System;
using System.Collections.Generic;
using Il2CppTGBTools.InputImage;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CairnRouteMaker.Game;

/// <summary>
/// The button pictures the game draws in its own control hints: key caps, mouse buttons, sticks and
/// pad buttons. They come from the game's <c>InputImageManager</c>, which keeps one
/// <c>InputImageData</c> per device type (generic gamepad, which has the Xbox pictures, the
/// PlayStation pads, keyboard and mouse), each mapping a control path such as "/buttonSouth" to a
/// sprite. Only some keys have a picture; the rest get a blank key cap that the caller writes the
/// key's name on, the way the game does.
/// </summary>
internal static class ButtonGlyphs
{
    private const string GamepadType = "UnityEngine.InputSystem.Gamepad";
    private const string KeyboardType = "UnityEngine.InputSystem.Keyboard";
    private const string MouseType = "UnityEngine.InputSystem.Mouse";
    private const string FallbackPath = "/Fallback";

    // Context 0 holds the light pictures (pale buttons with dark letters) the game draws over the
    // world; context 1 holds dark ones. Variant 0 is the plain picture; the others are animation
    // frames and combined ones such as D-pad left-and-right.
    private const int LightContext = 0;
    private const int PlainVariant = 0;

    /// <summary>Device type full name, then control path, to sprite. Null until the game's data is loaded.</summary>
    private static Dictionary<string, Dictionary<string, Sprite>> _sprites;
    private static bool _logged;
    private static bool _warned;

    /// <summary>
    /// The picture for a binding's control, or null if there's none (the game's data isn't loaded,
    /// or it's a key with no picture of its own and <paramref name="blank"/> is false).
    /// </summary>
    /// <param name="layout">The binding's device layout, e.g. "Gamepad", "Keyboard", "Mouse" or "XInputController".</param>
    /// <param name="path">The control's path on the device, e.g. "buttonSouth" or "dpad/up".</param>
    /// <param name="blank">Fall back to a blank key cap.</param>
    /// <param name="isBlank">Set if the picture is the blank key cap, which needs a name written on it.</param>
    public static Sprite Find(string layout, string path, bool blank, out bool isBlank)
    {
        isBlank = false;
        if (!EnsureLoaded() || string.IsNullOrEmpty(path))
        {
            return null;
        }

        var key = "/" + path.TrimStart('/');
        var types = DeviceTypes(layout);
        foreach (var type in types)
        {
            if (TryGet(type, key, out var sprite))
            {
                return sprite;
            }
        }

        if (!blank)
        {
            return null;
        }

        // Pads have no blank picture of their own; a key cap with the button's name on it will do.
        isBlank = true;
        foreach (var type in types)
        {
            if (TryGet(type, FallbackPath, out var sprite))
            {
                return sprite;
            }
        }

        return TryGet(KeyboardType, FallbackPath, out var keyCap) ? keyCap : null;
    }

    /// <summary>The device types whose pictures fit a layout, most specific first.</summary>
    private static List<string> DeviceTypes(string layout)
    {
        var types = new List<string>();
        if (IsLayout(layout, "Keyboard"))
        {
            types.Add(KeyboardType);
        }
        else if (IsLayout(layout, "Mouse") || IsLayout(layout, "Pointer"))
        {
            types.Add(MouseType);
        }
        else
        {
            // The pad in use picks the pictures: a PlayStation pad's type (or one of its base
            // types) has its own, and anything else falls back to the generic Xbox-style ones.
            try
            {
                for (var type = Gamepad.current?.GetIl2CppType(); type != null; type = type.BaseType)
                {
                    types.Add(type.FullName);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"Couldn't read the gamepad's type for its button pictures: {e.Message}");
            }

            types.Add(GamepadType);
        }

        return types;
    }

    private static bool IsLayout(string layout, string baseLayout)
    {
        if (string.IsNullOrEmpty(layout))
        {
            return false;
        }

        if (string.Equals(layout, baseLayout, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            return InputSystem.IsFirstLayoutBasedOnSecond(layout, baseLayout);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryGet(string type, string path, out Sprite sprite)
    {
        sprite = null;
        if (_sprites != null && _sprites.TryGetValue(type, out var paths) && paths.TryGetValue(path, out var found))
        {
            if (found == null)
            {
                // Unloaded under us: read the data again next time.
                _sprites = null;
                return false;
            }

            sprite = found;
            return true;
        }

        return false;
    }

    private static bool EnsureLoaded()
    {
        if (_sprites != null)
        {
            return true;
        }

        try
        {
            if (!InputImageManager.IsAvailable())
            {
                return false;
            }

            var manager = InputImageManager.Instance;
            var data = manager?.inputImagesData;
            if (manager == null || !manager.IsReady || data == null || data.Count == 0)
            {
                return false;
            }

            var sprites = new Dictionary<string, Dictionary<string, Sprite>>();
            for (var i = 0; i < data.Count; i++)
            {
                var device = data[i];
                if (device == null || string.IsNullOrEmpty(device.deviceFullType) || device.contextualSprites == null)
                {
                    continue;
                }

                var paths = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
                for (var c = 0; c < device.contextualSprites.Count; c++)
                {
                    var context = device.contextualSprites[c];
                    if (context == null || context.context.value != LightContext || context.pathsData == null)
                    {
                        continue;
                    }

                    for (var p = 0; p < context.pathsData.Count; p++)
                    {
                        var pathData = context.pathsData[p];
                        var variants = pathData?.spriteVariants;
                        if (variants == null || string.IsNullOrEmpty(pathData.relativePath))
                        {
                            continue;
                        }

                        for (var v = 0; v < variants.Count; v++)
                        {
                            var variant = variants[v];
                            if (variant != null && variant.variant.value == PlainVariant && variant.sprite != null)
                            {
                                paths[pathData.relativePath] = variant.sprite;
                            }
                        }
                    }
                }

                sprites[device.deviceFullType] = paths;
            }

            _sprites = sprites;
            if (!_logged)
            {
                _logged = true;
                var summary = new List<string>();
                foreach (var (type, paths) in sprites)
                {
                    summary.Add($"{type.Substring(type.LastIndexOf('.') + 1)} {paths.Count}");
                }

                MelonLogger.Msg($"Button pictures from the game: {string.Join(", ", summary)}.");
            }

            return true;
        }
        catch (Exception e)
        {
            if (!_warned)
            {
                _warned = true;
                MelonLogger.Warning($"Couldn't read the game's button pictures: {e}");
            }

            return false;
        }
    }
}
