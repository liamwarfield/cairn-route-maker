using System;
using CairnRouteMaker.Editor;
using CairnRouteMaker.Game;
using CairnRouteMaker.Routes;
using Il2Cpp;
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

    /// <summary>How often a running free climb renews its claim on its mountain's summit save.</summary>
    private const float ClaimSeconds = 60f;

    private readonly Traveler _traveler;
    private Route _pendingRoute;
    private bool _launched;
    private float _launchedAt;
    private bool _reportedSlowLaunch;
    private float _nextCheck;
    private StreamingTweakables.World? _launchedWorld;
    /// <summary>The mountain whose summit slot this session saves to: the one it started on, even after a trip.</summary>
    private StreamingTweakables.World? _saveWorld;
    private float _nextClaim;

    public FreeClimbSession(Traveler traveler)
    {
        _traveler = traveler;
        MainMenuPatches.MainMenuShown += End;
        IceGearPatches.KeepIceGear = () => IsActive;
        ClimbingModePatches.InFreeClimb = () => IsActive;
    }

    public bool IsActive { get; private set; }

    /// <summary>A session began, with the route chosen in the launcher, or null for none.</summary>
    public event Action<Route> Started;

    public event Action Ended;

    /// <summary>A game was launched from the menu; with a route, take Ava to its start once she's in.</summary>
    public void OnLaunched(Route route, StreamingTweakables.World world)
    {
        _launched = true;
        _launchedAt = Time.unscaledTime;
        _reportedSlowLaunch = false;
        _pendingRoute = route;
        _launchedWorld = world;
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
                return;
            }

            // A reload (New run, a retry) brings back the setup the game saved or launched.
            ClimbingModePatches.Apply();
            if (Time.unscaledTime >= _nextClaim)
            {
                ClaimSave();
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
            if (GameApi.IsPlayersSummitGame(out var world))
            {
                FreeClimbSaves.Release(world);
            }

            if (_launched)
            {
                MelonLogger.Warning($"Free climb was launched but the running game has other rules ({GameApi.DescribeGameSetup()}); the mod stays off.");
                _launched = false;
                _pendingRoute = null;
                _launchedWorld = null;
            }

            return;
        }

        Start();
    }

    private void Start()
    {
        IsActive = true;
        ClimbingModePatches.Apply();
        // A free-climb save continued from the game's own menu has no launch of ours.
        _saveWorld = _launchedWorld ?? GameApi.CurrentWorld;
        _launchedWorld = null;
        ClaimSave();
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
            _traveler.StartToRoute(route, message => MelonLogger.Msg(message));
        }
    }

    private void End()
    {
        _launched = false;
        _pendingRoute = null;
        _launchedWorld = null;
        if (!IsActive)
        {
            return;
        }

        // Quitting saves just before this.
        ClaimSave();
        IsActive = false;
        ClimbingModePatches.Restore();
        MelonLogger.Msg("Free climb ended.");
        Ended?.Invoke();
    }

    /// <summary>Note that the summit save on this session's mountain is a free climb (see <see cref="FreeClimbSaves"/>).</summary>
    private void ClaimSave()
    {
        _nextClaim = Time.unscaledTime + ClaimSeconds;
        if (_saveWorld is { } world)
        {
            FreeClimbSaves.Claim(world);
        }
    }
}
