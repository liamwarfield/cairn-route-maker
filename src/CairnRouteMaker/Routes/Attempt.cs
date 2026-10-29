using System;
using System.Numerics;

namespace CairnRouteMaker.Routes;

public enum AttemptState
{
    /// <summary>Not timing: she's on the rock, or on her feet at the finish, outside an attempt.</summary>
    Idle,

    /// <summary>On her feet; the clock starts when she starts climbing.</summary>
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
/// from the ground again after a finish.
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

    private double _startedAt;
    private double _endedAt;

    /// <summary>Where she left the ground for this attempt.</summary>
    private Vector3 _climbedFrom;

    /// <summary>Last frame she was on her feet away from the finish, so climbing now is a start.</summary>
    private bool _wasOnGround;

    /// <summary>Where she last stood away from the finish.</summary>
    private Vector3 _lastOnGround;

    public AttemptState State { get; private set; }

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
        _wasOnGround = false;
    }

    /// <summary>
    /// Advance with where Ava is this frame. <paramref name="top"/> is a point up her body from
    /// <paramref name="position"/>, used for the finish check.
    /// </summary>
    /// <returns>True on the frame the attempt changes state.</returns>
    public bool Update(Route route, double now, Vector3 position, Vector3 top, bool climbing)
    {
        var previous = State;
        var startsClimbing = climbing && _wasOnGround;
        var leftFrom = _lastOnGround;
        _wasOnGround = !climbing && !(route.Finish?.Contains(position, top, FinishMargin) ?? false);
        if (_wasOnGround)
        {
            _lastOnGround = position;
        }
        if (State == AttemptState.Climbing)
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
            OffRoute = false;
            State = AttemptState.Climbing;
            _startedAt = now;
            _climbedFrom = leftFrom;
        }
        else if (State != AttemptState.Finished)
        {
            // A finished time stays up until the next attempt starts.
            State = _wasOnGround ? AttemptState.Ready : AttemptState.Idle;
        }

        return State != previous;
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
