using System;
using System.Numerics;

namespace CairnRouteMaker.Routes;

public enum AttemptState
{
    /// <summary>Ava isn't at the start of the route.</summary>
    Idle,

    /// <summary>On her feet at the start marker; the clock starts when she starts climbing.</summary>
    Ready,

    /// <summary>The clock is running.</summary>
    Climbing,

    /// <summary>She reached the finish; the time is final.</summary>
    Finished,
}

/// <summary>
/// One timed go at a route. Standing at the start arms it, leaving the ground starts the clock,
/// and reaching the finish stops it. Attaching a hand or foot anywhere that isn't on-route marks
/// the attempt as off-route, but the clock keeps going so the climb can still be finished.
/// Coming back down to the start resets it for another go.
/// </summary>
public sealed class Attempt
{
    /// <summary>How close to the start marker Ava has to stand for an attempt to arm.</summary>
    public const float StartRadius = 1.5f;

    /// <summary>Walking this far from the start without climbing disarms the attempt.</summary>
    public const float LeaveRadius = 5f;

    private double _startedAt;
    private double _endedAt;

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
    }

    /// <summary>
    /// Advance with where Ava is this frame. <paramref name="top"/> is a point up her body from
    /// <paramref name="position"/>, used for the finish check.
    /// </summary>
    /// <returns>True on the frame the attempt changes state.</returns>
    public bool Update(Route route, double now, Vector3 position, Vector3 top, bool climbing)
    {
        var previous = State;
        if (route.Start == null)
        {
            Reset();
            return State != previous;
        }

        var startDistance = Vector3.Distance(position, route.Start.Position);
        if (!climbing && startDistance <= StartRadius)
        {
            // Standing at the start always means a fresh go, including stepping off mid-climb.
            if (State != AttemptState.Ready)
            {
                Reset();
                State = AttemptState.Ready;
            }
        }
        else if (State == AttemptState.Ready)
        {
            if (climbing)
            {
                State = AttemptState.Climbing;
                _startedAt = now;
            }
            else if (startDistance > LeaveRadius)
            {
                Reset();
            }
        }
        else if (State == AttemptState.Climbing && route.Finish != null && route.Finish.Contains(position, top))
        {
            State = AttemptState.Finished;
            _endedAt = now;
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
