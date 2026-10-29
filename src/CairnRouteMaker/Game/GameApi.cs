using System;
using System.Collections;
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
    private static CanvasGroup _hiddenTitleMenu;
    private static float _savedTitleMenuAlpha;
    private static UnityEngine.UI.Image _hiddenTitleArrow;
    private static bool _savedTitleArrowEnabled;

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
    /// <param name="Reaching">The player is moving this limb towards a new hold (so it isn't holding).</param>
    public readonly record struct LimbGrip(bool Holding, Vector3 Anchor, bool IsFoot, bool PlacedFromGround, bool Reaching);

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
            var state = limb == null ? ClimbingV2PawnLimb.LimbState.None : limb.CurrentState;
            grips[i] = state == ClimbingV2PawnLimb.LimbState.Holding
                ? new LimbGrip(true, limb.CurrentGrabAnchorPosition, limb.IsFoot, limb.IsOnInitialHoldFromGround, false)
                : new LimbGrip(false, default, false, false, state == ClimbingV2PawnLimb.LimbState.Reaching);
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

    /// <summary>Split a "World/Zone" key (see <see cref="CurrentZoneKey"/>) into its parts.</summary>
    public static bool TryParseZoneKey(string zoneKey, out StreamingTweakables.World world, out string zone)
    {
        var parts = zoneKey?.Split('/', 2);
        world = default;
        zone = parts is { Length: 2 } ? parts[1] : null;
        return zone != null && Enum.TryParse(parts[0], out world);
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

    public readonly record struct WorldInfo(StreamingTweakables.World Id, bool IsCurrent);

    public readonly record struct ZoneInfo(string Name, bool IsCurrent);

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

    /// <summary>The playable mountains (not the test world) the game knows about.</summary>
    public static List<WorldInfo> Worlds()
    {
        var result = new List<WorldInfo>();
        var current = CurrentWorld;
        foreach (var id in new[] { StreamingTweakables.World.Kami, StreamingTweakables.World.Lakpa, StreamingTweakables.World.Tenzing, StreamingTweakables.World.Apa })
        {
            if (TryGetWorldData(id, out _))
            {
                result.Add(new WorldInfo(id, id == current));
            }
        }

        return result;
    }

    /// <summary>Zones of a mountain, in the game's own order; only the current one has a zone marked current.</summary>
    public static List<ZoneInfo> ZonesIn(StreamingTweakables.World world)
    {
        var result = new List<ZoneInfo>();
        var streaming = StreamingManager.Instance;
        if (streaming == null || !TryGetWorldData(world, out var data) || data.zones == null)
        {
            return result;
        }

        var currentName = CurrentWorld != world || streaming.CurrentZone == null ? null : streaming.CurrentZone.name;
        foreach (var zone in data.zones)
        {
            if (zone != null)
            {
                result.Add(new ZoneInfo(zone.name, zone.name == currentName));
            }
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

        var zone = FindZone(data, zoneName);
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
        setup.spawner = SpawnerObjectName(data, zone, spawner.name);
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

    private static ZoneSceneData FindZone(WorldZoneData world, string name)
    {
        foreach (var zone in world.zones)
        {
            if (zone != null && zone.name == name)
            {
                return zone;
            }
        }

        return null;
    }

    /// <summary>
    /// For the log: is Ava spawned and where, which zone streaming thinks she and the camera are in,
    /// the running spawner, the loaded scenes, and the spawners in them.
    /// </summary>
    public static string DescribeWorldState()
    {
        var lines = new List<string>();
        void Add(string what, Func<string> read)
        {
            try
            {
                lines.Add($"  {what}: {read()}");
            }
            catch (Exception e)
            {
                lines.Add($"  {what}: (unreadable: {e.Message})");
            }
        }

        Add("Ava", () => $"spawned={PawnManager.MCSpawned} switcher={(Switcher == null ? "none" : Switcher.CurrentMode.ToString())} at {AvaPosition?.ToString() ?? "(not in the world)"}");
        Add("camera", () => Camera.main == null ? "(none)" : $"{Camera.main.name} at {Camera.main.transform.position}");
        Add("streaming", () =>
        {
            var streaming = StreamingManager.Instance;
            return streaming == null ? "(no manager)"
                : $"world={(streaming.World == null ? "none" : streaming.World.WorldId.ToString())} zone={streaming.CurrentZone?.name ?? "none"} "
                + $"cameraZone={streaming.CurrentCameraZone?.name ?? "none"} preloading={streaming.PreloadingZone?.name ?? "none"}";
        });
        Add("scenes", () =>
        {
            var scenes = CairnSceneManager.Instance;
            return scenes == null ? "(no manager)"
                : $"busy={scenes.IsBusy()} travelling={scenes.IsTraveling} streamingForCamera={scenes.isStreamingForCamera} "
                + $"loading={scenes.ScenesLoadingCount} unloading={scenes.ScenesUnloadingCount} spawner={scenes.GameSetup?.spawner ?? "none"}";
        });
        Add("loaded scenes", () =>
        {
            var names = new List<string>();
            for (var i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                names.Add(scene.isLoaded ? scene.name : $"{scene.name} (loading)");
            }

            return $"{names.Count}: {string.Join(", ", names)}";
        });
        Add("spawners", () =>
        {
            var spawners = Object.FindObjectsByType<SpawnerMC>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var names = new List<string>();
            foreach (var spawner in spawners)
            {
                names.Add($"{spawner.name}{(spawner.gameObject.activeInHierarchy ? "" : " (inactive)")} at {spawner.transform.position}");
            }

            return names.Count == 0 ? "none loaded" : string.Join("; ", names);
        });
        Add("ice gear", DescribeIceGear);
        Add("ice triggers", () =>
        {
            var triggers = Object.FindObjectsByType<IceClimbingTrigger>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var names = new List<string>();
            foreach (var trigger in triggers)
            {
                var collider = trigger.GetComponent<Collider>();
                var where = collider == null ? trigger.transform.position.ToString() : $"{collider.bounds.center} size {collider.bounds.size}";
                names.Add($"{trigger.name} {(trigger.equipIceClimbing ? "equip" : "unequip")} at {where}{(trigger.gameObject.activeInHierarchy ? "" : " (inactive)")}");
            }

            return names.Count == 0 ? "none loaded" : string.Join("; ", names);
        });
        Add("ice surfaces", () => Object.FindObjectsByType<IceSurface>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Length.ToString());
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Whether the game means Ava to wear her ice axes and crampons. A story-free game starts
    /// with them off; only the game's ice trigger volumes, cutscenes and map warps change this.
    /// </summary>
    private static bool ShouldWearIceGear
    {
        get
        {
            var story = StoryEventManager.Instance;
            return story != null && story.GetShouldWearIceClimbingDevices();
        }
    }

    /// <summary>
    /// Give Ava her ice axes and crampons, as the game's equip trigger volumes do: mark them as
    /// worn (warps, respawns and saves go by that) and let every limb use them. The game still
    /// switches each limb between its tool and bare hand or boot to suit the surface.
    /// </summary>
    public static void EquipIceGear()
    {
        try
        {
            var story = StoryEventManager.Instance;
            var pawns = PawnManager.Instance;
            if (story == null || pawns == null)
            {
                MelonLogger.Warning("Couldn't give Ava her ice gear: the game isn't ready.");
                return;
            }

            var before = DescribeIceGear();
            story.SetShouldWearIceClimbingDevices(true);
            pawns.EquipAllIceClimbigDevices(true, false);
            MelonLogger.Msg($"Gave Ava her ice gear: {before} -> {DescribeIceGear()}.");
        }
        catch (Exception e)
        {
            MelonLogger.Warning($"Couldn't give Ava her ice gear: {e.Message}");
        }
    }

    /// <summary>For the log: what the game wants and what each limb has (allowed / in hand).</summary>
    private static string DescribeIceGear()
    {
        var parts = new List<string> { $"shouldWear={ShouldWearIceGear}" };
        var climbing = Switcher == null ? null : Switcher.climbingPawnController;
        if (climbing != null)
        {
            foreach (var target in LimbOrder)
            {
                var limb = climbing.GetLimb(target);
                if (limb != null)
                {
                    parts.Add($"{target} allowed={limb.IsIceClimbingAllowed} active={limb.IsIceClimbingDeviceEquipped}");
                }
            }
        }

        return string.Join(", ", parts);
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
        if (!IsAvaReady)
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
        var zone = FindZone(streaming.World, zoneName);
        if (zone == null)
        {
            reason = $"No zone called {zoneName} in {streaming.World.WorldId}.";
            return false;
        }

        RespawnInZone(streaming.World, zone);
        CairnSceneManager.Instance.TravelToZone(zone, streaming.World);
        return true;
    }

    /// <summary>
    /// TravelToZone loads the zone but doesn't place Ava, and a Summit-mode game (free climb) then
    /// puts her back at its setup's spawner: the zone she started in. Pointing the setup at the
    /// destination's first spawn point makes that land her there instead (and later respawns too):
    /// SpawnerMC.OnRestartLevel moves her to the spawner named in GameSetup.GetCurrentGameSetup().
    /// Zones without spawn points keep the old spawner.
    /// </summary>
    private static void RespawnInZone(WorldZoneData world, ZoneSceneData zone)
    {
        var scenes = CairnSceneManager.Instance;
        var setup = scenes.GameSetup;
        if (setup == null || !setup.summitMode || zone.spawnersForMainMenu == null || zone.spawnersForMainMenu.Count == 0)
        {
            MelonLogger.Msg($"Travelling to {zone.name} without changing the spawn point ({(setup == null ? "no game setup" : $"spawner {setup.spawner}")}).");
            return;
        }

        var spawner = SpawnerObjectName(world, zone, zone.spawnersForMainMenu[0].name);
        MelonLogger.Msg($"Spawn point for the trip: {setup.spawner} -> {spawner}.");
        // GameSetup is a struct: this is a copy, so it has to be stored back.
        setup.spawner = spawner;
        scenes.GameSetup = setup;
        MelonLogger.Msg($"The game's spawn point is now {scenes.GameSetup.spawner}.");
    }

    /// <summary>
    /// The name of the spawner object a listed spawn point means. Ava spawns at the loaded SpawnerMC
    /// whose name matches GameSetup.spawner, and a few listed names are stale: 26_LastBridge lists
    /// SpawnerMC_Kami_24_LastBridge, but its object is SpawnerMC_Kami_26_LastBridge. With no match
    /// she never spawns and the camera sits at the origin, which only shows the distant (_LOD) scenes.
    /// A name that points at no zone of the world is taken to mean this zone's spawner.
    /// </summary>
    private static string SpawnerObjectName(WorldZoneData world, ZoneSceneData zone, string listed)
    {
        var prefix = world == null ? null : $"SpawnerMC_{world.WorldId}_";
        if (prefix == null || listed == null || !listed.StartsWith(prefix, StringComparison.Ordinal))
        {
            return listed;
        }

        // "SpawnerMC_Kami_20_SpecialDome (4)" names the zone 20_SpecialDome.
        var named = listed.Substring(prefix.Length);
        var space = named.IndexOf(' ');
        if (space >= 0)
        {
            named = named[..space];
        }

        if (FindZone(world, named) != null)
        {
            return listed;
        }

        var corrected = prefix + zone.name;
        MelonLogger.Msg($"The spawn point {listed} names no zone of {world.WorldId}; using {corrected}.");
        return corrected;
    }

    public static bool TryTravelToWorld(StreamingTweakables.World world, out string reason)
    {
        if (!CanMoveAva(out reason))
        {
            return false;
        }

        if (TryGetWorldData(world, out var data))
        {
            RespawnInWorld(data);
        }

        CairnSceneManager.Instance.TravelToWorld(world, false);
        return true;
    }

    /// <summary>
    /// A world trip has the zone trip's problem, but worse: the setup still names a spawner on the
    /// old mountain, which the new one doesn't have, so Ava never spawns at all. TravelToWorld loads
    /// the mountain's first zone (01_FirstRidge on Kami), so point the setup at the first spawn point
    /// on the new mountain, which is in that zone on Kami and Tenzing.
    /// </summary>
    private static void RespawnInWorld(WorldZoneData world)
    {
        foreach (var zone in world.zones)
        {
            if (zone != null && zone.spawnersForMainMenu != null && zone.spawnersForMainMenu.Count > 0)
            {
                RespawnInZone(world, zone);
                return;
            }
        }

        MelonLogger.Warning($"{world.WorldId} has no spawn points; Ava may not appear there.");
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
    /// Hide the title screen's buttons and bouncing arrow while our menu stands in for them (it
    /// copies their look and sits in the same place). The scene behind stays.
    /// </summary>
    public static void HideTitleMenu(bool hide)
    {
        if (hide)
        {
            if (_hiddenTitleMenu == null)
            {
                var element = Object.FindFirstObjectByType<MainMenuModeSelectElement>();
                if (element == null || element.group == null)
                {
                    return;
                }

                _hiddenTitleMenu = element.group;
                _savedTitleMenuAlpha = _hiddenTitleMenu.alpha;
                _hiddenTitleArrow = element.mainMenu?.bouncingArrow?.image;
                _savedTitleArrowEnabled = _hiddenTitleArrow != null && _hiddenTitleArrow.enabled;
            }

            _hiddenTitleMenu.alpha = 0f;
            if (_hiddenTitleArrow != null)
            {
                _hiddenTitleArrow.enabled = false;
            }

            return;
        }

        if (_hiddenTitleMenu != null)
        {
            _hiddenTitleMenu.alpha = _savedTitleMenuAlpha;
        }

        if (_hiddenTitleArrow != null)
        {
            _hiddenTitleArrow.enabled = _savedTitleArrowEnabled;
        }

        _hiddenTitleMenu = null;
        _hiddenTitleArrow = null;
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
    /// Where and how to put Ava for a point picked on the rock: <see cref="Mode"/> is Walking for
    /// ground she can stand on and Climbing for a wall, with <see cref="Position"/> moved off the
    /// rock and <see cref="Rotation"/> facing it.
    /// </summary>
    public readonly record struct Placement(Vector3 Position, Quaternion Rotation, PawnControllerSwitcher.Mode Mode);

    /// <summary>
    /// How far off the rock Ava's root goes on a wall: the radius of the sphere the game's fly mode
    /// casts at the rock to find where to drop her when it puts her on a wall.
    /// </summary>
    private const float WallStandOff = 0.3f;

    /// <summary>Rock steeper than about 45 degrees is a wall, so she starts on it rather than on her feet.</summary>
    private const float MinGroundNormalY = 0.7f;


    /// <summary>How close the probe has to find rock to the picked point to trust its normal.</summary>
    private const float SurfaceProbeTolerance = 0.2f;

    /// <summary>How far a hold can stick out of its wall: the probe for the wall behind it reaches this far.</summary>
    private const float HoldDepth = 0.5f;

    private const int GroundLayer = 3;
    private const int ClimbingWallLayer = 6;
    private const int ClimbingHoldsLayer = 12;

    private static readonly Vector3[] SurfaceProbeDirections = BuildSurfaceProbeDirections();

    /// <summary>
    /// Decide how Ava should arrive at a point on the rock. Route starts only store a point and a
    /// yaw, so the surface is probed again here; if it can't be found she arrives on her feet.
    /// </summary>
    public static Placement PlaceAva(Vector3 point, float yawDegrees)
    {
        if (!TryProbeSurface(point, SurfaceMask, SurfaceProbeTolerance, out var hit))
        {
            MelonLogger.Msg($"No rock found at {point}; Ava arrives on her feet.");
            return Standing(point, yawDegrees);
        }

        return PlaceAva(point, hit, yawDegrees);
    }

    /// <summary>
    /// Decide how Ava should arrive at <paramref name="point"/>, given the rock there (a raycast
    /// hit on <see cref="SurfaceMask"/>, which is how the freecam picks it). Only ClimbingWall is
    /// climbed: steep Ground is somewhere she walks or slides, and a hold's own faces say nothing
    /// about the slope of the wall it's on, so that's looked up behind it.
    /// </summary>
    public static Placement PlaceAva(Vector3 point, RaycastHit hit, float yawDegrees)
    {
        if (!TryStartSurface(point, ref hit, out var layer))
        {
            MelonLogger.Msg($"No wall behind the hold at {point}; Ava arrives on her feet.");
            return Standing(point, yawDegrees);
        }

        var normal = hit.normal;
        if (!IsClimbed(layer, normal))
        {
            return Standing(point, yawDegrees);
        }

        // Face into the rock, upright, the way fly mode drops her. Under a roof there's no
        // horizontal direction to face, so keep the yaw she was given.
        var facing = new Vector3(-normal.x, 0f, -normal.z);
        var rotation = facing.sqrMagnitude > 0.01f ? Quaternion.LookRotation(facing.normalized, Vector3.up) : Quaternion.Euler(0f, yawDegrees, 0f);
        return new Placement(hit.point + normal * WallStandOff, rotation, PawnControllerSwitcher.Mode.Climbing);
    }

    /// <summary>
    /// The rock a route's start or finish sits on, found the way <see cref="PlaceAva(Vector3, float)"/>
    /// finds it but without logging, for drawing their markers: its outward normal (the wall's, for a
    /// point on a hold) and whether Ava would start there climbing. False while that rock isn't loaded.
    /// </summary>
    public static bool TryFindRock(Vector3 point, out Vector3 normal, out bool climbing)
    {
        normal = Vector3.up;
        climbing = false;
        if (!TryProbeSurface(point, SurfaceMask, SurfaceProbeTolerance, out var hit))
        {
            return false;
        }

        // A hold with no wall behind it: she stands, so draw it as ground.
        if (TryStartSurface(point, ref hit, out var layer))
        {
            normal = hit.normal;
            climbing = IsClimbed(layer, normal);
        }

        return true;
    }

    /// <summary>
    /// The general facing of the rock across a disc of <paramref name="radius"/> around
    /// <paramref name="point"/>, as seen from along <paramref name="axis"/>: the average normal of
    /// the rock hit by rays cast back along it across the disc. The face at the point itself can be
    /// one facet of a crack or a knob, facing anywhere. Gives <paramref name="axis"/> if no ray hits.
    /// </summary>
    public static Vector3 AverageRockNormal(Vector3 point, Vector3 axis, float radius)
    {
        var across = Vector3.Cross(axis, Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
        var along = Vector3.Cross(axis, across);

        // Rays start well in front of the disc and give up a radius behind it, so rock far behind a
        // drop-off doesn't count.
        var reach = radius + 0.5f;
        var sum = Vector3.zero;
        foreach (var (ring, count) in new[] { (0f, 1), (0.5f, 8), (1f, 12) })
        {
            for (var i = 0; i < count; i++)
            {
                var angle = 2f * Mathf.PI * i / count;
                var offset = (across * Mathf.Cos(angle) + along * Mathf.Sin(angle)) * (ring * radius);
                if (Physics.Raycast(point + offset + axis * reach, -axis, out var hit, reach + radius, SurfaceMask, QueryTriggerInteraction.Ignore)
                    && Vector3.Dot(hit.normal, axis) > 0.2f)
                {
                    sum += hit.normal;
                }
            }
        }

        return sum.sqrMagnitude > 1e-4f ? sum.normalized : axis;
    }

    /// <summary>
    /// The surface that decides how Ava starts at <paramref name="point"/>, and its layer. A hold's
    /// own faces say nothing about the slope of the wall it's on, so for a hold
    /// <paramref name="hit"/> becomes the wall behind it; false if there isn't one.
    /// </summary>
    private static bool TryStartSurface(Vector3 point, ref RaycastHit hit, out int layer)
    {
        layer = hit.collider == null ? GroundLayer : hit.collider.gameObject.layer;
        if (layer != ClimbingHoldsLayer)
        {
            return true;
        }

        layer = ClimbingWallLayer;
        return TryProbeSurface(point, 1 << ClimbingWallLayer, HoldDepth, out hit);
    }

    /// <summary>Only ClimbingWall is climbed: steep Ground is somewhere she walks or slides.</summary>
    private static bool IsClimbed(int layer, Vector3 normal) => layer == ClimbingWallLayer && normal.y < MinGroundNormalY;

    private static Placement Standing(Vector3 point, float yawDegrees) =>
        new(point, Quaternion.Euler(0f, yawDegrees, 0f), PawnControllerSwitcher.Mode.Walking);

    /// <summary>
    /// The rock surface nearest <paramref name="point"/>, within <paramref name="tolerance"/>: cast
    /// at it from all round and keep the closest hit. Every hit along each ray counts, so rock
    /// nearer a ray's start (the other side of a crack) doesn't hide it. Rays starting inside the
    /// rock pass out through its back faces without hitting them.
    /// </summary>
    private static bool TryProbeSurface(Vector3 point, int mask, float tolerance, out RaycastHit best)
    {
        const float reach = 1f;
        var bestDistance = tolerance * tolerance;
        best = default;
        var found = false;
        foreach (var direction in SurfaceProbeDirections)
        {
            var hits = Physics.RaycastAll(point + direction * reach, -direction, reach * 1.5f, mask, QueryTriggerInteraction.Ignore);
            for (var i = 0; i < hits.Length; i++)
            {
                var distance = (hits[i].point - point).sqrMagnitude;
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    best = hits[i];
                    found = true;
                }
            }
        }

        return found;
    }

    /// <summary>The 26 directions to the faces, edges and corners of a cube around the point.</summary>
    private static Vector3[] BuildSurfaceProbeDirections()
    {
        var directions = new List<Vector3>();
        for (var x = -1; x <= 1; x++)
        {
            for (var y = -1; y <= 1; y++)
            {
                for (var z = -1; z <= 1; z++)
                {
                    if (x != 0 || y != 0 || z != 0)
                    {
                        directions.Add(new Vector3(x, y, z).normalized);
                    }
                }
            }
        }

        return directions.ToArray();
    }

    /// <summary>
    /// Raised when <see cref="TryTeleportAva"/> has sent Ava somewhere. She has usually landed by then,
    /// but a warp started while the world is busy lands once it's done.
    /// </summary>
    public static event Action<Placement> Teleported;

    /// <summary>
    /// Put Ava at a point on the rock using the game's own free-roam warp (what its map uses): it
    /// shows a loading screen, takes her off the wall, unclips the rope from the last piton, stows
    /// ice tools, brings the Climbot along and respawns her if needed. On a wall the warp puts her
    /// straight into climbing, the same way the map's wall warp points do.
    /// </summary>
    public static bool TryTeleportAva(Vector3 point, float yawDegrees, out string reason)
    {
        if (!CanMoveAva(out reason))
        {
            return false;
        }

        var placement = PlaceAva(point, yawDegrees);
        if (!TryWarp(placement) && !TryHop(placement, out reason))
        {
            return false;
        }

        Teleported?.Invoke(placement);
        return true;
    }

    /// <summary>
    /// Move Ava to the rock the freecam is pointing at, within the zone that's already loaded,
    /// with no loading screen and no save (see <see cref="TryHop"/>). Another zone, or a game
    /// that's over, goes through the warp as <see cref="TryTeleportAva"/> does.
    /// </summary>
    public static bool TryQuickTeleportAva(RaycastHit hit, float yawDegrees, out string reason)
    {
        if (!CanMoveAva(out reason))
        {
            return false;
        }

        var placement = PlaceAva(hit.point, hit, yawDegrees);
        var streaming = StreamingManager.Instance;
        var targetZone = streaming == null ? null : streaming.GetBestZoneAt(hit.point);
        var sameZone = targetZone != null && streaming.CurrentZone != null && targetZone.name == streaming.CurrentZone.name;
        if (!sameZone)
        {
            MelonLogger.Msg($"Quick teleport not possible (target zone {(targetZone == null ? "none" : targetZone.name)}); using the warp.");
        }
        else if (IsGameOver)
        {
            // The warp revives her and waits for the respawn before moving her.
            MelonLogger.Msg("Quick teleport not possible (game over); using the warp.");
        }
        else
        {
            return TryHop(placement, out reason);
        }

        return TryWarp(placement) || TryHop(placement, out reason);
    }

    /// <summary>Ava has died or passed out and the game is waiting to respawn her.</summary>
    public static bool IsGameOver
    {
        get
        {
            var game = GlobalGameManager.Instance;
            return game != null && game.CurrentGameState == GlobalGameManager.GameState.GameOver;
        }
    }

    /// <summary>
    /// Close the game-over screen once Ava has been revived. Reviving her doesn't leave the game's
    /// GameOver state: only closing the screen does (GameOverMenu.PopGameOverState), and with it
    /// time restarts, the death music stops and other sound comes back. The screen's own retry
    /// warps and then closes it; its map's "fast travel" and the mod's warp have to do it here.
    /// </summary>
    public static void CloseGameOverMenu()
    {
        try
        {
            if (!TryCloseGameOverMenu())
            {
                MelonCoroutines.Start(CloseGameOverMenuWhenOpened());
            }
        }
        catch (Exception e)
        {
            MelonLogger.Error($"Failed closing the game-over screen: {e}");
        }
    }

    /// <summary>
    /// Undo what dying did to the sound when the game carries on without loading a level (see
    /// GameOverPatches): clear SoundManager.BackToMainMenu, put the sound engine's game state back
    /// to in-game (what SoundManager does on returning to the game when the flag is clear), and
    /// start the zone's music again (what MusicManager does when a game starts).
    /// </summary>
    public static void RestoreInGameSound()
    {
        var sound = SoundManager.Instance;
        if (sound != null)
        {
            var wasLeaving = sound.BackToMainMenu;
            sound.BackToMainMenu = false;
            sound.SetState(Il2CppAK.STATES.STATE_GAME.GROUP, Il2CppAK.STATES.STATE_GAME.STATE.INGAME);
            MelonLogger.Msg($"Restored in-game sound (sound manager was {(wasLeaving ? "" : "not ")}flagged as leaving, paused={sound.IsPaused}).");
        }

        var music = Il2CppTheGameBakers.Cairn.Music.MusicManager.Instance;
        if (music != null)
        {
            music.SetMusic(Il2CppAK.STATES.STATE_MUSIC.STATE.NONE, false);
        }
    }

    private static bool TryCloseGameOverMenu()
    {
        var menu = Object.FindObjectOfType<GameOverMenu>();
        if (menu == null || !menu.IsOpened)
        {
            return false;
        }

        menu.Close(false);
        MelonLogger.Msg(IsGameOver ? "Closed the game-over screen, but the game is still over." : "Closed the game-over screen.");
        return true;
    }

    /// <summary>The screen can't close while it's still fading in, so wait for it. Time is stopped, so this counts real time.</summary>
    private static IEnumerator CloseGameOverMenuWhenOpened()
    {
        var giveUp = Time.unscaledTime + 5f;
        while (Time.unscaledTime < giveUp)
        {
            yield return null;
            if (!IsGameOver)
            {
                yield break;
            }

            bool closed;
            try
            {
                closed = TryCloseGameOverMenu();
            }
            catch (Exception e)
            {
                MelonLogger.Error($"Failed closing the game-over screen: {e}");
                yield break;
            }

            if (closed)
            {
                yield break;
            }
        }

        MelonLogger.Warning("The game-over screen never finished opening; the game may stay over (no sound, death music).");
    }

    private static bool TryWarp(Placement placement)
    {
        var freeRoam = FreeRoamManager.Instance;
        if (freeRoam == null)
        {
            MelonLogger.Warning("No FreeRoamManager in this game mode; teleporting without the warp.");
            return false;
        }

        // Kept inactive so the point never runs Awake and joins the game's own warp list. The
        // warp ignores the point's rotation; on a wall the climbing controller turns her to face
        // the rock itself when it sets her up.
        var go = new GameObject("CairnRouteMaker warp point");
        go.SetActive(false);
        go.transform.SetPositionAndRotation(placement.Position, placement.Rotation);
        var warpPoint = go.AddComponent<FreeRoamWarpPoint>();
        warpPoint.warpMode = placement.Mode;

        // The warp takes the ice gear off unless the point says to wear it, and sets the game's
        // "wear ice gear" flag to match. Free climb hands it out, so keep whatever she has.
        warpPoint.setIceEquipment = ShouldWearIceGear;
        var reviving = IsGameOver;
        try
        {
            MelonLogger.Msg($"Warping Ava to {placement.Position} ({placement.Mode}).");
            freeRoam.WarpToPoint(warpPoint, (Disk.IOOperationCallback)new Action<bool>(_ => Object.Destroy(go)));
            if (reviving)
            {
                // The warp has revived her by now; as the game's own retry does, close the screen after.
                CloseGameOverMenu();
            }

            return true;
        }
        catch (Exception e)
        {
            MelonLogger.Warning($"Free-roam warp failed: {e.Message}");
            Object.Destroy(go);
            return false;
        }
    }

    /// <summary>
    /// The steps of the warp's own routine (FreeRoamManager.WarpRoutine) without the loading screen
    /// around it or the save it ends with, which shows the auto-save icon. Unlike the routine it
    /// passes the rotation on, so on a wall she starts out facing the rock, as fly mode drops her.
    /// </summary>
    private static bool TryHop(Placement placement, out string reason)
    {
        var revived = false;
        var switcher = Switcher;
        var climbing = switcher.climbingPawnController;
        var path = switcher.PathRecorder;
        try
        {
            MelonLogger.Msg($"Moving Ava to {placement.Position} ({placement.Mode}).");

            // The warp's first step: after a fall that ends the game she has to be brought back
            // before she can be moved.
            if (IsGameOver && PawnManager.Instance != null)
            {
                MelonLogger.Msg("Reviving Ava first.");
                PawnManager.Instance.Respawn(PawnManager.RespawnMode.Revive, null);
                revived = true;
            }

            if (path != null)
            {
                path.HaltPath();
            }

            // As the warp does: re-equip the ice gear she's meant to wear, or put it away. Putting it
            // away also stops her limbs using it afterwards.
            var pawns = PawnManager.Instance;
            if (pawns != null)
            {
                if (ShouldWearIceGear)
                {
                    pawns.EquipAllIceClimbigDevices(true, false);
                }
                else
                {
                    pawns.UnEquipAllIceClimbingDevices(true, true, true);
                }
            }

            switcher.TransitionToMode(
                placement.Mode,
                new Il2CppSystem.Nullable<Vector3>(placement.Position),
                new Il2CppSystem.Nullable<Quaternion>(placement.Rotation),
                true,
                PawnControllerSwitcher.Context.None,
                true);

            var robot = climbing == null ? null : climbing.associatedRobotPawn;
            if (robot != null)
            {
                robot.TeleportNearClimber(true, true);
            }

            UnclipRope(climbing);
        }
        catch (Exception e)
        {
            MelonLogger.Error($"Teleport failed: {e}");
            reason = "The game refused the teleport (see log).";
            return false;
        }
        finally
        {
            if (path != null)
            {
                path.ResumePath();
            }

            if (revived)
            {
                CloseGameOverMenu();
            }
        }

        reason = null;
        return true;
    }

    /// <summary>Take the rope off every piton securing it, so it doesn't stretch back to where she was.</summary>
    private static void UnclipRope(ClimbingV2PawnController climbing)
    {
        var harness = climbing == null ? null : climbing.harness;
        var lifeline = harness == null ? null : harness.Lifeline;
        if (lifeline == null)
        {
            return;
        }

        // The warp loops until none is left; the cap only guards against a piton that won't let go.
        for (var i = 0; i < 64; i++)
        {
            var piton = lifeline.GetLastSecuringPiton();
            if (piton is null)
            {
                return;
            }

            // Unity's != is false for a piton whose object is already gone; that one is only removed.
            if (piton != null)
            {
                lifeline.DetachPiton(piton);
            }
            else
            {
                lifeline.RemovePiton(piton);
            }
        }
    }

    private static readonly SurvivalStat[] SurvivalStats =
    {
        SurvivalStat.Hunger, SurvivalStat.Thirst, SurvivalStat.Cold, SurvivalStat.Exhaustion, SurvivalStat.Hp,
    };

    /// <summary>
    /// Put Ava back to full: hunger, thirst, cold, exhaustion and HP refilled (and their critical
    /// timers stopped), every finger at full health, and each limb's climbing stamina reset.
    /// Finger tape is left alone.
    /// </summary>
    public static void RestoreAva()
    {
        try
        {
            var data = GameDataManager.Instance;
            if (data != null)
            {
                var before = DescribeStats(data);
                foreach (var stat in SurvivalStats)
                {
                    data.RefillStat(stat);
                }

                // After the other stats, so whatever caps max HP has been refilled too.
                data.SetCurrentHp(data.CurrentMaxHp);
                var climber = data.GameData == null ? null : data.GameData.climberData;
                if (climber != null)
                {
                    foreach (var stat in SurvivalStats)
                    {
                        climber.EndCriticalStatTimer(stat);
                    }

                    var fingers = climber.fingerHP01;
                    for (var i = 0; fingers != null && i < fingers.Length; i++)
                    {
                        climber.SetFingerHealth01(i, 1f, true, false);
                    }
                }

                MelonLogger.Msg($"Restored Ava: {before} -> {DescribeStats(data)}.");
            }

            var climbing = Switcher == null ? null : Switcher.climbingPawnController;
            if (climbing != null)
            {
                foreach (var target in LimbOrder)
                {
                    var limb = climbing.GetLimb(target);
                    if (limb != null)
                    {
                        limb.ResetStamina();
                    }
                }
            }
        }
        catch (Exception e)
        {
            MelonLogger.Warning($"Couldn't restore Ava's stats: {e.Message}");
        }
    }

    private static string DescribeStats(GameDataManager data)
    {
        var parts = new List<string>();
        foreach (var stat in SurvivalStats)
        {
            parts.Add($"{stat} {data.GetStat(stat):0.##}");
        }

        var climber = data.GameData == null ? null : data.GameData.climberData;
        if (climber != null)
        {
            parts.Add($"fingers {climber.GetAverageFingersHealth01PerHand(false):0.##}");
        }

        return string.Join(", ", parts);
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
