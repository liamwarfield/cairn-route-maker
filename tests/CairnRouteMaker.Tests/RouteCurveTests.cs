using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CairnRouteMaker.Routes;
using Xunit;

namespace CairnRouteMaker.Tests;

public class RouteCurveTests
{
    private static List<LinePoint> Points(params Vector3[] positions) =>
        positions.Select(p => new LinePoint(p, Vector3.UnitZ)).ToList();

    [Fact]
    public void NoPointsGiveNoSamplesAndOneGivesOne()
    {
        Assert.Empty(RouteCurve.Sample(new List<LinePoint>(), 0.25f));

        var single = RouteCurve.Sample(Points(new Vector3(1, 2, 3)), 0.25f);

        Assert.Equal(new Vector3(1, 2, 3), Assert.Single(single).Position);
    }

    [Fact]
    public void TwoPointsGiveAnEvenlySampledStraightLine()
    {
        var samples = RouteCurve.Sample(Points(Vector3.Zero, new Vector3(0, 10, 0)), 0.25f);

        Assert.Equal(41, samples.Count);
        Assert.All(samples, s => Assert.Equal(0f, s.Position.X, 4));
        for (var i = 1; i < samples.Count; i++)
        {
            Assert.Equal(0.25f, Vector3.Distance(samples[i - 1].Position, samples[i].Position), 3);
        }
    }

    [Fact]
    public void PassesThroughEveryControlPoint()
    {
        var points = Points(Vector3.Zero, new Vector3(2, 3, 0), new Vector3(-1, 6, 1), new Vector3(0.5f, 9, 0), new Vector3(3, 11, 0));

        var samples = RouteCurve.Sample(points, 0.2f);

        foreach (var point in points)
        {
            Assert.Contains(samples, s => Vector3.Distance(s.Position, point.Position) < 1e-4f);
        }

        Assert.Equal(points[0].Position, samples[0].Position);
        Assert.Equal(points[^1].Position, samples[^1].Position);
    }

    [Fact]
    public void NoPieceIsMuchLongerThanTheSpacing()
    {
        var points = Points(Vector3.Zero, new Vector3(4, 2, 0), new Vector3(0, 5, 0), new Vector3(6, 9, 2));

        var samples = RouteCurve.Sample(points, 0.3f);

        for (var i = 1; i < samples.Count; i++)
        {
            Assert.True(Vector3.Distance(samples[i - 1].Position, samples[i].Position) < 0.3f * 1.25f);
        }
    }

    [Fact]
    public void BunchedUpPointsDontMakeTheCurveOvershoot()
    {
        // Uniform Catmull-Rom swings well past the short middle segment here; centripetal stays close.
        var points = Points(Vector3.Zero, new Vector3(10, 0, 0), new Vector3(10.3f, 0.3f, 0), new Vector3(10.3f, 10.3f, 0));

        var samples = RouteCurve.Sample(points, 0.05f);

        Assert.All(samples, s => Assert.InRange(s.Position.X, -0.01f, 10.5f));
        Assert.All(samples, s => Assert.InRange(s.Position.Y, -0.2f, 10.31f));
    }

    [Fact]
    public void RepeatedPointsDontProduceNaNs()
    {
        var points = Points(Vector3.Zero, Vector3.Zero, new Vector3(0, 3, 0), new Vector3(0, 3, 0));

        var samples = RouteCurve.Sample(points, 0.25f);

        Assert.All(samples, s => Assert.True(float.IsFinite(s.Position.X + s.Position.Y + s.Position.Z)));
        Assert.Equal(new Vector3(0, 3, 0), samples[^1].Position);
    }

    [Fact]
    public void NormalsBlendBetweenControlPointsAndStayUnitLength()
    {
        var points = new List<LinePoint>
        {
            new(Vector3.Zero, Vector3.UnitZ),
            new(new Vector3(0, 4, 0), -Vector3.UnitX),
        };

        var samples = RouteCurve.Sample(points, 0.5f);

        Assert.All(samples, s => Assert.Equal(1f, s.Normal.Length(), 4));
        Assert.Equal(Vector3.UnitZ, samples[0].Normal);
        Assert.Equal(-Vector3.UnitX, samples[^1].Normal);
        var middle = samples[samples.Count / 2].Normal;
        Assert.True(middle.Z > 0.5f && middle.X < -0.5f);
    }

    [Fact]
    public void SegmentsSampledSeparatelyJoinIntoTheWholeLine()
    {
        var points = Points(Vector3.Zero, new Vector3(1, 2, 0), new Vector3(0, 4, 1), new Vector3(2, 6, 0));
        var pieces = new List<CurveSample>();
        for (var segment = 0; segment < RouteCurve.SegmentCount(points.Count); segment++)
        {
            RouteCurve.SampleSegment(points, segment, 0.25f, pieces, includeStart: segment == 0);
        }

        Assert.Equal(RouteCurve.Sample(points, 0.25f), pieces);
    }
}
