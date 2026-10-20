using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn;
using Il2CppTheGameBakers.Cairn.IO;
using Il2CppTheGameBakers.Cairn.UI;
using MelonLoader;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.EventSystems;
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
    private static bool _savedMenuDisableInputs;
    private static bool _menuInputBlocked;
    private static readonly List<BaseInputModule> _disabledInputModules = new();

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

    /// <summary>Ava's root position, or null when she isn't in the world.</summary>
    public static Vector3? AvaPosition
    {
        get
        {
            var pawns = PawnManager.Instance;
            var ava = pawns == null ? null : pawns.MCGameObject;
            return ava == null ? null : ava.transform.position;
        }
    }

    /// <summary>One hand or foot: whether it's attached, and where.</summary>
    /// <param name="PlacedFromGround">
    /// Set by the game on limbs it plants for her while she steps from the ground onto the wall.
    /// </param>
    public readonly record struct LimbGrip(bool Holding, Vector3 Anchor, bool IsFoot, bool PlacedFromGround);

    private static readonly ClimbingPawnCairnIK.IKLimbTarget[] LimbOrder =
    {
        ClimbingPawnCairnIK.IKLimbTarget.LeftHand, ClimbingPawnCairnIK.IKLimbTarget.RightHand,
        ClimbingPawnCairnIK.IKLimbTarget.LeftFoot, ClimbingPawnCairnIK.IKLimbTarget.RightFoot,
    };

    /// <summary>
    /// Read all four limbs (left hand, right hand, left foot, right foot) from the climbing
    /// controller into <paramref name="grips"/>.
    /// </summary>
    /// <returns>False, leaving the array alone, unless Ava is climbing.</returns>
    public static bool TryReadGrips(LimbGrip[] grips)
    {
        var controller = ClimbingController;
        if (controller == null)
        {
            return false;
        }

        for (var i = 0; i < LimbOrder.Length; i++)
        {
            var limb = controller.GetLimb(LimbOrder[i]);
            grips[i] = limb != null && limb.CurrentState == ClimbingV2PawnLimb.LimbState.Holding
                ? new LimbGrip(true, limb.CurrentGrabAnchorPosition, limb.IsFoot, limb.IsOnInitialHoldFromGround)
                : default;
        }

        return true;
    }

    /// <summary>Where the limb Ava is moving would attach if the player let go now.</summary>
    /// <param name="Limb">Index in the order <see cref="TryReadGrips"/> uses.</param>
    /// <param name="FromGame">
    /// True when this is the game's own pick (the limb's next best grab), false when it's only the
    /// closest rock to the limb because the game hasn't picked a hold yet.
    /// </param>
    public readonly record struct NextGrab(int Limb, Vector3 Point, bool FromGame);

    /// <summary>The grab the reaching hand or foot is heading for, if Ava is climbing and moving one.</summary>
    public static bool TryReadNextGrab(out NextGrab next)
    {
        next = default;
        var controller = ClimbingController;
        ClimbingV2PawnLimb limb = null;
        if (controller == null || !controller.GetReachingLimb(out limb) || limb == null)
        {
            return false;
        }

        var index = (int)limb.limb;
        var pose = limb.poseHandler;
        var grab = pose == null ? null : pose.nextBestGrab;
        if (grab != null && grab.isValid)
        {
            next = new NextGrab(index, grab.anchorPosition, true);
            return true;
        }

        var wall = limb.currentClosestWallHit;
        if (wall.collider == null)
        {
            return false;
        }

        next = new NextGrab(index, wall.point, false);
        return true;
    }

    /// <summary>The climbing controller, or null unless Ava is climbing.</summary>
    private static ClimbingV2PawnController ClimbingController
    {
        get
        {
            var switcher = Switcher;
            return switcher == null || switcher.CurrentMode != PawnControllerSwitcher.Mode.Climbing
                ? null
                : switcher.climbingPawnController;
        }
    }

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

    /// <summary>Rules for a free-climb session: no story, no tutorials, no hunger or thirst, warping allowed, deaths don't count.</summary>
    private const GamemodeConstraints FreeClimbConstraints =
        GamemodeConstraints.NoStory | GamemodeConstraints.NoTutorials | GamemodeConstraints.NoPhysiologicalNeeds
        | GamemodeConstraints.CanWarp | GamemodeConstraints.MeaninglessDeath;

    public readonly record struct LaunchZone(string Name, IReadOnlyList<string> Spawners);

    /// <summary>
    /// Zones of a world that a game can start in, from the main menu: the ones with spawn points
    /// (the same spawners Summit mode offers). Empty until the game has discovered its worlds.
    /// </summary>
    public static List<LaunchZone> LaunchableZones(StreamingTweakables.World world)
    {
        var result = new List<LaunchZone>();
        if (!TryGetWorldData(world, out var data) || data.zones == null)
        {
            return result;
        }

        foreach (var zone in data.zones)
        {
            var spawners = new List<string>();
            if (zone != null && zone.spawnersForMainMenu != null)
            {
                foreach (var spawner in zone.spawnersForMainMenu)
                {
                    spawners.Add(spawner.name);
                }
            }

            if (spawners.Count > 0)
            {
                result.Add(new LaunchZone(zone.name, spawners));
            }
        }

        return result;
    }

    /// <summary>
    /// From the main menu, start a story-free game at a spawn point. Goes through the game's own
    /// Summit launch (Summit runs save to their own SUMMIT slots, never to story saves) with
    /// free-climb rules instead of the player's Summit settings.
    /// </summary>
    public static bool TryLaunchFreeClimb(StreamingTweakables.World world, string zoneName, string spawnerName, out string reason)
    {
        var menu = Object.FindFirstObjectByType<MainMenu>();
        var summit = Object.FindFirstObjectByType<SummitMenu>(FindObjectsInactive.Include);
        if (menu == null || summit == null)
        {
            reason = "The main menu isn't showing.";
            return false;
        }

        if (menu.IsBusy)
        {
            reason = "The menu is busy; try again.";
            return false;
        }

        if (!TryGetWorldData(world, out var data))
        {
            reason = $"The game doesn't know the world {world} yet.";
            return false;
        }

        ZoneSceneData zone = null;
        foreach (var candidate in data.zones)
        {
            if (candidate != null && candidate.name == zoneName)
            {
                zone = candidate;
            }
        }

        ZoneSceneData.SpawnerInfo spawner = null;
        if (zone?.spawnersForMainMenu != null)
        {
            foreach (var candidate in zone.spawnersForMainMenu)
            {
                if (candidate.name == spawnerName)
                {
                    spawner = candidate;
                }
            }
        }

        if (spawner == null)
        {
            reason = $"No spawn point {spawnerName} in {world}/{zoneName}.";
            return false;
        }

        var setup = new GameSetup(spawner) { summitMode = true, constraints = FreeClimbConstraints };
        summit.SelectedWorld = data;
        summit.SelectedZone = zone;
        summit.SelectedSpawner = spawner;
        summit.GameSetup = setup;
        MelonLogger.Msg($"Launching free climb: {world}/{zoneName} at {spawnerName} ({setup.ToString()}).");
        try
        {
            menu.TransitionToStep(MainMenu.Step.LaunchNewSummitGame);
        }
        catch (Exception e)
        {
            MelonLogger.Error($"The menu refused to launch: {e}");
            reason = "the game's menu refused (see log).";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// Whether the running game is one we launched: a Summit-type game with exactly the free-climb
    /// rules. Story games and the player's own Summit runs never match. Says nothing about whether
    /// Ava is in the world yet; the game keeps its last setup while back on the main menu.
    /// </summary>
    public static bool IsFreeClimbGame
    {
        get
        {
            try
            {
                var scenes = CairnSceneManager.Instance;
                var setup = scenes == null ? null : scenes.GameSetup;
                return setup != null && setup.summitMode && setup.constraints == FreeClimbConstraints;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>The title screen's state, for the log.</summary>
    public static string DescribeMainMenu()
    {
        try
        {
            var menu = Object.FindFirstObjectByType<MainMenu>();
            var events = EventSystem.current;
            var input = InputManager.Instance;
            var selected = events == null || events.currentSelectedGameObject == null ? "(none)" : events.currentSelectedGameObject.name;
            var modules = new List<string>();
            if (events != null)
            {
                foreach (var module in events.GetComponents<BaseInputModule>())
                {
                    modules.Add($"{module.GetIl2CppType().Name}={module.enabled}");
                }
            }

            return $"step={(menu == null ? "(no menu)" : menu.step.ToString())} busy={(menu != null && menu.IsBusy)} "
                + $"selected={selected} eventSystem={(events == null ? "(none)" : events.enabled.ToString())} modules=[{string.Join(", ", modules)}] "
                + $"disableInputs={(input == null ? "(no input manager)" : input.disableInputs.ToString())}";
        }
        catch (Exception e)
        {
            return $"(unreadable: {e.Message})";
        }
    }

    /// <summary>The running game's setup, for the log.</summary>
    public static string DescribeGameSetup()
    {
        try
        {
            var scenes = CairnSceneManager.Instance;
            var setup = scenes == null ? null : scenes.GameSetup;
            return setup == null ? "(none)" : $"summitMode={setup.summitMode} constraints={setup.constraints} spawner={setup.spawner}";
        }
        catch (Exception e)
        {
            return $"(unreadable: {e.Message})";
        }
    }

    /// <summary>
    /// Flag this run the way the game's own cheats do, so its times and paths never reach the
    /// online leaderboards (they're filtered out as cheating records).
    /// </summary>
    public static void MarkRunAsCheating()
    {
        try
        {
            GameEventManager.Cheats();
        }
        catch (Exception e)
        {
            MelonLogger.Warning($"Couldn't flag the run as cheating: {e.Message}");
        }
    }

    private static bool TryGetWorldData(StreamingTweakables.World world, out WorldZoneData data)
    {
        data = null;
        var scenes = CairnSceneManager.Instance;
        return scenes != null && scenes.TryGetWorld(world, out data) && data != null;
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
    /// Stop the title screen reacting to keyboard, mouse and gamepad while our menu is over it.
    /// Unlike <see cref="BlockGameInput"/> it leaves the cursor alone, and it also turns off the
    /// UI event system's input modules, which move the menu's selection on their own. The event
    /// system itself stays on: MainMenu.TransitionToStep reads EventSystem.current and throws
    /// when it's null.
    /// </summary>
    public static void BlockMenuInput(bool block)
    {
        if (block == _menuInputBlocked)
        {
            return;
        }

        var input = InputManager.Instance;
        if (block)
        {
            if (input != null)
            {
                _savedMenuDisableInputs = input.disableInputs;
                input.DisableInputs();
            }

            var events = EventSystem.current;
            if (events != null)
            {
                foreach (var module in events.GetComponents<BaseInputModule>())
                {
                    if (module.enabled)
                    {
                        module.enabled = false;
                        _disabledInputModules.Add(module);
                    }
                }
            }
        }
        else
        {
            if (input != null)
            {
                input.EnableInputs();
                input.disableInputs = _savedMenuDisableInputs;
                input.UpdateInputContext();
            }

            foreach (var module in _disabledInputModules)
            {
                if (module != null)
                {
                    module.enabled = true;
                }
            }

            _disabledInputModules.Clear();
        }

        _menuInputBlocked = block;
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

        return TryDirectTeleport(position, rotation, out reason);
    }

    /// <summary>
    /// Move Ava without the warp's loading screen, for short hops: a bare TransitionToMode, used
    /// only when she's on her feet and the target is in the zone that's already loaded. Anything
    /// else (climbing, another zone) goes through <see cref="TryTeleportAva"/> and its loading screen.
    /// </summary>
    public static bool TryQuickTeleportAva(Vector3 position, float yawDegrees, out string reason)
    {
        if (!CanMoveAva(out reason))
        {
            return false;
        }

        var streaming = StreamingManager.Instance;
        var targetZone = streaming == null ? null : streaming.GetBestZoneAt(position);
        var sameZone = targetZone != null && streaming.CurrentZone != null && targetZone.name == streaming.CurrentZone.name;
        if (Switcher.CurrentMode != PawnControllerSwitcher.Mode.Walking || !sameZone)
        {
            MelonLogger.Msg($"Quick teleport not possible ({Switcher.CurrentMode}, target zone {(targetZone == null ? "none" : targetZone.name)}); using the warp.");
            return TryTeleportAva(position, yawDegrees, out reason);
        }

        return TryDirectTeleport(position, Quaternion.Euler(0f, yawDegrees, 0f), out reason);
    }

    private static bool TryDirectTeleport(Vector3 position, Quaternion rotation, out string reason)
    {
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
