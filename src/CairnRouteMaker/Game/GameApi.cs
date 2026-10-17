using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn;
using Il2CppTheGameBakers.Cairn.IO;
using MelonLoader;
using Unity.Cinemachine;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CairnRouteMaker.Game;

/// <summary>
/// The only place that touches Cairn's own types, so game updates break one file.
/// See docs/game-internals.md for where these names come from.
/// </summary>
internal static class GameApi
{
    // Layers 3 Ground, 6 ClimbingWall, 12 ClimbingHolds; used if Helpers isn't initialised yet.
    private const int FallbackSurfaceMask = (1 << 3) | (1 << 6) | (1 << 12);

    private static bool _savedDisableInputs;
    private static bool _inputBlocked;
    private static CinemachineBrain _disabledBrain;

    /// <summary>Rock, holds and ground: what the brush and start marker can land on.</summary>
    public static int SurfaceMask
    {
        get
        {
            try
            {
                var mask = Helpers.ClimbingWallAndHoldsAndGroundLayerMask;
                return mask != 0 ? mask : FallbackSurfaceMask;
            }
            catch (Exception)
            {
                return FallbackSurfaceMask;
            }
        }
    }

    /// <summary>The game's own debug-line shader, as a last resort for drawing the overlay.</summary>
    public static Shader LineMaterialShader()
    {
        try
        {
            var material = Helpers.GLLineMaterial;
            return material == null ? null : material.shader;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static PawnControllerSwitcher Switcher
    {
        get
        {
            var pawns = PawnManager.Instance;
            return pawns == null ? null : pawns.PawnControllerSwitcher;
        }
    }

    public static bool IsAvaReady => PawnManager.MCSpawned && Switcher != null;

    /// <summary>"World/Zone", or null outside the game world (menus, loading).</summary>
    public static string CurrentZoneKey
    {
        get
        {
            try
            {
                var streaming = StreamingManager.Instance;
                if (streaming == null || streaming.World == null || streaming.CurrentZone == null)
                {
                    return null;
                }

                return $"{streaming.World.WorldId}/{streaming.CurrentZone.name}";
            }
            catch (Exception)
            {
                // Singletons aren't up yet in menus and while loading.
                return null;
            }
        }
    }

    public static string GameVersion
    {
        get
        {
            try
            {
                return CairnVersion.ComputeFull();
            }
            catch (Exception)
            {
                return Application.version;
            }
        }
    }

    public readonly record struct WorldInfo(StreamingTweakables.World Id, bool IsCurrent, bool IsUnlocked);

    public readonly record struct ZoneInfo(string Name, bool IsCurrent, bool IsDiscovered);

    public static StreamingTweakables.World? CurrentWorld
    {
        get
        {
            try
            {
                var streaming = StreamingManager.Instance;
                return streaming == null || streaming.World == null ? null : streaming.World.WorldId;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>The playable mountains (not the test world), with whether the save has unlocked them.</summary>
    public static List<WorldInfo> Worlds()
    {
        var result = new List<WorldInfo>();
        var scenes = CairnSceneManager.Instance;
        var current = CurrentWorld;
        if (scenes == null)
        {
            return result;
        }

        foreach (var id in new[] { StreamingTweakables.World.Kami, StreamingTweakables.World.Lakpa, StreamingTweakables.World.Tenzing, StreamingTweakables.World.Apa })
        {
            if (scenes.TryGetWorld(id, out var data) && data != null)
            {
                result.Add(new WorldInfo(id, id == current, data.IsUnlocked));
            }
        }

        return result;
    }

    /// <summary>Zones of the world Ava is in, in the game's own order.</summary>
    public static List<ZoneInfo> ZonesInCurrentWorld()
    {
        var result = new List<ZoneInfo>();
        var streaming = StreamingManager.Instance;
        var scenes = CairnSceneManager.Instance;
        if (streaming == null || streaming.World == null || scenes == null)
        {
            return result;
        }

        var discovered = new HashSet<string>();
        var discoveredList = scenes.discoveredZones;
        if (discoveredList != null)
        {
            foreach (var zone in discoveredList)
            {
                discovered.Add(zone.name);
            }
        }

        var currentName = streaming.CurrentZone == null ? null : streaming.CurrentZone.name;
        foreach (var zone in streaming.World.zones)
        {
            result.Add(new ZoneInfo(zone.name, zone.name == currentName, discovered.Contains(zone.name)));
        }

        return result;
    }

    /// <summary>Loading, streaming or travelling: not a moment to start another trip.</summary>
    public static bool IsWorldBusy
    {
        get
        {
            try
            {
                var scenes = CairnSceneManager.Instance;
                var streaming = StreamingManager.Instance;
                return scenes == null || streaming == null || scenes.IsBusy() || scenes.IsTraveling || streaming.PreloadingZone != null;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }

    /// <summary>Whether Ava is in the world and nothing is loading: safe to start a trip or warp.</summary>
    public static bool CanMoveAva(out string reason)
    {
        if (!IsAvaReady || Switcher == null)
        {
            reason = "Ava isn't in the world yet.";
            return false;
        }

        if (IsWorldBusy)
        {
            reason = "The world is still loading.";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>Load another zone of the current world; the game shows a loading screen and drops Ava at its spawn.</summary>
    public static bool TryTravelToZone(string zoneName, out string reason)
    {
        if (!CanMoveAva(out reason))
        {
            return false;
        }

        var streaming = StreamingManager.Instance;
        foreach (var zone in streaming.World.zones)
        {
            if (zone.name == zoneName)
            {
                CairnSceneManager.Instance.TravelToZone(zone, streaming.World);
                return true;
            }
        }

        reason = $"No zone called {zoneName} in {streaming.World.WorldId}.";
        return false;
    }

    public static bool TryTravelToWorld(StreamingTweakables.World world, out string reason)
    {
        if (!CanMoveAva(out reason))
        {
            return false;
        }

        CairnSceneManager.Instance.TravelToWorld(world, false);
        return true;
    }

    /// <summary>
    /// Stop the game reacting to keyboard, mouse and gamepad while the editor owns them.
    /// We still read devices directly through Keyboard.current / Mouse.current.
    /// Uses DisableInputs (what the game does while teleporting out of no-go areas), not
    /// SetIgnoreInputEvents: that one is for key rebinding and swallows device events entirely,
    /// so our own reads would go dead too.
    /// </summary>
    public static void BlockGameInput(bool block)
    {
        var input = InputManager.Instance;
        if (input == null || block == _inputBlocked)
        {
            return;
        }

        if (block)
        {
            _savedDisableInputs = input.disableInputs;
            input.DisableInputs();
            input.ShowAndFreeMouse();
        }
        else
        {
            input.EnableInputs();
            input.disableInputs = _savedDisableInputs;
            input.UpdateInputContext();
            input.HideAndLockMouse();
        }

        _inputBlocked = block;
    }

    /// <summary>
    /// Detach the main camera from Cinemachine so we can fly it ourselves. Re-enabling the brain
    /// hands control back and the gameplay camera blends back to Ava.
    /// </summary>
    public static Camera TakeCamera()
    {
        var camera = Camera.main;
        if (camera == null)
        {
            return null;
        }

        var brain = camera.GetComponent<CinemachineBrain>();
        if (brain != null && brain.enabled)
        {
            brain.enabled = false;
            _disabledBrain = brain;
        }

        return camera;
    }

    public static void ReleaseCamera()
    {
        if (_disabledBrain != null)
        {
            _disabledBrain.enabled = true;
        }

        _disabledBrain = null;
    }

    /// <summary>
    /// Put Ava at a position using the game's own free-roam warp (what its map uses): it shows a
    /// loading screen, takes her off the wall, unclips the rope from the last piton, stows ice
    /// tools, brings the Climbot along and respawns her if needed. A bare TransitionToMode skips
    /// all that and can leave her T-posed, so it's only the fallback when the warp isn't there.
    /// </summary>
    public static bool TryTeleportAva(Vector3 position, float yawDegrees, out string reason)
    {
        if (!CanMoveAva(out reason))
        {
            return false;
        }

        var rotation = Quaternion.Euler(0f, yawDegrees, 0f);
        var freeRoam = FreeRoamManager.Instance;
        if (freeRoam != null)
        {
            // Kept inactive so the point never runs Awake and joins the game's own warp list.
            var go = new GameObject("CairnRouteMaker warp point");
            go.SetActive(false);
            go.transform.SetPositionAndRotation(position, rotation);
            var point = go.AddComponent<FreeRoamWarpPoint>();
            point.warpMode = PawnControllerSwitcher.Mode.Walking;
            try
            {
                freeRoam.WarpToPoint(point, (Disk.IOOperationCallback)new Action<bool>(_ => Object.Destroy(go)));
                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"Free-roam warp failed, falling back to a direct teleport: {e.Message}");
                Object.Destroy(go);
            }
        }
        else
        {
            MelonLogger.Warning("No FreeRoamManager in this game mode; using a direct teleport.");
        }

        var switcher = Switcher;
        if (switcher.CurrentMode != PawnControllerSwitcher.Mode.Walking)
        {
            reason = $"Ava must be on her feet to teleport here (currently {switcher.CurrentMode}).";
            return false;
        }

        try
        {
            switcher.TransitionToMode(
                PawnControllerSwitcher.Mode.Walking,
                new Il2CppSystem.Nullable<Vector3>(position),
                new Il2CppSystem.Nullable<Quaternion>(rotation),
                true,
                PawnControllerSwitcher.Context.None,
                true);
        }
        catch (Exception e)
        {
            MelonLogger.Error($"Teleport failed: {e}");
            reason = "The game refused the teleport (see log).";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// Stream the zone around the main camera instead of only around Ava, so flying the editor
    /// camera away from her still shows full-detail scenery. Uses the game's own (otherwise unused)
    /// camera streaming; call a few times a second while the editor is open.
    /// </summary>
    public static void StreamAroundCamera()
    {
        try
        {
            var streaming = StreamingManager.Instance;
            if (streaming != null && !IsWorldBusy)
            {
                streaming.UpdateStreamingCameraEx();
            }
        }
        catch (Exception e)
        {
            MelonLogger.Warning($"Camera streaming failed: {e.Message}");
        }
    }

    /// <summary>Hand streaming back to Ava's position.</summary>
    public static void StopStreamingAroundCamera()
    {
        try
        {
            var streaming = StreamingManager.Instance;
            if (streaming != null)
            {
                streaming.StopCameraStreaming();
            }
        }
        catch (Exception e)
        {
            MelonLogger.Warning($"Stopping camera streaming failed: {e.Message}");
        }
    }
}
