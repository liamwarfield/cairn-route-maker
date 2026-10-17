using System;
using CairnRouteMaker.Game;
using Il2Cpp;
using UnityEngine;

namespace CairnRouteMaker.Editor;

/// <summary>
/// Gets Ava to a world, zone and optionally an exact spot, one loading screen at a time:
/// travel to the world if needed, then to the zone, then teleport once she's standing.
/// Each step waits for the game to finish streaming before the next one starts.
/// </summary>
internal sealed class Traveler
{
    private const float StepTimeoutSeconds = 90f;

    /// <summary>After a trip is requested, how long the world may look idle before we call it failed.</summary>
    private const float IdleGraceSeconds = 5f;

    private StreamingTweakables.World _world;
    private string _zone;
    private Vector3? _position;
    private float _yaw;
    private Action<string> _report;
    private string _issued;
    private float _stepStarted;
    private float _idleSince = -1f;

    public bool IsActive { get; private set; }

    public string Destination => _zone ?? _world.ToString();

    /// <summary>Go to a world, optionally a zone in it, optionally an exact standing spot there.</summary>
    public void Start(StreamingTweakables.World world, string zone, Vector3? position, float yaw, Action<string> report)
    {
        _world = world;
        _zone = zone;
        _position = position;
        _yaw = yaw;
        _report = report;
        _issued = null;
        _stepStarted = Time.unscaledTime;
        _idleSince = -1f;
        IsActive = true;
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
            return;
        }

        if (GameApi.CurrentWorld != _world)
        {
            Step("world", () => GameApi.TryTravelToWorld(_world, out var reason) ? null : reason);
            return;
        }

        if (_zone != null && CurrentZoneName() != _zone)
        {
            Step("zone", () => GameApi.TryTravelToZone(_zone, out var reason) ? null : reason);
            return;
        }

        if (_position != null)
        {
            Finish(GameApi.TryTeleportAva(_position.Value, _yaw, out var reason)
                ? $"Arrived at {Destination}."
                : $"Reached {Destination} but couldn't place Ava: {reason}");
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
