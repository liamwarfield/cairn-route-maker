using System;
using CairnRouteMaker.Routes;
using UnityEngine;

namespace CairnRouteMaker.Editor;

/// <summary>
/// The colours of route lines: the game map's difficulty scale, yellow (easiest) to white (hardest).
/// Black is the one dark colour, so it gets a light border instead of the usual dark one.
/// </summary>
internal static class LineColors
{
    private static readonly Color32 DarkBorder = new(16, 16, 20, 230);
    private static readonly Color32 LightBorder = new(235, 235, 240, 230);

    public static readonly LineGrade[] All = (LineGrade[])Enum.GetValues(typeof(LineGrade));

    public static Color32 Of(LineGrade grade) => grade switch
    {
        LineGrade.Yellow => new Color32(255, 210, 30, 255),
        LineGrade.Green => new Color32(60, 200, 60, 255),
        LineGrade.Blue => new Color32(40, 110, 255, 255),
        LineGrade.Red => new Color32(232, 35, 30, 255),
        LineGrade.Black => new Color32(20, 20, 22, 255),
        LineGrade.Purple => new Color32(160, 50, 220, 255),
        _ => new Color32(245, 245, 245, 255),
    };

    public static Color32 BorderOf(LineGrade grade) => grade == LineGrade.Black ? LightBorder : DarkBorder;

    public static string NameOf(LineGrade grade) => grade.ToString();

    /// <summary>The next colour along the scale, wrapping from white back to yellow.</summary>
    public static LineGrade Next(LineGrade grade) => All[((int)grade + 1) % All.Length];
}
