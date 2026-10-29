using System;
using System.Globalization;
using CairnRouteMaker.Routes;
using MelonLoader;
using UnityEngine;

namespace CairnRouteMaker.Editor;

/// <summary>How route paint is drawn: a translucent fill, a glow along its edges, or both.</summary>
internal enum PaintStyle
{
    FillAndGlow,
    GlowOnly,
    FillOnly,
}

/// <summary>
/// How route paint looks: the colours of allow and deny paint, which the brush preview and the
/// next-grab indicator follow too, whether it's drawn as a fill, a glow along its edges or both,
/// and whether the glow pulses; also whether route lines show (each line has its own colour, see
/// <see cref="LineColors"/>), and how far from Ava (or the editor camera) the paint shows. Set on the
/// Routes page (the Routes tab of the game's Settings) and saved in UserData/MelonPreferences.cfg,
/// the colours as #RRGGBB, so any other colour can be typed in while the game is closed. The
/// default yellow and red are hard to tell apart with red-green colour blindness; the blue and
/// orange preset stays distinct under simulated protanopia, deuteranopia and tritanopia.
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
    private static MelonPreferences_Entry<string> _styleEntry;
    private static MelonPreferences_Entry<bool> _pulseEntry;
    private static MelonPreferences_Entry<bool> _showLineEntry;
    private static MelonPreferences_Entry<float> _distanceEntry;

    private const float DefaultPaintDistance = 15f;

    /// <summary>
    /// The levels <see cref="PaintDistance"/> is offered at on the Routes page, lowest first: a
    /// name, and the distance in metres (0 for all of the paint).
    /// </summary>
    public static readonly (string Name, float Metres)[] PaintDistanceLevels =
    {
        ("Extra low", 10f), ("Low", DefaultPaintDistance), ("Medium", 25f), ("High", 40f), ("Max", 0f),
    };

    public static Color32 Allow { get; private set; } = Yellow.Color;
    public static Color32 Deny { get; private set; } = Red.Color;
    public static PaintStyle Style { get; private set; } = PaintStyle.FillAndGlow;
    public static bool GlowPulse { get; private set; } = true;
    public static bool ShowLine { get; private set; } = true;

    /// <summary>
    /// Paint only shows within this many metres of Ava, or of the camera in the route editor,
    /// fading out towards the edge, since every painted spot costs frame rate and the route lines
    /// show the way from further off. 0 shows it everywhere.
    /// </summary>
    public static float PaintDistance { get; private set; } = DefaultPaintDistance;

    public static bool ShowsFill => Style != PaintStyle.GlowOnly;
    public static bool ShowsGlow => Style != PaintStyle.FillOnly;

    /// <summary>Raised after anything here changes.</summary>
    public static event Action Changed;

    public static void Load()
    {
        _category = MelonPreferences.CreateCategory("CairnRouteMaker", "Cairn Route Maker");
        _allowEntry = _category.CreateEntry("AllowPaintColor", ToHex(Yellow.Color), "Allow paint colour", "Route paint for holds Ava may use, as #RRGGBB.");
        _denyEntry = _category.CreateEntry("DenyPaintColor", ToHex(Red.Color), "Deny paint colour", "Route paint for holds that are off-route, as #RRGGBB.");
        _styleEntry = _category.CreateEntry("PaintStyle", PaintStyle.FillAndGlow.ToString(), "Paint style", "FillAndGlow, GlowOnly or FillOnly.");
        _pulseEntry = _category.CreateEntry("PaintGlowPulse", true, "Paint glow pulse", "Slowly pulse the glow along the edges of route paint.");
        _showLineEntry = _category.CreateEntry("ShowRouteLine", true, "Show route lines", "Draw route lines over the rock.");
        _distanceEntry = _category.CreateEntry("PaintShowDistance", DefaultPaintDistance, "Paint show distance",
            "Route paint only shows within this many metres of Ava (of the camera in the route editor and freecam), fading out towards the edge; 0 shows it everywhere.");
        Allow = Read(_allowEntry, Yellow.Color);
        Deny = Read(_denyEntry, Red.Color);
        Style = ReadStyle(_styleEntry);
        GlowPulse = _pulseEntry.Value;
        ShowLine = _showLineEntry.Value;
        PaintDistance = ReadDistance(_distanceEntry);
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

    public static void ApplyPreset(Preset preset) => SetBoth(preset.Allow, preset.Deny);

    public static void SetShowLine(bool on)
    {
        ShowLine = on;
        if (_category != null)
        {
            _showLineEntry.Value = on;
            _category.SaveToFile(false);
        }

        Changed?.Invoke();
    }

    public static void SetStyle(PaintStyle style)
    {
        Style = style;
        if (_category != null)
        {
            _styleEntry.Value = style.ToString();
            _category.SaveToFile(false);
        }

        Changed?.Invoke();
    }

    public static void SetGlowPulse(bool on)
    {
        GlowPulse = on;
        if (_category != null)
        {
            _pulseEntry.Value = on;
            _category.SaveToFile(false);
        }

        Changed?.Invoke();
    }

    public static void SetPaintDistance(float metres)
    {
        PaintDistance = metres;
        if (_category != null)
        {
            _distanceEntry.Value = metres;
            _category.SaveToFile(false);
        }

        Changed?.Invoke();
    }

    public static void SetPaintDistanceLevel(int level) => SetPaintDistance(PaintDistanceLevels[level].Metres);

    /// <summary>
    /// The level nearest a distance, for showing one typed into MelonPreferences.cfg that isn't a
    /// level's: 0 (all of the paint) is Max, anything else the nearest of the rest.
    /// </summary>
    public static int LevelOf(float metres)
    {
        var best = 0;
        for (var i = 0; i < PaintDistanceLevels.Length; i++)
        {
            var level = PaintDistanceLevels[i].Metres;
            if (level == metres)
            {
                return i;
            }

            if (level > 0f && metres > 0f && MathF.Abs(level - metres) < MathF.Abs(PaintDistanceLevels[best].Metres - metres))
            {
                best = i;
            }
        }

        return metres > 0f ? best : Array.FindIndex(PaintDistanceLevels, l => l.Metres == 0f);
    }

    /// <summary>"Low (15 m)", "Max (all paint)", or just "20 m" for a distance that isn't a level's.</summary>
    public static string DescribePaintDistance(float metres)
    {
        var distance = metres > 0f ? $"{metres:0.#} m" : "all paint";
        var level = PaintDistanceLevels[LevelOf(metres)];
        return level.Metres == metres ? $"{level.Name} ({distance})" : distance;
    }

    public static string NameOf(PaintStyle style) => style switch
    {
        PaintStyle.GlowOnly => "Glow only",
        PaintStyle.FillOnly => "Fill only",
        _ => "Fill and glow",
    };

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

    private static PaintStyle ReadStyle(MelonPreferences_Entry<string> entry)
    {
        if (Enum.TryParse<PaintStyle>(entry.Value, true, out var style) && Enum.IsDefined(style))
        {
            return style;
        }

        MelonLogger.Warning($"{entry.Identifier} '{entry.Value}' in MelonPreferences.cfg isn't FillAndGlow, GlowOnly or FillOnly; using FillAndGlow.");
        return PaintStyle.FillAndGlow;
    }

    private static float ReadDistance(MelonPreferences_Entry<float> entry)
    {
        if (entry.Value >= 0f && float.IsFinite(entry.Value))
        {
            return entry.Value;
        }

        MelonLogger.Warning($"{entry.Identifier} '{entry.Value}' in MelonPreferences.cfg isn't a distance in metres; using {DefaultPaintDistance}.");
        return DefaultPaintDistance;
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
