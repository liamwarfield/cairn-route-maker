using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CairnRouteMaker.Routes;
using Xunit;

namespace CairnRouteMaker.Tests;

public class RappelDotsTests
{
    private static List<RappelDot> Lay(Vector3[] polyline, float finest)
    {
        var dots = new List<RappelDot>();
        RappelDots.Lay(polyline, 0, polyline.Length - 1, finest, dots);
        return dots;
    }

    [Fact]
    public void SpacesAPowerOfTwoOfGapsEvenlyFromEndToEnd()
    {
        // 10 m at 0.3 m or more apart: 32 gaps of 0.3125 m.
        var dots = Lay(new[] { Vector3.Zero, new Vector3(0, -4, 0), new Vector3(0, -10, 0) }, 0.3f);

        Assert.Equal(33, dots.Count);
        Assert.All(dots, dot => Assert.Equal(0.3125f, dot.Spacing, 4));
        for (var j = 0; j < dots.Count; j++)
        {
            Assert.Equal(-0.3125f * j, dots[j].Position.Y, 3);
        }
    }

    [Fact]
    public void EveryOtherDotIsALevelCoarserAndTheEndsAlwaysStay()
    {
        var dots = Lay(new[] { Vector3.Zero, new Vector3(0, -10, 0) }, 0.3f);

        Assert.Equal(RappelDots.Ends, dots[0].Level);
        Assert.Equal(RappelDots.Ends, dots[^1].Level);
        Assert.Equal(new[] { 0, 1, 0, 2, 0, 1, 0, 3, 0 }, dots.Skip(1).Take(9).Select(dot => dot.Level));
        Assert.Equal(4, dots[16].Level);
    }

    [Fact]
    public void FollowsBendsInThePolyline()
    {
        var dots = Lay(new[] { Vector3.Zero, new Vector3(0, -1, 0), new Vector3(1, -1, 0) }, 0.5f);

        Assert.Equal(5, dots.Count);
        Assert.Equal(new Vector3(0, -0.5f, 0), dots[1].Position);
        Assert.Equal(new Vector3(0, -1, 0), dots[2].Position);
        Assert.Equal(new Vector3(0.5f, -1, 0), dots[3].Position);
        Assert.Equal(new Vector3(1, -1, 0), dots[4].Position);
        Assert.Equal(-Vector3.UnitY, dots[1].Direction);
        Assert.Equal(Vector3.UnitX, dots[3].Direction);
    }

    [Fact]
    public void AShortRunIsJustItsEnds()
    {
        var dots = Lay(new[] { Vector3.Zero, new Vector3(0, -0.2f, 0) }, 0.3f);

        Assert.Equal(2, dots.Count);
        Assert.All(dots, dot => Assert.Equal(RappelDots.Ends, dot.Level));
    }

    [Fact]
    public void DotsShrinkAwayAsTheyCrowdTogether()
    {
        var finest = new RappelDot(Vector3.Zero, Vector3.UnitY, 1f, 0);
        var coarser = new RappelDot(Vector3.Zero, Vector3.UnitY, 1f, 1);
        var end = new RappelDot(Vector3.Zero, Vector3.UnitY, 1f, RappelDots.Ends);

        // Far enough apart: everything's drawn.
        Assert.Equal(1f, RappelDots.Scale(finest, 0.5f));
        Assert.Equal(1f, RappelDots.Scale(finest, 1f));

        // A bit too close: the finest dots shrink, every other one stays.
        Assert.Equal(1f - 0.5849625f, RappelDots.Scale(finest, 1.5f), 4);
        Assert.Equal(1f, RappelDots.Scale(coarser, 1.5f));

        // Twice too close: the finest are gone, and then the next ones start shrinking.
        Assert.Equal(0f, RappelDots.Scale(finest, 2f));
        Assert.Equal(1f, RappelDots.Scale(coarser, 2f));
        Assert.Equal(0f, RappelDots.Scale(finest, 3f));
        Assert.True(RappelDots.Scale(coarser, 3f) is > 0f and < 1f);
        Assert.Equal(1f, RappelDots.Scale(end, 1000f));
    }
}
