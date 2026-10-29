using System;
using System.IO;
using CairnRouteMaker.Editor;
using CairnRouteMaker.Game;
using CairnRouteMaker.Routes;
using MelonLoader;
using UnityEngine;

namespace CairnRouteMaker.Climbing;

/// <summary>Whether the hold the moving limb would grab next is on-route.</summary>
internal enum NextGrabStatus
{
    /// <summary>Ava isn't climbing, so there's nothing to show.</summary>
    Hidden,

    /// <summary>Climbing, but no limb is moving towards a hold.</summary>
    None,
    Valid,
    Invalid,
}

/// <summary>
/// Feeds the game's state into an <see cref="Attempt"/> each frame: where Ava is, whether she's
/// climbing, and each hand or foot that has just attached. Only attachments count against the
/// route, not limbs swinging across paint, so this watches for limbs starting to hold. It also
/// checks the hold the moving limb is heading for, so the player can see a bad grab coming.
/// Finishing without going off-route keeps the time if it's the route's personal best.
/// </summary>
internal sealed class AttemptTracker
{
    /// <summary>How far up from Ava's root the finish check reaches, roughly her height.</summary>
    private const float BodyHeight = 1.6f;

    /// <summary>
    /// A held limb whose anchor jumps this far in one frame has let go and grabbed again within
    /// the frame (mantles, dynos), so that counts as a new attachment.
    /// </summary>
    private const float RegrabDistance = 0.25f;

    private readonly GameApi.LimbGrip[] _grips = new GameApi.LimbGrip[4];
    private readonly GameApi.LimbGrip[] _previous = new GameApi.LimbGrip[4];

    /// <summary>The last predicted grab for each limb, compared with where it lands in the log.</summary>
    private readonly GameApi.NextGrab?[] _predicted = new GameApi.NextGrab?[4];
    private readonly PersonalBests _bests;
    private Route _route;
    private bool _broken;

    public AttemptTracker(PersonalBests bests) => _bests = bests;

    public Attempt Attempt { get; } = new();

    /// <summary>The route being attempted, or null.</summary>
    public Route Route => _route;

    /// <summary>The route's best time in seconds, or null if it hasn't been finished on-route yet.</summary>
    public double? Best => _bests.Get(_route?.Id);

    /// <summary>The attempt just finished set the route's best time.</summary>
    public bool NewBest { get; private set; }

    public NextGrabStatus NextGrab { get; private set; }

    public void Reset()
    {
        Attempt.Reset();
        NewBest = false;
        NextGrab = NextGrabStatus.Hidden;
        Array.Clear(_previous);
        Array.Clear(_predicted);
    }

    public void Update(Route route)
    {
        if (route != _route)
        {
            _route = route;
            Reset();
        }

        if (route == null || _broken || !GameApi.IsAvaReady || GameApi.AvaPosition is not { } position)
        {
            NextGrab = NextGrabStatus.Hidden;
            return;
        }

        try
        {
            Track(route, position);
        }
        catch (Exception e)
        {
            // Don't log the same failure every frame; the editor still works without timing.
            MelonLogger.Error($"Route timing stopped after an error: {e}");
            _broken = true;
            Reset();
        }
    }

    private void Track(Route route, Vector3 position)
    {
        var now = Time.timeAsDouble;
        var climbing = GameApi.TryReadGrips(_grips);
        if (!climbing)
        {
            Array.Clear(_grips);
            Array.Clear(_predicted);
        }

        UpdateNextGrab(route, climbing);

        // Grabs only count while the clock runs, so check them before the finish can stop it this
        // frame, or after the clock starts if it wasn't running.
        var wasClimbing = Attempt.State == AttemptState.Climbing;
        if (wasClimbing)
        {
            CheckGrabs(route, now);
        }

        var root = RouteOverlay.ToNumerics(position);
        var top = root + System.Numerics.Vector3.UnitY * BodyHeight;
        if (Attempt.Update(route, now, root, top, climbing))
        {
            NewBest = Attempt.State == AttemptState.Finished && RecordBest(route, Attempt.Elapsed(now));
            LogState(now);
        }

        if (!wasClimbing)
        {
            CheckGrabs(route, now);
        }
    }

    /// <summary>Check each hand or foot that has just attached against the route.</summary>
    private void CheckGrabs(Route route, double now)
    {
        for (var i = 0; i < _grips.Length; i++)
        {
            var grip = _grips[i];
            var previous = _previous[i];
            _previous[i] = grip;
            if (!grip.Holding || (previous.Holding && (grip.Anchor - previous.Anchor).sqrMagnitude < RegrabDistance * RegrabDistance))
            {
                continue;
            }

            // Stepping onto the wall, the game plants her feet for her, often on the ground.
            if (grip.IsFoot && grip.PlacedFromGround)
            {
                continue;
            }

            LogPrediction(i, grip);
            if (Attempt.OnGrab(route, RouteOverlay.ToNumerics(grip.Anchor)))
            {
                MelonLogger.Msg($"Failed to stay on route: {(grip.IsFoot ? "foot" : "hand")} attached off-route at {grip.Anchor} after {Attempt.FormatTime(Attempt.Elapsed(now))}.");
            }
        }
    }

    /// <summary>Keep the time of a finish that stayed on-route if it beats the route's best.</summary>
    /// <returns>True if it's the new best.</returns>
    private bool RecordBest(Route route, double seconds)
    {
        if (Attempt.OffRoute)
        {
            return false;
        }

        try
        {
            return _bests.Record(route.Id, seconds, DateTime.Now);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not a reason to stop timing.
            MelonLogger.Error($"Couldn't save the personal best: {e.Message}");
            return false;
        }
    }

    private void UpdateNextGrab(Route route, bool climbing)
    {
        if (!climbing)
        {
            NextGrab = NextGrabStatus.Hidden;
            return;
        }

        if (!GameApi.TryReadNextGrab(out var next))
        {
            NextGrab = NextGrabStatus.None;
            return;
        }

        _predicted[next.Limb] = next;
        NextGrab = route.IsAllowed(RouteOverlay.ToNumerics(next.Point)) ? NextGrabStatus.Valid : NextGrabStatus.Invalid;
    }

    /// <summary>
    /// Log how far a landed grab was from the last prediction for that limb, so play-testing can
    /// show whether the indicator can be trusted.
    /// </summary>
    private void LogPrediction(int limb, GameApi.LimbGrip grip)
    {
        if (_predicted[limb] is not { } predicted)
        {
            return;
        }

        _predicted[limb] = null;
        var miss = Vector3.Distance(predicted.Point, grip.Anchor);
        MelonLogger.Msg($"Grab landed {miss:0.00} m from the predicted spot ({(predicted.FromGame ? "game's next best grab" : "closest rock")}).");
    }

    private void LogState(double now)
    {
        switch (Attempt.State)
        {
            case AttemptState.Ready:
                MelonLogger.Msg("Ready: the timer starts when Ava starts climbing.");
                break;
            case AttemptState.Climbing:
                MelonLogger.Msg("Route attempt started.");
                break;
            case AttemptState.Finished:
                var time = Attempt.FormatTime(Attempt.Elapsed(now));
                MelonLogger.Msg(Attempt.OffRoute ? $"Reached the finish in {time}, but failed to stay on route."
                    : NewBest ? $"Route complete in {time}! New personal best."
                    : $"Route complete in {time}! Personal best: {Attempt.FormatTime(Best ?? 0)}.");
                break;
        }
    }
}
