using System;
using CairnRouteMaker.Game;
using CairnRouteMaker.Routes;
using Il2Cpp;
using UnityEngine;

namespace CairnRouteMaker.Editor;

/// <summary>
/// Gets Ava to a world, zone and optionally a route's start, one loading screen at a time:
/// travel to the world if needed, then to the zone, then teleport once she's standing and set
/// her up in the route's loadout. Each step waits for the game to finish streaming before the
/// next one starts.
/// </summary>
internal sealed class Traveler
{
    private const float StepTimeoutSeconds = 90f;

    /// <summary>After a trip is requested, how long the world may look idle before we call it failed.</summary>
    private const float IdleGraceSeconds = 5f;

    /// <summary>
    /// After a zone trip, how long Ava must stay in the zone before it counts. The game first drops
    /// her in 01_FirstRidge for a moment, then puts her at the spawn point.
    /// </summary>
    private const float SettleSeconds = 3f;

    private StreamingTweakables.World _world;
    private string _zone;
    private Vector3? _position;
    private float _yaw;
    private Loadout _loadout;
    private Action<string> _report;
    private string _issued;
    private float _stepStarted;
    private float _idleSince = -1f;
    private float _settledSince = -1f;

    public bool IsActive { get; private set; }

    public string Destination => _zone != null ? ZoneNames.Pretty(_zone) : ZoneNames.PrettyWorld(_world.ToString());

    /// <summary>Go to a world, and optionally a zone in it, leaving Ava wherever the game puts her.</summary>
    public void Start(StreamingTweakables.World world, string zone, Action<string> report) => Begin(world, zone, null, 0f, null, report);

    private void Begin(StreamingTweakables.World world, string zone, Vector3? position, float yaw, Loadout loadout, Action<string> report)
    {
        _world = world;
        _zone = zone;
        _position = position;
        _yaw = yaw;
        _loadout = loadout;
        _report = report;
        _issued = null;
        _stepStarted = Time.unscaledTime;
        _idleSince = -1f;
        _settledSince = -1f;
        IsActive = true;
    }

    /// <summary>Go to a route's zone, and onto its start in the route's loadout if it has a start.</summary>
    /// <returns>False, without starting, if the route's zone key doesn't name a known world.</returns>
    public bool StartToRoute(Route route, Action<string> report)
    {
        if (!GameApi.TryParseZoneKey(route.ZoneKey, out var world, out var zone))
        {
            return false;
        }

        var start = route.Start;
        Begin(world, zone, start == null ? null : RouteOverlay.ToUnity(start.Position), start?.YawDegrees ?? 0f, route.Loadout, report);
        return true;
    }

    public void Cancel() => IsActive = false;

    public void Update()
    {
        if (!IsActive)
        {
            return;
        }

        if (Time.unscaledTime - _stepStarted > StepTimeoutSeconds)
        {
            Finish($"Gave up travelling to {Destination}: it took too long.");
            return;
        }

        if (!GameApi.CanMoveAva(out _))
        {
            // Loading, streaming, or Ava not standing yet: wait.
            _idleSince = -1f;
            _settledSince = -1f;
            return;
        }

        if (GameApi.CurrentWorld != _world)
        {
            Step("world", () => GameApi.TryTravelToWorld(_world, out var reason) ? null : reason);
            return;
        }

        if (_zone != null && CurrentZoneName() != _zone)
        {
            _settledSince = -1f;
            Step("zone", () => GameApi.TryTravelToZone(_zone, out var reason) ? null : reason);
            return;
        }

        if (_issued == "zone")
        {
            if (_settledSince < 0f)
            {
                _settledSince = Time.unscaledTime;
            }

            if (Time.unscaledTime - _settledSince < SettleSeconds)
            {
                return;
            }
        }

        if (_position != null)
        {
            // A trip to an exact spot is a route start, so Ava arrives set up for the route.
            if (GameApi.TryTeleportAva(_position.Value, _yaw, out var reason))
            {
                AvaLoadout.Apply(_loadout);
                Finish($"Arrived at {Destination}.");
            }
            else
            {
                Finish($"Reached {Destination} but couldn't place Ava: {reason}");
            }

            return;
        }

        Finish($"Arrived at {Destination}.");
    }

    /// <summary>Request one leg of the trip, once; if it never takes effect, stop.</summary>
    private void Step(string leg, Func<string> request)
    {
        if (_issued != leg)
        {
            var error = request();
            if (error != null)
            {
                Finish($"Couldn't travel to {Destination}: {error}");
                return;
            }

            _issued = leg;
            _stepStarted = Time.unscaledTime;
            _idleSince = -1f;
            return;
        }

        // Requested, the world is idle, and we're still not there.
        if (_idleSince < 0f)
        {
            _idleSince = Time.unscaledTime;
        }
        else if (Time.unscaledTime - _idleSince > IdleGraceSeconds)
        {
            Finish($"The game didn't take Ava to {Destination}.");
        }
    }

    private static string CurrentZoneName()
    {
        var key = GameApi.CurrentZoneKey;
        return key?.Substring(key.IndexOf('/') + 1);
    }

    private void Finish(string message)
    {
        IsActive = false;
        _report?.Invoke(message);
    }
}
