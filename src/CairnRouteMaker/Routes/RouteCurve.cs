using System;
using System.Collections.Generic;
using System.Numerics;

namespace CairnRouteMaker.Routes;

/// <summary>A point on the sampled route line, with the rock normal blended between the control points.</summary>
public readonly record struct CurveSample(Vector3 Position, Vector3 Normal);

/// <summary>
/// The smooth curve of a route line: a centripetal Catmull-Rom spline through the control points,
/// sampled into a polyline. It passes through every point the author placed (so the line stays on
/// the rock where they clicked, with no handles to manage from a gamepad), and the centripetal
/// parameterisation keeps it from looping or overshooting where points are bunched up or unevenly
/// spaced, which plain (uniform) Catmull-Rom does. The ends are extended by mirroring the
/// neighbouring point, so two points give a straight line.
/// </summary>
public static class RouteCurve
{
    /// <summary>0 is uniform, 0.5 centripetal, 1 chordal.</summary>
    private const float Alpha = 0.5f;

    /// <summary>Control points closer than this count as one, so the knot spacing never divides by zero.</summary>
    private const float MinKnotGap = 1e-4f;

    /// <summary>Each segment is split into at most this many pieces, however long it is.</summary>
    public const int MaxStepsPerSegment = 512;

    /// <summary>Points per segment of the fine pass that measures distance along the curve.</summary>
    private const int DenseSteps = 64;

    public static int SegmentCount(int pointCount) => Math.Max(0, pointCount - 1);

    /// <summary>The whole line as a polyline with pieces about <paramref name="spacing"/> long.</summary>
    public static List<CurveSample> Sample(IReadOnlyList<LinePoint> points, float spacing)
    {
        var samples = new List<CurveSample>();
        if (points.Count == 1)
        {
            samples.Add(new CurveSample(points[0].Position, SafeNormal(points[0].Normal, Vector3.UnitZ)));
        }

        for (var segment = 0; segment < SegmentCount(points.Count); segment++)
        {
            SampleSegment(points, segment, spacing, samples, includeStart: segment == 0);
        }

        return samples;
    }

    /// <summary>
    /// Append the samples of the piece between points <paramref name="segment"/> and
    /// <paramref name="segment"/> + 1. The segment's end point is always included; its start only if
    /// asked, so consecutive segments join without a duplicate. A segment depends on the point
    /// before and the point after it too (see <see cref="Neighbours"/>).
    /// </summary>
    public static void SampleSegment(IReadOnlyList<LinePoint> points, int segment, float spacing, List<CurveSample> output, bool includeStart)
    {
        var (p0, p1, p2, p3) = Neighbours(points, segment);
        var n1 = SafeNormal(points[segment].Normal, Vector3.UnitZ);
        var n2 = SafeNormal(points[segment + 1].Normal, n1);
        if (includeStart)
        {
            output.Add(new CurveSample(p1, n1));
        }

        // The spline's parameter doesn't move at a steady speed, so walk a finer polyline and take
        // points at even distances along it.
        var dense = new Vector3[DenseSteps + 1];
        var distance = new float[DenseSteps + 1];
        dense[0] = p1;
        for (var i = 1; i <= DenseSteps; i++)
        {
            dense[i] = i == DenseSteps ? p2 : Evaluate(p0, p1, p2, p3, (float)i / DenseSteps);
            distance[i] = distance[i - 1] + Vector3.Distance(dense[i - 1], dense[i]);
        }

        var length = distance[DenseSteps];
        var steps = length > 0f && spacing > 0f ? Math.Clamp((int)MathF.Ceiling(length / spacing), 1, MaxStepsPerSegment) : 1;
        var j = 1;
        for (var i = 1; i < steps; i++)
        {
            var target = length * i / steps;
            while (distance[j] < target)
            {
                j++;
            }

            var piece = distance[j] - distance[j - 1];
            var f = piece > 0f ? (target - distance[j - 1]) / piece : 0f;
            var u = (j - 1 + f) / DenseSteps;
            output.Add(new CurveSample(Vector3.Lerp(dense[j - 1], dense[j], f), SafeNormal(Vector3.Lerp(n1, n2, u), n1)));
        }

        output.Add(new CurveSample(p2, n2));
    }

    /// <summary>
    /// The four positions that shape a segment: its two ends and the points either side. Past the
    /// ends of the line, the missing neighbour is the next point mirrored through the end point.
    /// </summary>
    public static (Vector3 P0, Vector3 P1, Vector3 P2, Vector3 P3) Neighbours(IReadOnlyList<LinePoint> points, int segment)
    {
        var p1 = points[segment].Position;
        var p2 = points[segment + 1].Position;
        var p0 = segment > 0 ? points[segment - 1].Position : 2f * p1 - p2;
        var p3 = segment + 2 < points.Count ? points[segment + 2].Position : 2f * p2 - p1;
        return (p0, p1, p2, p3);
    }

    /// <summary>
    /// The curve between <paramref name="p1"/> (u = 0) and <paramref name="p2"/> (u = 1), by the
    /// Barry-Goldman pyramid with knots spaced by distance^<see cref="Alpha"/>.
    /// </summary>
    public static Vector3 Evaluate(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float u)
    {
        const float t0 = 0f;
        var t1 = t0 + Knot(p0, p1);
        var t2 = t1 + Knot(p1, p2);
        var t3 = t2 + Knot(p2, p3);
        var t = t1 + (t2 - t1) * u;

        var a1 = Blend(p0, p1, t0, t1, t);
        var a2 = Blend(p1, p2, t1, t2, t);
        var a3 = Blend(p2, p3, t2, t3, t);
        var b1 = Blend(a1, a2, t0, t2, t);
        var b2 = Blend(a2, a3, t1, t3, t);
        return Blend(b1, b2, t1, t2, t);
    }

    /// <summary>Total length of a polyline.</summary>
    public static float Length(IReadOnlyList<CurveSample> samples)
    {
        var length = 0f;
        for (var i = 1; i < samples.Count; i++)
        {
            length += Vector3.Distance(samples[i - 1].Position, samples[i].Position);
        }

        return length;
    }

    private static Vector3 Blend(Vector3 a, Vector3 b, float ta, float tb, float t) =>
        (tb - t) / (tb - ta) * a + (t - ta) / (tb - ta) * b;

    private static float Knot(Vector3 a, Vector3 b) => MathF.Max(MathF.Pow(Vector3.Distance(a, b), Alpha), MinKnotGap);

    private static Vector3 SafeNormal(Vector3 normal, Vector3 fallback)
    {
        var length = normal.Length();
        return length > 1e-4f && float.IsFinite(length) ? normal / length : fallback;
    }
}
