using System.Numerics;
using CairnRouteMaker.Routes;
using Xunit;

namespace CairnRouteMaker.Tests;

public class AttemptTests
{
    private static readonly Vector3 Up = Vector3.UnitY * 1.6f;

    /// <summary>Start on the ground at the origin, the wall painted from y=0 to y=10, finish on top.</summary>
    private static Route WallRoute()
    {
        var route = new Route
        {
            Start = new StartPosition { Position = Vector3.Zero },
            Finish = new FinishZone { Position = new Vector3(0, 10, 1), Radius = 1f },
        };
        var stroke = new Stroke(StrokeMode.Allow);
        for (var y = 0; y <= 10; y++)
        {
            stroke.Stamps.Add(new Stamp(new Vector3(0, y, 1), -Vector3.UnitZ, 1f));
        }

        route.Strokes.Add(stroke);
        var deny = new Stroke(StrokeMode.Deny);
        deny.Stamps.Add(new Stamp(new Vector3(0, 5, 1), -Vector3.UnitZ, 0.3f));
        route.Strokes.Add(deny);
        return route;
    }

    private static void Step(Attempt attempt, Route route, double now, Vector3 position, bool climbing) =>
        attempt.Update(route, now, position, position + Up, climbing);

    [Fact]
    public void TimesAClimbFromLeavingTheGroundToTheFinish()
    {
        var route = WallRoute();
        var attempt = new Attempt();

        Step(attempt, route, 1, new Vector3(0.5f, 0, 0), climbing: false);
        Assert.Equal(AttemptState.Ready, attempt.State);
        Assert.Equal(0, attempt.Elapsed(5));

        Step(attempt, route, 2, new Vector3(0, 0.5f, 0.5f), climbing: true);
        Assert.Equal(AttemptState.Climbing, attempt.State);
        Assert.Equal(3, attempt.Elapsed(5));

        Assert.False(attempt.OnGrab(route, new Vector3(0, 3, 1)));
        Step(attempt, route, 30, new Vector3(0, 8.5f, 0.6f), climbing: true);
        Assert.Equal(AttemptState.Finished, attempt.State);
        Assert.False(attempt.OffRoute);
        Assert.Equal(28, attempt.Elapsed(100));
    }

    [Fact]
    public void GrabbingDeniedOrUnpaintedRockFailsTheAttemptButKeepsTheClock()
    {
        var route = WallRoute();
        var attempt = new Attempt();
        Step(attempt, route, 0, Vector3.Zero, climbing: false);
        Step(attempt, route, 1, Vector3.Zero, climbing: true);

        Assert.True(attempt.OnGrab(route, new Vector3(0, 5, 1)));
        Assert.True(attempt.OffRoute);
        Assert.False(attempt.OnGrab(route, new Vector3(5, 5, 1)));
        Assert.Equal(AttemptState.Climbing, attempt.State);

        Step(attempt, route, 20, new Vector3(0, 9.5f, 1), climbing: true);
        Assert.Equal(AttemptState.Finished, attempt.State);
        Assert.True(attempt.OffRoute);
    }

    [Fact]
    public void UnpaintedRockIsOffRoute()
    {
        var route = WallRoute();
        var attempt = new Attempt();
        Step(attempt, route, 0, Vector3.Zero, climbing: false);
        Step(attempt, route, 1, Vector3.Zero, climbing: true);

        Assert.True(attempt.OnGrab(route, new Vector3(4, 3, 1)));
    }

    [Fact]
    public void GrabsBeforeTheClockStartsDoNotCount()
    {
        var route = WallRoute();
        var attempt = new Attempt();

        Assert.False(attempt.OnGrab(route, new Vector3(4, 3, 1)));
        Step(attempt, route, 0, Vector3.Zero, climbing: false);
        Assert.False(attempt.OnGrab(route, new Vector3(4, 3, 1)));
        Assert.False(attempt.OffRoute);
    }

    [Fact]
    public void SteppingBackDownWhereTheClimbStartedResetsForAnotherGo()
    {
        var route = WallRoute();
        var attempt = new Attempt();
        Step(attempt, route, 0, Vector3.Zero, climbing: false);
        Step(attempt, route, 1, Vector3.Zero, climbing: true);
        attempt.OnGrab(route, new Vector3(4, 3, 1));

        Step(attempt, route, 4, new Vector3(1, 0, 0), climbing: false);
        Assert.Equal(AttemptState.Ready, attempt.State);
        Assert.False(attempt.OffRoute);

        Step(attempt, route, 6, new Vector3(1, 0.5f, 0.5f), climbing: true);
        Assert.Equal(AttemptState.Climbing, attempt.State);
        Assert.Equal(1, attempt.Elapsed(7));
    }

    [Fact]
    public void StartsWhereverSheLeavesTheGround()
    {
        // A second line, well away from the start marker, or a route without one.
        foreach (var hasStart in new[] { true, false })
        {
            var route = WallRoute();
            if (!hasStart)
            {
                route.Start = null;
            }

            var attempt = new Attempt();
            var secondLine = new Vector3(20, 0, 0);
            Step(attempt, route, 0, secondLine, climbing: false);
            Assert.Equal(AttemptState.Ready, attempt.State);

            Step(attempt, route, 1, secondLine, climbing: true);
            Assert.Equal(AttemptState.Climbing, attempt.State);

            // Standing at the start marker isn't where this attempt began, so it's a ledge.
            Step(attempt, route, 5, Vector3.Zero, climbing: false);
            Assert.Equal(AttemptState.Climbing, attempt.State);
            Step(attempt, route, 6, new Vector3(20.5f, 0, 0), climbing: false);
            Assert.Equal(AttemptState.Ready, attempt.State);
        }
    }

    [Fact]
    public void AlreadyOnTheRockIsNotAStart()
    {
        var route = WallRoute();
        var attempt = new Attempt();

        Step(attempt, route, 0, new Vector3(0, 3, 0.5f), climbing: true);
        Step(attempt, route, 1, new Vector3(0, 4, 0.5f), climbing: true);

        Assert.Equal(AttemptState.Idle, attempt.State);
    }

    [Fact]
    public void ToppingOutAwayFromTheStartKeepsTheClockRunning()
    {
        var route = WallRoute();
        route.Finish = null;
        var attempt = new Attempt();
        Step(attempt, route, 0, Vector3.Zero, climbing: false);
        Step(attempt, route, 1, Vector3.Zero, climbing: true);

        Step(attempt, route, 5, new Vector3(0, 10, 3), climbing: false);

        Assert.Equal(AttemptState.Climbing, attempt.State);
    }

    [Fact]
    public void AFinishedTimeStaysUpUntilTheNextStart()
    {
        var route = WallRoute();
        var attempt = new Attempt();
        Step(attempt, route, 0, Vector3.Zero, climbing: false);
        Step(attempt, route, 1, Vector3.Zero, climbing: true);
        attempt.OnGrab(route, new Vector3(0, 5, 1));
        Step(attempt, route, 30, new Vector3(0, 9.5f, 1), climbing: true);

        // Walking away from the top and back down to the bottom.
        Step(attempt, route, 40, new Vector3(0, 10, 6), climbing: false);
        Step(attempt, route, 60, new Vector3(3, 0, 0), climbing: false);
        Assert.Equal(AttemptState.Finished, attempt.State);
        Assert.Equal(29, attempt.Elapsed(100));

        Step(attempt, route, 61, new Vector3(3, 0.5f, 0.5f), climbing: true);
        Assert.Equal(AttemptState.Climbing, attempt.State);
        Assert.False(attempt.OffRoute);
        Assert.Equal(4, attempt.Elapsed(65));
    }

    [Fact]
    public void ClimbingFromBesideTheFinishIsNotAStart()
    {
        var route = WallRoute();
        var attempt = new Attempt();
        Step(attempt, route, 0, Vector3.Zero, climbing: false);
        Step(attempt, route, 1, Vector3.Zero, climbing: true);
        Step(attempt, route, 30, new Vector3(0, 9.5f, 1), climbing: true);

        // Stepping off the top just past the finish, then back onto the wall and into it.
        Step(attempt, route, 31, new Vector3(0, 10, 3.5f), climbing: false);
        Step(attempt, route, 32, new Vector3(0, 9, 1.5f), climbing: true);
        Step(attempt, route, 33, new Vector3(0, 9.5f, 1), climbing: true);

        Assert.Equal(AttemptState.Finished, attempt.State);
        Assert.Equal(29, attempt.Elapsed(100));

        var fresh = new Attempt();
        Step(fresh, route, 0, new Vector3(0, 10, 3.5f), climbing: false);
        Assert.Equal(AttemptState.Idle, fresh.State);
        Step(fresh, route, 1, new Vector3(0, 9, 1.5f), climbing: true);
        Assert.Equal(AttemptState.Idle, fresh.State);
    }

    [Fact]
    public void FinishCountsAnyPartOfTheBody()
    {
        var finish = new FinishZone { Position = new Vector3(0, 2, 0), Radius = 0.5f };

        Assert.True(finish.Contains(Vector3.Zero, new Vector3(0, 1.6f, 0)));
        Assert.False(finish.Contains(Vector3.Zero, new Vector3(0, 1.4f, 0)));
        Assert.False(finish.Contains(new Vector3(1, 0, 0), new Vector3(1, 1.6f, 0)));
        Assert.True(finish.Contains(new Vector3(1, 0, 0), new Vector3(1, 1.6f, 0), margin: 1f));
    }

    [Theory]
    [InlineData(0, "0:00.00")]
    [InlineData(5.25, "0:05.25")]
    [InlineData(83.456, "1:23.45")]
    [InlineData(3725.5, "1:02:05.50")]
    public void FormatsTimes(double seconds, string expected)
    {
        Assert.Equal(expected, Attempt.FormatTime(seconds));
    }
}
