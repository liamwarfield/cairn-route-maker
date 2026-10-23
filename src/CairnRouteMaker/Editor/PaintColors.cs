using System;
using System.Globalization;
using CairnRouteMaker.Routes;
using MelonLoader;
using UnityEngine;

namespace CairnRouteMaker.Editor;

/// <summary>
/// The colours of allow and deny paint, which the brush preview and the next-grab indicator follow
/// too. Picked on the menu's Paint Colours page and saved as #RRGGBB in UserData/MelonPreferences.cfg,
/// where any other colour can be typed in while the game is closed. The default yellow and red are
/// hard to tell apart with red-green colour blindness; the blue and orange preset stays distinct
/// under simulated protanopia, deuteranopia and tritanopia.
/// </summary>
internal static class PaintColors
{
    public sealed class Choice
    {
        public Choice(string name, uint rgb)
        {
            Name = name;
            Color = FromRgb(rgb);
        }

        public string Name { get; }
        public Color32 Color { get; }
    }

    public sealed class Preset
    {
        public Preset(string name, string note, Choice allow, Choice deny)
        {
            Name = name;
            Note = note;
            Allow = allow.Color;
            Deny = deny.Color;
        }

        public string Name { get; }
        public string Note { get; }
        public Color32 Allow { get; }
        public Color32 Deny { get; }
    }

    private static readonly Choice Yellow = new("Yellow", 0xFFCD28);
    private static readonly Choice Red = new("Red", 0xF02D1E);
    private static readonly Choice Blue = new("Blue", 0x3C9BFF);
    private static readonly Choice Orange = new("Orange", 0xFF8C1A);

    /// <summary>Bright enough to show on dark rock. No black: that's the erase brush.</summary>
    public static readonly Choice[] Palette =
    {
        Yellow, Orange, Red, new("Magenta", 0xE633C8), new("Purple", 0x9B5CFF), Blue, new("Cyan", 0x1EDCE6), new("Green", 0x46D25A), new("White", 0xF0F0F0),
    };

    /// <summary>The first is the default.</summary>
    public static readonly Preset[] Presets =
    {
        new("Yellow and red", "default", Yellow, Red),
        new("Blue and orange", "colour-blind friendly", Blue, Orange),
    };

    private static MelonPreferences_Category _category;
    private static MelonPreferences_Entry<string> _allowEntry;
    private static MelonPreferences_Entry<string> _denyEntry;

    public static Color32 Allow { get; private set; } = Yellow.Color;
    public static Color32 Deny { get; private set; } = Red.Color;

    /// <summary>Raised after either colour changes.</summary>
    public static event Action Changed;

    public static void Load()
    {
        _category = MelonPreferences.CreateCategory("CairnRouteMaker", "Cairn Route Maker");
        _allowEntry = _category.CreateEntry("AllowPaintColor", ToHex(Yellow.Color), "Allow paint colour", "Route paint for holds Ava may use, as #RRGGBB.");
        _denyEntry = _category.CreateEntry("DenyPaintColor", ToHex(Red.Color), "Deny paint colour", "Route paint for holds that are off-route, as #RRGGBB.");
        Allow = Read(_allowEntry, Yellow.Color);
        Deny = Read(_denyEntry, Red.Color);
    }

    public static Color32 Of(StrokeMode mode) => mode == StrokeMode.Allow ? Allow : Deny;

    /// <summary>Paint one kind in a colour. If the other kind already has it, the two swap, so they never match.</summary>
    public static void Set(StrokeMode mode, Color32 color)
    {
        if (mode == StrokeMode.Allow)
        {
            SetBoth(color, Same(color, Deny) ? Allow : Deny);
        }
        else
        {
            SetBoth(Same(color, Allow) ? Deny : Allow, color);
        }
    }

    public static void SetBoth(Color32 allow, Color32 deny)
    {
        Allow = allow;
        Deny = deny;
        if (_category != null)
        {
            _allowEntry.Value = ToHex(allow);
            _denyEntry.Value = ToHex(deny);
            _category.SaveToFile(false);
        }

        Changed?.Invoke();
    }

    /// <summary>The palette name of a colour, or its hex for one typed into the preferences file.</summary>
    public static string NameOf(Color32 color)
    {
        foreach (var choice in Palette)
        {
            if (Same(choice.Color, color))
            {
                return choice.Name;
            }
        }

        return ToHex(color);
    }

    public static bool Same(Color32 a, Color32 b) => a.r == b.r && a.g == b.g && a.b == b.b;

    private static Color32 Read(MelonPreferences_Entry<string> entry, Color32 fallback)
    {
        if (TryParse(entry.Value, out var color))
        {
            return color;
        }

        MelonLogger.Warning($"{entry.Identifier} '{entry.Value}' in MelonPreferences.cfg isn't a #RRGGBB colour; using {ToHex(fallback)}.");
        return fallback;
    }

    private static bool TryParse(string text, out Color32 color)
    {
        var hex = text?.Trim().TrimStart('#');
        if (hex?.Length == 6 && uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rgb))
        {
            color = FromRgb(rgb);
            return true;
        }

        color = default;
        return false;
    }

    private static Color32 FromRgb(uint rgb) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);

    private static string ToHex(Color32 color) => $"#{color.r:X2}{color.g:X2}{color.b:X2}";
}
