using System;
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
    private Route _route;
    private bool _broken;

    public Attempt Attempt { get; } = new();

    /// <summary>The route being attempted, or null.</summary>
    public Route Route => _route;

    public NextGrabStatus NextGrab { get; private set; }

    public void Reset()
    {
        Attempt.Reset();
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

        var root = RouteOverlay.ToNumerics(position);
        var top = root + System.Numerics.Vector3.UnitY * BodyHeight;
        if (Attempt.Update(route, now, root, top, climbing))
        {
            LogState(now);
        }

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
                MelonLogger.Msg("At the route start. The timer starts when Ava starts climbing.");
                break;
            case AttemptState.Climbing:
                MelonLogger.Msg("Route attempt started.");
                break;
            case AttemptState.Finished:
                var time = Attempt.FormatTime(Attempt.Elapsed(now));
                MelonLogger.Msg(Attempt.OffRoute
                    ? $"Reached the finish in {time}, but failed to stay on route."
                    : $"Route complete in {time}!");
                break;
        }
    }
}
