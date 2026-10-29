using System;
using System.Collections.Generic;
using System.Numerics;

namespace CairnRouteMaker.Routes;

/// <summary>
/// One dot of a rappel: where it is, which way the line runs there, how far apart its run's dots
/// are, and how many times that spacing can double with this dot still among those drawn (see
/// <see cref="RappelDots"/>).
/// </summary>
public readonly record struct RappelDot(Vector3 Position, Vector3 Direction, float Spacing, int Level);

/// <summary>
/// Lays out the dots that draw the rappel pieces of a route line, so they're about the same distance
/// apart on screen near and far, and don't crawl along the line as the camera moves.
///
/// Each run of rappel pieces gets a power of two of equal gaps, so every other dot is also a dot of
/// a run spaced twice as far apart, and so on up to the two ends. Further away, the dots that would
/// crowd together shrink away, the extra ones first.
/// </summary>
public static class RappelDots
{
    /// <summary>The level of a run's two ends, which are always drawn.</summary>
    public const int Ends = int.MaxValue;

    /// <summary>
    /// Add the dots of the run from <paramref name="start"/> to <paramref name="end"/> along a
    /// polyline, at least <paramref name="finest"/> apart.
    /// </summary>
    public static void Lay(IReadOnlyList<Vector3> polyline, int start, int end, float finest, List<RappelDot> output)
    {
        var length = Length(polyline, start, end);
        var gaps = length > finest ? 1 << (int)MathF.Log2(length / finest) : 1;
        var spacing = MathF.Max(length / gaps, 1e-3f);
        var sample = start;
        var walked = 0f;
        var piece = Vector3.Distance(polyline[start], polyline[start + 1]);
        for (var j = 0; j <= gaps; j++)
        {
            var along = length * j / gaps;
            while (sample < end - 1 && walked + piece < along)
            {
                walked += piece;
                sample++;
                piece = Vector3.Distance(polyline[sample], polyline[sample + 1]);
            }

            var t = piece > 0f ? Math.Clamp((along - walked) / piece, 0f, 1f) : 0f;
            var level = j == 0 || j == gaps ? Ends : BitOperations.TrailingZeroCount(j);
            var direction = piece > 0f ? (polyline[sample + 1] - polyline[sample]) / piece : Vector3.Zero;
            output.Add(new RappelDot(Vector3.Lerp(polyline[sample], polyline[sample + 1], t), direction, spacing, level));
        }
    }

    /// <summary>The length of a polyline from one point to another.</summary>
    public static float Length(IReadOnlyList<Vector3> polyline, int start, int end)
    {
        var length = 0f;
        for (var i = start; i < end; i++)
        {
            length += Vector3.Distance(polyline[i], polyline[i + 1]);
        }

        return length;
    }

    /// <summary>
    /// How big to draw a dot, from 1 down to 0 (hidden), when dots should be at least
    /// <paramref name="apart"/> apart where it is. Dots that are only just too close are drawn
    /// smaller, so they shrink away as the camera pulls back instead of popping out.
    /// </summary>
    public static float Scale(RappelDot dot, float apart)
    {
        // How many times the spacing has to double to be far enough apart.
        var doublings = MathF.Log2(apart / dot.Spacing);
        if (doublings <= 0f)
        {
            return 1f;
        }

        var level = (int)MathF.Ceiling(doublings);
        return dot.Level >= level ? 1f : dot.Level == level - 1 ? level - doublings : 0f;
    }
}
