using System;
using System.Numerics;

namespace CairnRouteMaker.Routes;

public enum AttemptState
{
    /// <summary>Not timing: she's on the rock, or on her feet at the finish, outside an attempt.</summary>
    Idle,

    /// <summary>
    /// On her feet, where the clock starts when she starts climbing; or teleported onto the wall,
    /// where it starts when she first moves a hand or foot.
    /// </summary>
    Ready,

    /// <summary>The clock is running.</summary>
    Climbing,

    /// <summary>She reached the finish; the time is final.</summary>
    Finished,
}

/// <summary>
/// One timed go at a route. Leaving the ground to climb starts the clock, wherever she is, so a
/// route can have several lines with their own starts. Reaching the finish stops it. Standing on
/// a ledge partway up doesn't. Attaching a hand or foot anywhere that isn't on-route marks the
/// attempt as off-route, but the clock keeps going so the climb can still be finished. Stepping
/// back down where the attempt left the ground resets it for another go, and so does climbing
/// from the ground again after a finish. A teleport (to a route start) is a fresh go too. One that
/// lands her on the wall has her climbing already, so there her first move starts the clock.
/// </summary>
public sealed class Attempt
{
    /// <summary>Back on her feet this close to where she started climbing, she's starting over.</summary>
    public const float RestartRadius = 1.5f;

    /// <summary>
    /// Climbing from the ground this close to the finish sphere doesn't start the clock, or stepping
    /// off at the top and climbing back in would be a run of a second or two.
    /// </summary>
    public const float FinishMargin = 2f;

    /// <summary>Teleported onto a wall, she has landed once she's climbing this close to where she was sent.</summary>
    public const float ArrivalRadius = 1.5f;

    /// <summary>Seconds a teleport onto a wall may take to land before the attempt stops waiting for it.</summary>
    public const double ArrivalTimeout = 10;

    private double _startedAt;
    private double _endedAt;

    /// <summary>Where she left the ground for this attempt.</summary>
    private Vector3 _climbedFrom;

    /// <summary>Last frame she was on her feet away from the finish, so climbing now is a start.</summary>
    private bool _wasOnGround;

    /// <summary>Where she last stood away from the finish.</summary>
    private Vector3 _lastOnGround;

    /// <summary>Where a teleport onto the wall is taking her, until she lands or it times out.</summary>
    private (Vector3 Position, double Deadline)? _arrival;

    public AttemptState State { get; private set; }

    /// <summary>Teleported onto the wall and waiting there: the clock starts on her first move.</summary>
    public bool ArmedOnWall { get; private set; }

    /// <summary>A teleport onto the wall hasn't landed yet.</summary>
    public bool Arriving => _arrival != null;

    /// <summary>A hand or foot was attached outside the allowed paint during this attempt.</summary>
    public bool OffRoute { get; private set; }

    /// <summary>Seconds on the clock: running while climbing, frozen once finished.</summary>
    public double Elapsed(double now) => State switch
    {
        AttemptState.Climbing => now - _startedAt,
        AttemptState.Finished => _endedAt - _startedAt,
        _ => 0,
    };

    public void Reset()
    {
        State = AttemptState.Idle;
        OffRoute = false;
        ArmedOnWall = false;
        _wasOnGround = false;
        _arrival = null;
    }

    /// <summary>
    /// Ava was just sent to <paramref name="position"/>, which starts a fresh go. Sent onto a wall, she
    /// lands climbing, so the attempt waits there for her first move. Nothing counts until she has
    /// landed: the warp takes a loading screen, and getting there isn't climbing the route.
    /// </summary>
    public void Teleported(Vector3 position, bool onWall, double now)
    {
        Reset();
        if (onWall)
        {
            _arrival = (position, now + ArrivalTimeout);
        }
    }

    /// <summary>
    /// Advance with where Ava is this frame. <paramref name="top"/> is a point up her body from
    /// <paramref name="position"/>, used for the finish check. <paramref name="reaching"/> is
    /// whether she's moving a hand or foot towards a new hold.
    /// </summary>
    /// <returns>True on the frame the attempt changes state.</returns>
    public bool Update(Route route, double now, Vector3 position, Vector3 top, bool climbing, bool reaching)
    {
        var previous = State;
        var startsClimbing = climbing && _wasOnGround;
        var leftFrom = _lastOnGround;
        _wasOnGround = !climbing && !(route.Finish?.Contains(position, top, FinishMargin) ?? false);
        if (_wasOnGround)
        {
            _lastOnGround = position;
        }
        if (_arrival is { } arrival)
        {
            // A reach she was making when she was sent isn't her first move, so wait for it to end.
            if (climbing && !reaching && Vector3.Distance(position, arrival.Position) <= ArrivalRadius)
            {
                _arrival = null;
                ArmedOnWall = true;
                State = AttemptState.Ready;
            }
            else if (now > arrival.Deadline)
            {
                _arrival = null;
            }
        }
        else if (ArmedOnWall && climbing)
        {
            if (reaching)
            {
                StartClock(now, position);
            }
        }
        else if (State == AttemptState.Climbing)
        {
            if (route.Finish != null && route.Finish.Contains(position, top))
            {
                State = AttemptState.Finished;
                _endedAt = now;
            }
            else if (!climbing && Vector3.Distance(position, _climbedFrom) <= RestartRadius)
            {
                OffRoute = false;
                State = AttemptState.Ready;
            }
        }
        else if (startsClimbing)
        {
            StartClock(now, leftFrom);
        }
        else if (State != AttemptState.Finished)
        {
            // A finished time stays up until the next attempt starts. Off the wall before her
            // first move, she's back to starting from the ground.
            ArmedOnWall = false;
            State = _wasOnGround ? AttemptState.Ready : AttemptState.Idle;
        }

        return State != previous;
    }

    private void StartClock(double now, Vector3 from)
    {
        OffRoute = false;
        ArmedOnWall = false;
        State = AttemptState.Climbing;
        _startedAt = now;
        _climbedFrom = from;
    }

    /// <summary>A hand or foot just attached at <paramref name="point"/>.</summary>
    /// <returns>True if this attachment is what took the attempt off-route.</returns>
    public bool OnGrab(Route route, Vector3 point)
    {
        if (State != AttemptState.Climbing || OffRoute || route.IsAllowed(point))
        {
            return false;
        }

        OffRoute = true;
        return true;
    }

    /// <summary>"m:ss.ff", or "h:mm:ss.ff" past an hour.</summary>
    public static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        var hundredths = time.Milliseconds / 10;
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}.{hundredths:00}"
            : $"{time.Minutes}:{time.Seconds:00}.{hundredths:00}";
    }
}
