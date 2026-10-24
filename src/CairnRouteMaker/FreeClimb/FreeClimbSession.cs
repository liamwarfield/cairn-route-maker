using System;
using CairnRouteMaker.Editor;
using CairnRouteMaker.Game;
using CairnRouteMaker.Routes;
using MelonLoader;
using UnityEngine;

namespace CairnRouteMaker.FreeClimb;

/// <summary>
/// Whether we're in a free-climb game, the only place the mod's tools (editor, free camera,
/// teleports, route overlay) work. Read from the game itself rather than remembered from the
/// launch: a session starts once Ava is in a game whose setup has the free-climb rules, and ends
/// when the main menu comes back. So a launch that didn't take, a story game or the player's own
/// Summit run never switch the tools on.
/// </summary>
internal sealed class FreeClimbSession
{
    private const float CheckSeconds = 0.5f;

    /// <summary>A launch that hasn't produced Ava after this long gets its world state logged.</summary>
    private const float SlowLaunchSeconds = 20f;

    private readonly Traveler _traveler;
    private Route _pendingRoute;
    private bool _launched;
    private float _launchedAt;
    private bool _reportedSlowLaunch;
    private float _nextCheck;

    public FreeClimbSession(Traveler traveler)
    {
        _traveler = traveler;
        MainMenuPatches.MainMenuShown += End;
        IceGearPatches.KeepIceGear = () => IsActive;
    }

    public bool IsActive { get; private set; }

    /// <summary>A session began, with the route chosen in the launcher, or null for none.</summary>
    public event Action<Route> Started;

    public event Action Ended;

    /// <summary>A game was launched from the menu; with a route, take Ava to its start once she's in.</summary>
    public void OnLaunched(Route route)
    {
        _launched = true;
        _launchedAt = Time.unscaledTime;
        _reportedSlowLaunch = false;
        _pendingRoute = route;
    }

    public void Update()
    {
        if (Time.unscaledTime < _nextCheck)
        {
            return;
        }

        _nextCheck = Time.unscaledTime + CheckSeconds;
        if (IsActive)
        {
            if (!GameApi.IsFreeClimbGame)
            {
                End();
            }

            return;
        }

        if (!GameApi.IsAvaReady)
        {
            if (_launched && !_reportedSlowLaunch && Time.unscaledTime - _launchedAt > SlowLaunchSeconds)
            {
                _reportedSlowLaunch = true;
                MelonLogger.Warning($"Free climb was launched {SlowLaunchSeconds:0} s ago and Ava still isn't in the world:\n{GameApi.DescribeWorldState()}");
            }

            return;
        }

        if (!GameApi.IsFreeClimbGame)
        {
            if (_launched)
            {
                MelonLogger.Warning($"Free climb was launched but the running game has other rules ({GameApi.DescribeGameSetup()}); the mod stays off.");
                _launched = false;
                _pendingRoute = null;
            }

            return;
        }

        Start();
    }

    private void Start()
    {
        IsActive = true;
        _launched = false;
        GameApi.MarkRunAsCheating();
        MelonLogger.Msg($"Free climb started ({GameApi.DescribeGameSetup()}).");
        // A story-free game starts without them, and only the game's trigger volumes put them on.
        GameApi.EquipIceGear();
        var route = _pendingRoute;
        _pendingRoute = null;
        Started?.Invoke(route);
        if (route?.Start != null)
        {
            var parts = route.ZoneKey.Split('/', 2);
            if (parts.Length == 2 && Enum.TryParse<Il2Cpp.StreamingTweakables.World>(parts[0], out var world))
            {
                _traveler.Start(world, parts[1], RouteOverlay.ToUnity(route.Start.Position), route.Start.YawDegrees, message => MelonLogger.Msg(message));
            }
        }
    }

    private void End()
    {
        _launched = false;
        _pendingRoute = null;
        if (!IsActive)
        {
            return;
        }

        IsActive = false;
        MelonLogger.Msg("Free climb ended.");
        Ended?.Invoke();
    }
}
