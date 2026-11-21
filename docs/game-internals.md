# Cairn game internals

Notes on the game code this mod depends on. They were found by reading the IL2CPP interop assemblies MelonLoader generates (`MelonLoader/Il2CppAssemblies/`), the game's call-graph cache, and the [CairnMP](https://github.com/CairnMP/cairnmp-mod) source (AGPL-3.0, read for names only).

Checked against Cairn Steam build 23683671, Unity 6000.0.59f2, MelonLoader 0.7.3. The October 2026 update (game 1.1.593s, Unity 6000.0.72f1, the On the Trail - Deep Water DLC) moved several of these; the notes marked **1.1** were re-read against it.

Types in the game's global namespace are prefixed `Il2Cpp.` in the interop assemblies. Game fields appear as properties.

Confidence: **sig** = confirmed from signatures, **xref** = confirmed from the native call graph, **mp** = what CairnMP does, **guess** = unverified.

## Ava (player pawn)

- `Il2Cpp.PawnManager.Instance` (check `IsReady`) → `MCGameObject` is Ava's root object; use its `transform` for position and rotation. **sig/mp**
- Spawn hooks: `PawnManager.add_MCPawnReady`, static `add_MCSpawnedCallback(Action<GameObject>)`, `GameEventManager.OnMCSpawned`. **sig**
- `PawnControllerSwitcher` (component on Ava):
  - `CurrentMode` is one of `None`, `Walking`, `Climbing`, `Flying` (debug fly mode), `Hovering` (waiting for streaming).
  - `add_OnModeChange(prev, cur)` fires on mode changes.
  - Sub-controllers: `walkingPawnController`, `climbingPawnController` (`ClimbingV2PawnController`), `flyModePawnController`. **sig**
- Simplest state check: `switcher.NetplayData.capture.GetPlayerPawnState()` → `NetFrame.PawnStateType { Invalid, Walking, Climbing, Dead, Falling }`. **sig/mp**
- Game state: `GlobalGameManager.Instance.CurrentGameState` / `IsInGame`. **sig**

## Teleport (start position)

- Game API: `PawnControllerSwitcher.TransitionToMode(Mode, Vector3? pos, Quaternion? rot, bool force, Context ctx, bool checkShouldHover)`. **sig**
  - It has 52 native callers, including `NetplaySpawn.TeleportLocalPlayerTo`, `SpawnerMC.TeleportTo` and `FreeRoamManager.WarpRoutine`. **xref**
  - With `checkShouldHover: true`, Ava hovers until the target zone has streamed in. **xref**; whether that works for far targets is a **guess** until tested at runtime.
  - Passing `Mode.Climbing` puts her straight onto a wall (**xref**; the mod uses it for wall starts):
    1. `TransitionToMode` sets the root position, and the rotation only if one is given, then switches mode.
    2. `ClimbingV2PawnController.ResetClimber(ClimbingStartMode.Default)` runs `ResetLimbs` and `HoldsSetUtility.Refresh` / `ApplyImmediate`, which grab holds at once. Then `UpdateClosestWallPointPosAndNormal` and `UpdateTransformForward` turn her to face the rock.
  - **Fly mode's "go to climb"** is the game's own dev tool for this (`PawnControllerFlyMode.Update` / `GoToState`, **xref**):
    1. It runs `Physics.SphereCast` from the fly pawn along its forward: radius 0.3, 50 m, `Helpers.ClimbingWallAndGroundLayerMask`.
    2. `RaycastHitExtensions.GetCapsulePoleForHitPosition(hit, origin, dir)` (in `Il2CppTheGameBakers.Cairn.Utilities`) gives the sphere's centre at impact, 0.3 m off the rock. It becomes `offStateParameters.position`.
    3. On exit it moves her root there, keeping a yaw-only rotation, and calls `TransitionToMode(Climbing, null, null, false, None, true)`.
  - The game's debug commands `ClimberMemorySaveDC` / `ClimberMemoryRestoreDC` and the climb snapshots (`SceneSnapshotClimbData`: a hold id per limb) restore an exact saved grip. They can't place her at an arbitrary point.
  - **Wall or ground (the mod's rule):**
    - She climbs only where the rock under the point is on the ClimbingWall layer and steeper than about 45° (normal y < 0.7).
    - Ground is always walked, however steep.
    - A hold (ClimbingHolds) is judged by the ClimbingWall surface within 0.5 m of it, since a jug's top faces up even on a vertical wall.
    - The freecam passes its raycast hit. Route starts store only a point, so the mod finds the surface again by casting at the point from 26 directions (`Physics.RaycastAll`, so the far side of a crack doesn't hide it). If nothing is found within 0.2 m, it logs that and she arrives on her feet.
- Full sequence in `FreeRoamManager.WarpRoutine` (**xref**):
  1. If `GlobalGameManager.Instance.CurrentGameState == GameOver`, `PawnManager.Respawn(RespawnMode.Revive)`.
  2. Wait while `CairnSceneManager.IsBusy()`.
  3. `PathRecorder.HaltPath()`.
  4. Equip or unequip ice gear as the warp point says (see "Ice gear").
  5. `TransitionToMode`.
  6. `RobotPawnController.TeleportNearClimber`.
  7. Clean up pitons via `Lifeline`.
  8. Wait a frame, then while the scene manager is busy or Ava is hovering.
  9. `PathRecorder.ResumePath()`.
  10. Save.
- CairnMP's approach (**mp**):
  1. Teleport only while Walking.
  2. If the target is in another zone (`StreamingManager.Instance.GetBestZoneAt(pos)` differs from `CurrentZone`), call `CairnSceneManager.Instance.TravelToZone`, then wait for loading to finish.
  3. Write the transform directly and repeat until Ava stays put.

## Survival stats and fingers (restoring Ava)

- `SurvivalStat` enum: `Hunger`, `Thirst`, `Cold`, `Hp`, `Exhaustion`. **sig**
- **1.1:** `GameDataManager` is gone. Survival lives on `Il2Cpp.SurvivalManager.Instance` and the end-game flag on `StoryEventManager` (see "Route loadouts"). **sig**
- `SurvivalManager.Instance` has `RefillStat(stat)`, `GetStat(stat)`, `ModifyStat(stat, toAdd, canCountAsDebuf)`, `SetCurrentHp(value)`, `CurrentHp`, `CurrentMaxHp` and `AbsoluteMaxHp`. Max HP can be eroded below the absolute max. **sig**; whether `RefillStat(Exhaustion)` clears exhaustion is a **guess** (the mod logs the values before and after).
- Fingers: `SurvivalManager.fingersState` (`FingersState`) has `fingerHP01` (one 0-1 value per finger). `SurvivalManager.SetFingerHealth01(fingerIndex, health01, ignoreCooldown, useTapingMultiplier)` and `GetAverageFingersHealth01PerHand(isRightHand)` wrap it. **sig**
- `SurvivalManager.survivalState` (`SurvivalState`): `EndCriticalStatTimer(stat)` stops a stat's pass-out countdown. **sig**
- Climbing stamina is per limb: `ClimbingV2PawnLimb.ResetStamina()`. **sig**
- `SurvivalManager.ResetStats()` / `ResetSurvivalState()` also exist. They aren't used because what else they reset is unknown.
- Setting a finger's health to 1 also tapes it: see "Route loadouts" below.

## Route loadouts

How the mod sets Ava up for a route (`Game/AvaLoadout.cs`). Read by disassembly; tweakable values come from `StreamingAssets/aa/StandaloneWindows64/tweakables_assets_all.bundle` (read with UnityPy, **asset**). **game** = confirmed in game.

- **Values (asset):** max HP 100; hunger, thirst and cold max 100, warning at 0.33, pass-out timer 180 s; finger hurt threshold 0.25, tape threshold (`fingerMaximumThreshold`) 0.7; `endGameMaxStatRatio` 0.333, `endGameMaxHpRatio` 0.5.
- **End game** (`StoryEventManager.Instance.InEndGame` in 1.1; it was `GameDataManager.InEndGame`, true if `GameData.isInEndGame` or summit end is set):
  - While it's on, `CurrentMaxHp` is halved, `ModifyStat` caps hunger, thirst and cold at a third, pass-out timers are a third as long, and `WalkingPawnController.EndGameExhausted` stops her running. **xref**
  - Before 1.1, `GameDataManager.SetIsInEndGame(true)` played a 10 s coroutine that pauses survival and game time (both reference counts) and only then clamps the stats. Cancelling it early with `SetIsInEndGame(false)` leaks both pauses. **xref**
  - The mod skips the coroutine: it sets the flag alone, then `EndGameAnimationAmount = 1` (0 to turn off) so the HUD draws the caps. `ModifyStat(stat, 0, false)` then clamps a stat under its cap. **game** (before 1.1): hunger, thirst and cold settle at 33.3 and HP at 50/50, and switching it off lifts the caps.
  - **1.1:** the flag setter is `StoryEventManager.SetIsInEndGame(bool)`, which calls nothing else (no xrefs), so it's still just the flag. The animation is `SurvivalManager.PlayEndGameAnimation` / `AnimateEndGame`, started from `SurvivalManager.OnEndGameTriggered`. **sig/xref**; untested in game since the update.
  - `RefillStat` ignores the cap (it clamps with the minimum first), so it fills hunger, thirst and cold to 100 even in end game. **xref**
  - It needs no story systems; the story only switches it on (`CutsceneStoryEvent`). Avoid `SetIsSummitEnd`: in summit end `ModifyStat` ignores every change. **xref**
- **Hunger, thirst and cold drain:** `GamemodeConstraints.NoPhysiologicalNeeds` is read live from `CairnSceneManager.GameSetup` by `CanStatBeAffected` (on `SurvivalManager` since 1.1) (game-time drain, `ModifyStat`'s negative changes, pass-out timers) and by `StatusUI.Update`. Flipping the flag on the running setup (a struct: assign it back) starts or stops the drain. **game**: in free climb, hunger fell from 33.3 to 24 and thirst to 17 in about 4 minutes; cold held at Lunch Break. Assist mode's God mode keeps it off regardless.
- **Health:** `SetCurrentHp` writes the value without clamping and kills Ava at 0. At 1 HP or less the next knock kills her, and under 15 she's in the critical state. Max HP isn't eroded in this build (`maxHpErosion` 0). **xref**
- **Fingers:** health runs 0-1 (`SetFingerHealth01` clamps). A finger shows tape above 0.7 (`IsFingerTaped` compares with `FingerRenderer.TAPE_VISIBILITY_THRESHOLD`, which reads the threshold), and taping sets 1.0. So setting every finger to 1 tapes them all. **xref**
  - `SetFingerHealth01(i, h, true, false)` sets it exactly (the debug command `ChangeFingersHPDC` passes the same flags) and raises `FingerHealthHasChanged`, which updates the hand UI. Fingers 0-4 are the left hand. **xref**
  - The taping screen's grip gauge (`GetGlobalFingerStatePerHand`): excellent if the hand averages over 0.7 with no finger under 0.25, normal if it averages over 0.25 with at most 2 fingers under 0.25, hurt otherwise. In play it changes stamina, not grip (`fingerGripEffectMultiplier` is 0). **xref/asset**
- **Item ids:** `InventoryItemStringId` wraps an int; `InventoryItemStringIdEnum` names them (`ITEM_PITON`, ...). Saves store the label, so the mod does too. **xref**
- **Item names:** `LocalizationManager.Instance.Get(item.locKey)`, from `InventoryManager.Instance.library.items` (315 items; 158 pass the mod's filter). **game**
- **The flask** lives outside the bag, chalk bag, harness and Climbot, so emptying those leaves it and its water, and the item filter doesn't offer it. **game**
- **Emptying inventories:** `InventoryManager.Clear()` and `Recreate()` reset all seven inventories, charms and readables included. The item's own `Remove` leaves leftovers and throws for custom items (`CustomItem.Remove`), so the game's `EmptyBackpackDC` breaks on a light stick. **xref**
  - The mod snapshots each of `BagInventoryData` (`GetBagItemData(i).ItemData`), `PuffInventoryData`, `PitonInventoryData` and `ClimbotInventoryData` (`inventory[i]`), then calls the inventory's own `Remove(item, data)` on everything but custom items. It raises `NotifyItemDestroyed` for the UI. **game** (a fresh free-climb inventory is 18 stacks). The quick wheel's piton and chalk counts only catch up when the wheel next refreshes.
- **Adding items:** the debug command `GiveItemDC` makes `new InventoryStorageRequest(id, count, contentId, 0, LootTarget.Aava, BagSearchMode.BottomUp, printErrors, displayToasts, metadata)` and calls `PutItemParts`. **xref**
  - `PutItemsWhole` asks for count × `MaxCount` parts: whole items, and full containers when given a content id.
  - Climbing gear stacks, so the mod adds pitons as parts; one without per-unit data gets full health.
  - Whatever doesn't fit in its inventory goes to the fallback (puff and pitons overflow into the bag), and the rest is dropped (`response.MissingParts`).
  - Adding a flask replaces the existing one. **xref**
  - A whole chalk (`ITEM_PUFF_1`) is three puffs, so the mod counts chalk in puffs, like pitons. **game**
  - Pitons (`ITEM_PITON`) and crystal pitons (`ITEM_CRYSTAL_PITON`, which go into rock too hard for normal ones) are added the same way, as parts. The picker doesn't offer either: a loadout sets them with a count of its own. Crystal pitons from a loadout are untested in game.
- **Cooked variants:** meals and drinks that share a name differ by `ConsumableItem.modifier` (`ConsumableModifier`: None, Creamy, ExtraSweet, Intense, Herbal, Mixed, Flambeed, Rare), and each has its own effects. **sig**
  - **Never call `PutItemParts` / `PutItemsWhole` with the `out InventoryStorageRequestResponse` overloads from a mod.** The response is a struct with two list references, and the interop passes an 8-byte slot for it: the game writes past it and crashes outright, with nothing in any log. **game** Use `PutItemParts(request)`, which returns the response, and for whole items call `request.WholeItemRequestToPartsRequest()` first (that plus `PutItemPartsInternal` is all `PutItemsWhole` does, **xref**).
- **What resets the inventory:** respawns from a spawner or save (`PawnManager.Respawn`) and `ResetClimber(ResetAfterFall)` run `Recreate` + `GiveInitialItems`. The mod's revive warps don't. Assist mode's infinite gear refills pitons. **xref**

## Ice gear (axes and crampons)

Read by disassembly; the free-climb fix is confirmed in game.

- **Whether she wears it:** `StoryEventManager.Instance` has a `shouldWearIceClimbingDevices` flag (`Get`/`SetShouldWearIceClimbingDevices`), saved with the game. `Init` clears it. Once Ava is ready, `TryActivateIceClimbingDevices` runs once and calls `PawnManager.EquipAllIceClimbigDevices` (sic) or `UnEquipAllIceClimbingDevices` to match it. **xref**
- **What changes it:**
  - `IceClimbingTrigger` volumes in the world (`equipIceClimbing` true or false). `OnTriggerEntered` acts only when `GameSetup.IsStoryDisabled`, so they're how Summit and free climb get the gear. At Icefall, the only loaded equip trigger is far below the spawn.
  - `CutsceneStoryEvent.wearIceClimbingGearAfterCutsceneEnds` in the story.
  - The map warp: `WarpRoutine` sets the flag from `FreeRoamWarpPoint.setIceEquipment`, then equips or unequips. A warp point with the flag off strips the gear. **xref**
- **Per limb:** `EquipAllIceClimbigDevices(manageOutfitSkin, force)` calls `ClimbingV2PawnLimb.EquipIceClimbingDevice` on each limb. That sets `IsIceClimbingAllowed` (it doesn't need an inventory item). It also switches Ava to the ice outfit (`PawnSkinHandler.TryChangeBodyAndOutfitSkin(2)`).
  - `WalkingPawnController.OnSwitchOn` clears every limb's flag, so they read false while she walks.
  - `ClimbingV2PawnController.ResetGettingUpFromGround` re-equips when `IsWearingIceClimbingOutfit`. The outfit is the lasting state.
  - On the wall, the auto-switch (`UpdateIceGearAutoSwitch*`) picks the tool or the bare hand or boot to suit the surface.
- **What the mod does:**
  - At the start of a free-climb session it sets the flag and calls `EquipAllIceClimbigDevices(true, false)`.
  - Its warp point copies the current flag into `setIceEquipment`.
  - In free climb, a Harmony prefix skips `IceClimbingTrigger.OnTriggerEntered` for unequip triggers.

## Limb attach (route scoring)

- `ClimbingV2PawnController.limbs` / `hands` / `feet` / `GetLimb(IKLimbTarget)`. **sig**
- Each `ClimbingV2PawnLimb` is a plain object with:
  - `limb` (`IKLimbTarget { LeftHand, RightHand, LeftFoot, RightFoot }`).
  - `CurrentState` (`LimbState`: `None`, `Idle`, `Holding = 2`, `Reaching`, `GoingToHold`, `GoingToHoldIceClimbing`, `RestoreIceClimbing`, `LowGravityStarsClimbing`).
  - `LastHeldGrab` and `currentClosestWallHit` (a `RaycastHit`). **sig**
- A grab (`ClimbingV2HoldGrab`) has `hold`, `anchorPosition`, `anchorRotation`. A hold (`ClimbingV2Hold`) has `id` (stable and position-derived for hand-placed holds) and `dynamic` (true for holds generated on bare rock). **sig**
- Hooks, best first:
  1. `GameEventManager.Climbing.OnLimbHasJustGrabHold(limb, isDynamicHold, grabPosition, grabRotation, onInputFromPlayer)`.
     - Raised only by `ClimbingV2PawnLimb.GoingToHoldStateUpdate`, when a reaching limb lands. **xref**
     - Needs no Harmony patch.
     - Misses instant grabs (mantling, landing, dyno, …). Ice tools raise `OnIceClimbingDeviceHasJustGrabHold` instead.
  2. Harmony postfix on `ClimbingV2PawnLimb.OnChangedLimbState(LimbState previousState)`, filtered to transitions into `Holding`.
     - Catches every grab, including ones the player didn't make. **xref**
     - In the postfix, the grab data and wall hit are already up to date.
  3. `ClimbingV2PawnController.OnLimbStateChange`, an instance delegate field. **sig**; whether the game actually invokes it is a **guess**.
- Detach: `GameEventManager.OnLimbDrop(Action<DropCause, limb>)`. **sig**
- **What the mod uses:** it polls each limb every frame instead of hooking, and treats a limb entering `Holding` (or its anchor jumping more than 0.25 m in one frame while holding) as an attachment. That catches instant grabs without a Harmony patch. **sig**; untested at runtime.
  - `switcher.climbingPawnController.GetLimb(ClimbingPawnCairnIK.IKLimbTarget)`. Both enums are nested in the interop: `ClimbingPawnCairnIK.IKLimbTarget` and `ClimbingV2PawnLimb.LimbState`.
  - `limb.CurrentGrabAnchorPosition` is where the limb is attached; `limb.IsFoot` tells feet from hands.
  - `limb.IsOnInitialHoldFromGround` marks limbs the game places itself while Ava steps from the ground onto the wall. Feet with it set are ignored, since they are often on the ground.
  - A limb in `Reaching` is one the player is moving. After a teleport puts Ava on the wall, the first limb seen reaching starts the route clock. Confirmed in game on the free-climb start.

## Climbable surfaces (brush raycast)

- Physics layers:

  | Index | Layer |
  |---|---|
  | 3 | Ground |
  | 4 | Water |
  | 6 | ClimbingWall |
  | 7 | Pawn |
  | 11 | Player |
  | 12 | ClimbingHolds |
  | 13 | InvisibleWall |
  | 27 | TechArt/IceSurfaces |
  | 28 | ZoneLimitersAndNoGoAreas |

- The game's masks are statics on `Il2Cpp.Helpers`, e.g. `ClimbingWallAndHoldsAndGroundLayerMask` and `ClimbingWallLayerMask`. **sig**
- Brush mask: `Helpers.ClimbingWallAndHoldsAndGroundLayerMask`, or `(1<<6)|(1<<12)|(1<<3)`; add `1<<27` to include ice. Use `QueryTriggerInteraction.Ignore`.
- Holds are generated anywhere on ClimbingWall mesh colliders (`DynamicHold`). Hand-placed holds live on ClimbingHolds in `_Holds` subscenes.

## Cameras and input

- **Debug free cam** (best fit for the editor):
  - `Resources.Load("Prefabs/FreeCam")`, instantiate it, then `GameEventManager.FreeCam.FreeCamActivate()`.
  - The component is `Il2Cpp.FreeCamController` (static `Instance`, `vCam`). **sig/xref**
- **Fly mode** (the game's built-in "fly, then drop Ava here" tool):
  - `switcher.flyModePawnController.ForceStateOn()` / `ForceStateOff(bool walk)`.
  - It previews the landing spot and calls `TransitionToMode` on exit. **sig/xref**
- **Photo mode:** `Il2CppTGBTools.PhotoMode.PhotoModeManager.Instance` (`Open`, `Close`, `IsOpened`, `Camera`). It limits how far the camera can move from Ava, which makes it a poor editor camera. **sig**
- **Gameplay cameras:** Cinemachine, via `CameraManager.Instance` (`CinemachineBrain`, `CurrentCairnCamera`). **sig**
- **Input:** `Il2Cpp.InputManager.Instance`.
  - Contexts: `PushInputContext(InputContext)` returns a handle; release it with `handle.PopInputContext()`.
  - `ShowAndFreeMouse()` / `HideAndLockMouse()` toggle the cursor.
  - Raw keys and mouse: `Keyboard.current` / `Mouse.current`.
  - To block the game: `disabledInputs = true` (`disableInputs` before 1.1; `DisableInputs()` sets it and calls `UpdateInputContext`) + `SetIgnoreInputEvents(true)`. To restore: `SetIgnoreInputEvents(false)`, `EnableInputs()`, `UpdateInputContext()`. **sig/mp**

## Location and version

- `StreamingManager.Instance`: `World` (`WorldZoneData`, `WorldId` Kami/Lakpa/Tenzing/Apa), `CurrentZone` (`ZoneSceneData`), `GetBestZoneAt(pos)`. **sig**
- Proposed route key: `World.WorldId + "/" + CurrentZone.name`. **guess**: that the name is stable hasn't been checked.
- Game version: `Il2CppTheGameBakers.Cairn.CairnVersion.ComputeFull()` / `ComputeSmall()`. `Application.version` is just `"1.0"`.

## Other

- Debug commands (`DebugMenuManager.ExecuteCommand<T>()`): `FreeCamDC`, `FlyModeDC`, `GoToLocationBookmarkDC`, `SceneSnapshotCopyDC` / `SceneSnapshotPasteDC`, `InfiniteStaminaDC`, `StickyHoldsDC`, `HoldCreationDC`.
- Drawing:
  - `Helpers.DrawGLLine` / `DrawGLQuad` with `Helpers.GLLineMaterial`.
  - `TGBDecal` / `TGBDecalsManager`.
  - `ColliderHighlighter`, `ClimbPathRenderer`.
  - The map's routes look like node lists (`nodes`, `MAX_NODES_PER_ROUTE`) drawn with pixel-width outlines (`outlinePixelWidth`, `outline2PixelWidth`, `lineWidthMulitplier`) through a command buffer (`AddPathRenderersToCommandBuffer`). **sig**; whether the nodes are spline control points or a dense polyline is a **guess**.
  - The map (eagle eye) UI is `EagleEyeUI`. Its "Display route" button (Alt) calls `OnShowAavaPathInput()`, which sets `pathOn` and shows or hides Ava's recorded path (`PathRecorder.ShowPath` / `HidePath` / `PathIsDisplayed`, `OnPathDisplayHasChanged`). The mod follows `pathOn` after each press to show or hide the route line. **sig**; that `pathOn` is already updated when the method returns is a **guess**. Its "Read map" (Space) view is the topology view (`SetTopologyView`, `topologyDifficultyWidget` is the Easier/Harder legend).
  - The route line (`RouteLine`) asks for `Hidden/Internal-Colored` first: it multiplies in vertex colours and takes `_ZTest`, `_ZWrite`, `_Cull`, `_SrcBlend` and `_DstBlend` as properties. Whether the game's build includes it is a **guess** until the log's "Route line shader" line says so.
  - CairnMP draws trails with `LineRenderer` and the first shader it finds among `Sprites/Default`, `Universal Render Pipeline/Unlit` and `Unlit/Color`.
- `StreamingAssets/GHOSTS` holds recordings of climbing ghosts (tutorial holograms, rivals, community runs), played by `GhostsManager`. It contains no route data.

## Decals (route paint)

Confirmed in game; the shader was read by disassembling `TGB/Decal` from `tgbothers_assets_all.bundle`.

- **Rendering:**
  - Built-in render pipeline, deferred shading.
  - `TGBDecalsManager` draws every enabled `TGBDecal` into the G-buffer with a command buffer.
  - A decal registers itself through `TGBDecalSystem.AddDecal` in `OnEnable`, so configure it while its GameObject is inactive.
- **`TGB/Decal` shader:** a box projector over the unit cube.
  1. It rebuilds the world position from depth.
  2. It converts that to box space with `unity_WorldToObject`.
  3. It discards anything outside ±0.5.
  4. It computes `uv = (local.xz + 0.5) * _MainTex_ST * _TilingOffset`, then `(uv - 0.5) * _UvRescale + 0.5`.

  So the projection runs along the box's local **Y**: u follows local X and v follows local Z. It blends with the texture's alpha.

  A texel colours every surface along its whole column through the box. To keep a projection shallow, scale the box along local Y. The transform is read live, so a non-uniform scale or a move while the decal is enabled takes effect right away.
- **Decals added from code need their texture transform set on the material.** Their `MaterialPropertyBlock` stays empty (even after `UpdateMPB()`), so `_TilingOffset` and `_MainTex_ST` come from the material:
  - Set `_TilingOffset = (1, 1, 0, 0)`.
  - Reset `mainTextureScale` and `mainTextureOffset`.

  Materials borrowed from the game's tape decals have `_MainTex_ST = (-0.5, 1, 0, 0)`. With clamped sampling, that pins every pixel to one texel column.
- **Opacity goes through the decal, not the material.** `TGBDecal.UpdateOpacity` writes `_Opacity = lifetimeOpacityMul * fadeOutOpacityMul * opacityMul` into the decal's `MaterialPropertyBlock` (creating it if needed), and that value overrides the material's `_Opacity`. Setting `_Opacity` on the material does nothing visible. To fade a decal, call `SetOpacityMultiplier(x)`: it sets `opacityMul` and calls `UpdateOpacity`. **disasm** (`GameAssembly.dll`, addresses from Cpp2IL's `cpp2il_out`). In game, a material `_Opacity` fade on route paint had no visible effect.
- **Fades the build leaves out:** the shader declares `NEARFADE` (`_NearFadeMin`, `_NearFadeMax`), but no variant was compiled with it, and no program reads those values, so turning it on does nothing. Of the fades, only `BOXFADE` (`_BoxFadeDistance`) and `NORMALSFADE` are built. **disasm** (keyword and uniform names in the shader's program blobs).
- **Layers:** `TGBDecal.Layer.Enviro0` paints environment surfaces. Other values are `Background = -1`, `Enviro1`, `VFX = 10` and `Wetness = 20`.
- **IL2CPP performance:** under IL2CPP, every `UnityEngine.Vector3` / `Mathf` call from a mod crosses into native code. Per-texel loops written with them took seconds, so use `System.Numerics` and `MathF` in hot loops.
- **Collider meshes:** rock `MeshCollider` meshes are not CPU-readable (`isReadable = false`).

## Warping and camera streaming

- **Safe teleport:** `FreeRoamManager.Instance.WarpToPoint(FreeRoamWarpPoint wp, Disk.IOOperationCallback onDone)`. This is the game's map warp. It shows a loading screen, then `WarpRoutine` runs (**xref**):
  1. Revive Ava with `PawnManager.Respawn(RespawnMode.Revive)` if the game is over.
  2. Wait for `CairnSceneManager.IsBusy()` to clear.
  3. `PathRecorder.HaltPath()`.
  4. Put on or take off ice gear as the warp point's `setIceEquipment` says.
  5. `TransitionToMode(wp.WarpMode, wp.TeleportDestination, ...)`.
  6. `RobotPawnController.TeleportNearClimber`.
  7. Detach and remove the last securing piton from the `Lifeline`.
  8. `ResumePath()`, then save.

  `WarpToPoint` is `CairnSceneManager.ShowLoadingScreen`, then `WarpRoutine` (public), then `HideLoadingScreen`. The routine always ends with `SavegameManager.Save`, which shows the auto-save icon. Its other steps can be called directly, which is what the mod does for hops within the loaded zone:
  - `switcher.PathRecorder.HaltPath()` / `ResumePath()`.
  - `PawnManager.UnEquipAllIceClimbingDevices(true, true, true)`, or `EquipAllIceClimbigDevices(true, false)` when the point has `setIceEquipment` (see Ice gear above).
  - `climbingPawnController.associatedRobotPawn.TeleportNearClimber(true, true)`.
  - The revive. A hop is never made while the game is over: the freecam uses the warp then, so the routine's wait after the revive applies. The revive is only called directly when there's no `FreeRoamManager`.
  - Loop over `climbingPawnController.harness.Lifeline.GetLastSecuringPiton()`: `DetachPiton` if the piton's object is alive, `RemovePiton` otherwise.

  The warp passes the point's position but no rotation (`TransitionToMode(wp.warpMode, pos, null, true, None, true)`), so with `warpMode = Climbing` the climbing reset decides which way she faces.

  A bare `TransitionToMode` skips these steps. In testing it sometimes left Ava T-posed and unusable, and it does nothing useful while she's climbing.
- **Leaving Game Over:**
  - `GameState.GameOver` is pushed only by `GameOverMenu.OnOpening`. It is popped only by `GameOverMenu.PopGameOverState`, which runs when the menu closes (`OnClosing`, `OnDestroy`), and only if the menu was fully opened. **xref**
  - Closing the menu also restores `Time.timeScale` and unpauses time, pops the GameOver input context, stops the death music and brings sound back. **log** (`TGBTools.*.log` next to Player.log)
  - `PawnManager.Respawn(Revive)` only resets the pawn, stats, path recorder and Climbot. It doesn't touch the menu or the game state. **xref**
  - The death screen's retry (`RetryFromSave`, CanWarp branch) calls `WarpToPoint` then `Close(false)` on the menu. Its map's fast travel (`EagleEyeUI.OnReloadSaveInput`) calls `WarpToPoint` and nothing else, so the game stays over: no sound, death music, time stopped. **xref**
  - The mod closes the menu (`GameOverMenu.IsOpened`, then `Close(false)`) after its own revive warps, and after fast travel through a Harmony patch.
  - **Sound after a warp retry:**
    - `GameOverMenu.Retry` sets `SoundManager.BackToMainMenu` (renamed `MenuToLoading` in 1.1: same bool, offset 0x62, still set by `Retry`). Only `SoundManager.OnLevelLoading` clears it, and a CanWarp retry never loads a level. **xref**
    - While it's set, `SoundManager.OnChangeGameState` doesn't put Wwise `STATE_GAME` back to `INGAME`, so it stays `GAMEOVER` and sound effects are silent. Dying runs `PawnAudioHandler.TryPostGameOver`, which posts the game-over event and sets `STATE_GAME` to `GAMEOVER`. **xref**; that the state mutes sound effects is a **guess** from the soundbank side.
    - `MusicManager.OnChangeGameState` then stops the music. Nothing restarts it, because the zone's module is unchanged, and only `SetMusic` posts the music again (a level load does it with `SetMusic(NONE)`). **xref**
    - In free climb the mod restores all three when the menu closes in game: it clears the flag, calls `SoundManager.SetState(STATE_GAME.GROUP, STATE_GAME.STATE.INGAME)` and `MusicManager.SetMusic(STATE_MUSIC.STATE.NONE, false)`.
- **Warp points from code:**
  - A `FreeRoamWarpPoint` on an **inactive** GameObject works as a one-off destination. Its position and rotation come from the transform, and `warpMode` is Walking or Climbing.
  - Keep the object inactive, otherwise `Awake` registers it in the game's own warp list.
  - Destroy it in `onDone`.
- **Camera streaming:**
  - Zone streaming normally follows Ava (`UpdateStreamingGameplay`), and zones away from her show as `_LOD` scenes.
  - `StreamingManager.UpdateStreamingCameraEx()` loads the zone under `MainCamera` instead, through `LoadZoneSceneData(zone, false, forCamera: true)` and `CurrentCameraZone`. Nothing in the game calls it any more.
  - `StopCameraStreaming()` hands streaming back to Ava.
- **`NORMALSFADE`:**
  - The shader computes `d = dot(gbufferNormal, normalize(-boxLocalY))`. It discards pixels where `d < _NormalsFadeThreshold`, then fades in over `_NormalsFadeSmoothness` with a smoothstep.
  - Because the axis is `-boxLocalY`, a decal using the fade needs local **+Y pointing into the surface**. Pointing it outward discards everything.
  - The UV still comes from local X and Z, so read the u/v world directions back from the box rotation.

## Main menu and starting a game

Read from the interop assemblies (`Il2CppTheGameBakers.Cairn.Global.dll`) and by disassembly. The free-climb launch and the input notes below are confirmed in game.

- **Title screen:** `TheGameBakers.Cairn.UI.MainMenuModeSelectElement`, a `MainMenuElement`.
  - It has one `Button` field per entry: `storyModeButton`, `summitButton` (`summitModeButton` before 1.1; it reads "On the Trail", or a download prompt without the DLC: `RefreshSummitButton`, `HasAnySummitDLC`), `settingsButton`, `extrasButton`, `creditsButton`, `steamPageButton`, `quitButton`. Clicks are wired up in its `Awake`.
  - The base class gets the element's buttons from the virtual `GetButtons()` (`Button[]`) for navigation, the selection offset and colour, and the bouncing arrow. `Initialize()` records `basePositions` and `textMeshes` from it.
  - Button labels use `Il2Cpp.LocalizedText`, which rewrites the `TextMeshProUGUI` text.
  - The selection look lives on `MainMenuElement`: `colorWhenSelected` / `colorWhenNotSelected` (if `applySelectionColor`), `offsetXWhenSelected` (if `applySelectionOffset`), `selectTransitionDuration`, and its `group` (`CanvasGroup`). The arrow is `MainMenu.bouncingArrow` (`BouncingArrow`: `image`, `rect`, `activeColor`, `bouncingArrowXAmplitude`). Fonts are TGBTools `DynamicFont`s loaded through Addressables, so the mod copies `font` and `fontSharedMaterial` from a button's text instead of loading one.
- **Extras page:** `MainMenuExtraSelectElement` (step `ExtraSelect`), with `deluxePDFButton` (`Button`) and `tshirtButton` (`MainMenuButton`), which lead to the `DeluxePDF` and `TshirtMaker` steps. It overrides `GetButtons()`, and `NonGenericBackButton` is false. **sig**
  - The mod copies the Deluxe PDF button into a "Loading Screen Art" button below the last one and appends it to `GetButtons()`, the same way it adds Free Climb. **guess** until checked in game: that the page has no layout group, and that the PDF button shows for players without the Deluxe edition.
- **Loading screen art:** `TheGameBakers.Cairn.UI.LoadingScreen` draws the paintings shown while loading into `image`, cross-faded through `imageForCrossFade`. Templates: `Default`, `KamiIntro`, `AfterCredits`, and since 1.1 `WorldSpecific`.
  - **1.1:** every picture is an Addressables `AssetReference`, loaded when shown (`spriteOperationHandles`): `possibleSpritesReferences` (the paintings), `worldLoadingScreens` (`WorldSpecificLoadingScreen`: `world`, `character`, `sprite`, for the DLC crags), and `tenzenToKamiSprites` (`LocalizedSprite[]`, the story's "Next"-stepped sequence, template `KamiIntro`). A `LocalizedSprite` has `defaultSpriteReference`, `spritesByLanguage` and `Get()`, which returns the current language's `AssetReference`. **sig**
  - The mod's viewer lists all three, loads the picture on show and its neighbours with `Addressables.LoadAssetAsync<Sprite>(reference.RuntimeKey)`, its own handle rather than the reference's (the game's screen loads through those), and releases them on close. **game**: 11 paintings, 4 crag screens and 4 Tenzen to Kami pictures, found on the title screen with `Resources.FindObjectsOfTypeAll`. The viewer shows them as three sets, Story, On the Trail and Tenzen to Kami.
  - **data** (UnityPy on `loadingscreen_assets_all.bundle`, 1.1.593s): that's every loading screen the game has. Each has its own `loadingscreensprites_assets_*` bundle. The crag screens are Lamu (character `Invalid`, localised: the picture is a newspaper page), Haken (`Invalid`), Ruka (`Aava`) and Ruka (`Marco`). The `Lore_Crux_*` sprites in `cruxmodetextures_assets_all.bundle` are the DLC's lore pages and posters, not loading screens.
- **Screens:** `MainMenu.TransitionToStep(MainMenu.Step)`. Confirmed by disassembly (`GameAssembly.dll`, RVA `0x32A5F40`):
  - It returns without doing anything while `isAnimating` or `isDownloadingPreviews` is set, or while `demoPopUp` is showing.
  - It stores `EventSystem.current.currentSelectedGameObject` as `lastConfirmedElement`, so it **throws if `EventSystem.current` is null**. To block menu input, disable the event system's input modules, not the `EventSystem` itself.
  - Cpp2IL's stub DLLs under `MelonLoader/Dependencies/Il2CppAssemblyGenerator/Cpp2IL/cpp2il_out` have `[Address(RVA=…)]` on methods and `[FieldOffset]` on fields. With those, `objdump -d -M intel --start-address=0x180000000+RVA GameAssembly.dll` shows the real code.
- **Cancel (B / Esc):** `MainMenuElement.Cancel_performed` is an Input System action callback, so turning off UI input modules doesn't stop it. It calls the virtual `OnCancel` when the element is shown and `CanCancel` is true. On the title screen, `OnCancel` is `RequestTransitionTo(PressStart)`.
- **Step list:** `Step` is a fixed enum (`ModeSelect`, `StoryModeManageSave`, `SummitModeWorldSelect`, `SummitModeRouteSelect`, `SummitModeChallengeSelect`, `SummitModeRulesSelect`, `SummitModeConfirm`, …, `LaunchSummitGame` = 15, `LaunchSavedGame`, …, `SummitModeOnboarding`). `LaunchNewSummitGame` became `LaunchSummitGame` in 1.1.
- **Summit selection (1.1):** `MainMenuLaunchGameElement.Open(LaunchSummitGame)` calls `LaunchSummitMode(world, zone, setup, forceNewGame)` with its `summitMenu` field's (`MainMenuSummitSelectElement`) `SelectedWorld` (`WorldZoneData`), `SelectedZone` (`ZoneSceneData`) and `GameSetup`, and `MainMenu.nextGameStartOptions.forceSummitNewGame`. The old `SummitMenu` still exists but the launch doesn't read it. **xref**
  - Spawn points come from `ZoneSceneData.spawnersForMainMenu`; `GameSetup.spawner` names the one to use.
  - `WorldZoneData.IsSelectableForSummitMode` marks the worlds On the Trail offers: the Crux crags, not Kami (from each world's `WorldMeta` in `mainmenu_assets_all.bundle`). **asset**
- **Summit saves (1.1):** one per mountain. `LaunchSummitMode` switches to `SavegameManager.GetSlotForSummitSave(world)` (`ChangeActiveSlot(Summit, slot)`). Then:
  - with `forceSummitNewGame`, `IncrementSummitStatsAndDestroySave` (adding the select element's `Preview` stats) wipes the slot, and a fresh game loads (`CairnSceneManager.LoadZone`);
  - otherwise, if `HasAnySaveOfType(Summit)` and that slot holds a game, it **continues that save** instead of starting the given setup;
  - otherwise it loads fresh. **xref**
  - `HasAnySummitSaveForWorld(world, out slot)` tells which; slot n is `SUMMIT/SLOT_{'A'+n}.CPK` (`SlotInfo.Char`). `ChangeActiveSummitSlot` has no callers, so a trip to another mountain mid-game keeps saving to the launch mountain's slot. **xref**
- **Worlds (1.1):** `StreamingTweakables.World` adds `Crux_01_TowerGym` (in game "Lamu"), `Crux_02_DeepWaterPiton` ("Haken") and `Crux_03_Mallorca` ("Ruka"), the On the Trail - Deep Water crags, one zone each (zone name keys "Lamu", "Haken", "Ruka"). `Crux_04_Pika` … `Crux_07_Meschers` have only `WorldMeta`s (for a later DLC). `WorldZoneData.LocalizedName` is the in-game name. `PawnManager.Character` adds `Marco`. **asset/sig**
- **`GameSetup`:** `spawner`, `summitMode`, and `constraints`, a `GamemodeConstraints` flag set: `PermaDeath`, `NoStory`, `NoClimbot`, `MeaninglessDeath`, `NoBoosts`, `NoPhysiologicalNeeds`, `NoTutorials`, `CanWarp`, `NoAssistMode`.
  - `GetSaveType()` returns `SaveType.Story` or `SaveType.Summit`.
  - The running setup is `CairnSceneManager.Instance.GameSetup`.
- **Save slots:** saves are kept per type, under `Saved Games/TheGameBakers/Cairn_RETAIL/SAVEGAMES/RETAIL/{STORY,SUMMIT,DEV}/SLOT_*.CPK` in the Proton prefix.
- **Leaderboards:** `GameEventManager.Cheats()` raises `OnCheat`. That marks the run (`ClimbingStatistics.DeclareCheating`, `PathRecorder.IsCheating`), and scoreboard requests leave cheating records out by default (`ScoreboardRequest.includeCheatingRecords`).
- **Taping fingers:** `TapingFingersManager.Activate` shows the hands model (`models`, each a `TapingFingersModel` with a `PawnManager.Character character`) that matches the running `GameSetup.character`, and makes it `CurrentModel`. `TapingFingersInit` then throws if none matched. A `GameSetup` built from a spawner alone has `character = Invalid`, so the mod sets `Aava` when it launches free climb. **xref**; the fix is confirmed in game.
- **Free climb (this mod):**
  - The mod fills in the launch element's `summitMenu` with a `GameSetup(spawner)` that has `summitMode = true`, `character = Aava` and `NoStory | NoTutorials | NoPhysiologicalNeeds | CanWarp | MeaninglessDeath`, then calls `TransitionToStep(LaunchSummitGame)`.
  - **Saves (1.1):** free climb shares the mountain's summit slot with On the Trail. `FreeClimb/FreeClimbSaves.cs` remembers (in `UserData/CairnRouteMaker/FreeClimbSaves.txt`) which mountains' slots hold a free climb of ours: claimed on launch and every minute while playing, given up when the player's own summit game is seen there. A launch over a save that isn't ours (or whose file was written more than 5 minutes after our last claim) is refused with a warning; choosing the same mountain again within 15 s replaces it. Otherwise `forceSummitNewGame` is set only when a save is in the way, so a free climb never continues an On the Trail run.
  - **Gear-up (1.1):** On the Trail's "Pack your bag" screen is the loading screen's. `GameSetup.RequiresGearUp` just returns `summitMode`. `LoadingScreen.SetDisplayed(displayed, template)` clears `RequiresGearUp` (+0x8D) and `WaitForMe` (+0x8C), and only for the `WorldSpecific` template sets `RequiresGearUp` (unless some singleton's object at +0x80 has its byte at +0x10 set, which is probably a save being continued). It then sets `WaitForMe` and pushes an input context. `CurrentSubmitPrompt` is then `gearUpSubmitPrompt` ("Pack your bag") rather than `submitInputPrompt`, and `OnSubmit` opens `gearUpMenu` (`GearUpMenu`). **dis** For Kami that menu has no items set up and can't be left (seen in game). `Game/LoadingScreenPatches.cs` swaps `WorldSpecific` for `Default` in a free-climb launch's `SetDisplayed`, which gives the pre-1.1 painting and no gear-up. The launch sets a flag, because the new run's `GameSetup` isn't in place when the loading screen shows; the flag clears when that screen hides, or after 60 s if it never shows. The pause menu's "New run" reloads through the same `WorldSpecific` screen, so the patch also swaps the template while `GlobalGameManager.IsInGame` and the running game is free climb. **game**: Kami free climb and its New run load straight in. An On the Trail new run on Lamu, launched right after a free climb, still shows its newspaper screen and "Pack your bag".
  - A game is free climb only while the running setup has exactly those rules. Since 1.1 `summitMode` isn't part of the check, because free climb plays with it cleared (below).
  - **Health, not energy (1.1):** `summitMode` now means On the Trail. `GameSetup.UseEnergyInsteadOfHealth` and `RequiresGearUp` are the same folded getter, which returns `summitMode`, and `ArePhysiologicalNeedsDisabled` is `NoPhysiologicalNeeds || summitMode`. `GetCurrentGameSetup()` (it returns the running `CairnSceneManager.GameSetup` unless a debug override is set) has about 286 callers, which read the flag inline, e.g. `SurvivalManager.get_CurrentVital` picks `SurvivalState.Energy` or HP from it. **dis**
    - `Game/ClimbingModePatches.cs` clears `summitMode` on the running setup once Ava is in. `FreeClimbSession` reapplies it every 0.5 s, because a reload brings the saved setup back, and sets it again when the session ends. The game then climbs like a story game: health bar, stamina, no energy wager. **game** (1.1.610s): heart health bar, zone trip to Crag fine, pause menu is the story one (no New run).
    - The save code chooses the folder from the same flag (`GetSaveType` = `summitMode ? Summit(3) : Story(2)`, inlined). These `SavegameManager` methods call `GetCurrentGameSetup`: `ChangeActiveSlot(byte)`, `Delete(callback)`, `GetActiveSlot`, `EnsureActivePackageOpenedForWrite`, `Load()`, `Reload`, `QuickSave`, `Save`, `GlobalGaleManager_OnGameStart`, and the `ReadPathFragments` routine (read only). The patch runs every overload of these, plus `AutoSave` and `SaveAndClose`, with `summitMode` set again (a counted scope, ended in a finalizer). During free climb it also rewrites `SaveType.Story` to `Summit` in `Load(SaveType, …)`, `Delete(SaveType, …)` and `ChangeActiveSlot(SaveType, byte)`, the overloads that UI such as `GameOverMenu.RetryFromSave` calls with a channel it worked out from the flag. Overloads taking a `SlotInfo` are left alone. **game**: a quick save and quit from free climb wrote `SUMMIT/SLOT_X.CPK` (Kami's slot) and left every `STORY` slot byte-identical. Untested: death and retry.
    - `SpawnerMC.OnRestartLevel` only reads the setup's `spawner`, so zone trips work in either mode; `RespawnInZone` now checks for free climb instead of `summitMode`.
  - **Zone trips:** `CairnSceneManager.TravelToZone` loads the zone but doesn't place Ava. In a free-climb game she shows up in `01_FirstRidge` for a moment, then goes back to the setup's spawner, which is the zone the game started in. The zone load (`<LoadMountainZoneAndReleaseHandle>d__137.MoveNext`) calls `GameEventManager.RestartLevel`, and `SpawnerMC.OnRestartLevel` moves Ava to the loaded spawner whose name matches `GameSetup.GetCurrentGameSetup().spawner`. That reads `CairnSceneManager.GameSetup`.
    - `GameSetup` is a **struct** (`spawner` at offset 0, stored inline at `CairnSceneManager+0x80`). Reading the property from a mod gives a copy, so a change has to be assigned back.
    - `spawner` is the spawner's **name** (a string). The mod sets it to the destination's first `spawnersForMainMenu` entry before travelling.
    - Zones with no `spawnersForMainMenu` (for example `Tenzing_02_GymToMountain`) keep the old spawner, so a trip there ends back where the game started.
  - **World trips:** `CairnSceneManager.TravelToWorld(world)` loads the mountain's first zone (`01_FirstRidge` on Kami). The setup's spawner still names one on the old mountain, so no spawner matches and Ava never spawns (the log shows the zone change, then nothing). The mod first points the setup at the new mountain's first spawn point: `SpawnerMC_Kami_01_FirstRidge` or `SpawnerMC_CM_01_Main_Small` on Tenzing. **guess** that this zone is always the one `TravelToWorld` loads; it's true for both mountains' first zones.
  - **Stale spawn point names:** Ava spawns only at a loaded `SpawnerMC` whose object name matches `GameSetup.spawner`. Some `spawnersForMainMenu` names don't match: `26_LastBridge` lists `SpawnerMC_Kami_24_LastBridge`, but its object is `SpawnerMC_Kami_26_LastBridge`, and no `24_LastBridge` zone exists. With no match, Ava never spawns and the camera stays at the origin (inside `01_FirstRidge`), so every other zone shows only its `_LOD` scene. When a listed name points at no zone of the world, the mod uses `SpawnerMC_<world>_<zone>` instead.

## Remap controls screen

Read by disassembly (`GameAssembly.dll`; RVAs from the Cpp2IL stubs). The mod's use of it is untested in game.

- **Building the screen:** `TheGameBakers.Cairn.RebindSheet.Start` (RVA `0x30D1490`) runs once per sheet. It calls `FieldRebind<,>.ResetStatics()` for both field types, then walks `gamepadRebindList.rebinds` and `kbmRebindList.rebinds` (`RebindList` ScriptableObjects, shared by the title-screen and pause-menu sheets). For each `RebindData`:
  1. If `headerLabelLocKey` isn't the "none" key, it instantiates `headerPrefab` (a `LocalizedText`), calls `ChangeLocKey(header)` and parents it to the fields. Only the first row of each context has a header ("Context: …", keys `REBIND_CONTEXT_*`).
  2. It builds a `FieldGamepadRebind` / `FieldKbmRebind(rebind, sheet)`, adds it to `gamepadFields` / `kbmFields`, and adds the row to the scroll handler.
- **`RebindData`:** `labelLocKey`, `headerLabelLocKey`, `actionWrapper` (`InputActionReferenceWrapper`), `rebindContext`, `firstBindingId`, `secondBindingId`, `subActionWrappers`.
  - The field constructor loops over `subActionWrappers` without a null check, so it must be at least an empty array.
  - The row's label comes from `LocalizationManager.Get(labelLocKey)` (called in the `FieldRebind<,>` constructor and on language change). Headers and the rebind popup go through `LocalizedText.Refresh`, which also calls `Get`.
- **Finding the action:** `InputActionReferenceWrapper.InputAction` (RVA `0x2C52790` before 1.1) needs `InputManager` to be ready.
  - Before 1.1 it looked `reference.action` up in `InputManager.assetToInstanceCache` (`Dictionary<InputAction, InputAction>`) first, so an action from another asset worked if it was in the cache.
  - **1.1:** it calls `InputManager.GetReferenceAction`, which calls TGBTools' `InputManager.ResolveAction(reference.action)`. That looks up a `Resolver` by the action's asset name (`resolvers`, filled by `RegisterInstanceResolver(asset)`), and returns the action itself when there's none. So an action from an asset the game doesn't know resolves to itself. **xref**
- **Finding the binding:** `BaseFieldRebind.FindBinding(action, bindingId, wasAdded, out hasOverride, out forbiden, acceptedDeviceTypes)` (RVA `0x30BD7F0`):
  - It throws if `bindingId` is null.
  - It returns the first binding whose effective path contains one of the device type names (`Keyboard`/`Mouse` for kbm rows, `Gamepad` for gamepad rows) and whose id equals `bindingId`. An empty `bindingId` matches any id.
  - Kbm rows use only `firstBindingId`. With no match, a rebind adds a new binding (`WithRebindAddingNewBinding`).
- **Rebinding:** `FieldRebind<,>.Rebind` disables the action's map if it's enabled, runs `PerformInteractiveRebinding`, then re-enables the map. Resets call `RemoveBindingOverride`. Both end in `RebindSheet.OnRebind(field, action)`.
- **Clash checks:** `rebindContext` is a flag set (`RebindContextStringIdEnum`, game bits up to `1 << 18`).
  - `FieldRebind<,>.CheckFlags` loops over the static `contextEnums` array and calls back for each bit the row has (`(context & flag) != 0`).
  - `AddToDictionary` creates the per-context lists in `rebindsByContext` on first use. Extra bits work once they're appended to `contextEnums`.
- **Saving:** `ControlsSettingsPage.SaveData` stores `InputManager.GetRebindingsSave()`, which covers only the game's own actions. `ApplyRebindingsSave` loads them at startup.
- **What the mod does** (`Game/ControlsMenuPatches.cs`):
  - It appends rows for its own `InputActionAsset` (`Controls/ModControls.cs`) to both lists in a `RebindSheet.Start` prefix.
  - It adds context bits `1 << 24` (Freecam) and `1 << 25` (Route Editor). Before 1.1 it also put its actions in `assetToInstanceCache`; its asset ("CairnRouteMaker.Controls") has no resolver, so 1.1 needs nothing. Untested in game since the update.
  - It answers made-up loc keys in a `LocalizationManager.Get` prefix.
  - It saves its rebinds to `UserData/CairnRouteMaker/Controls.json` from a `RebindSheet.OnRebind` postfix.

## Settings menu

Read by disassembly and a hierarchy dump at runtime. The mod's Routes tab is confirmed in game, on the title screen and in the pause menu, and its page has been opened and closed from it without errors.

- **Layout:** `TheGameBakers.Cairn.UI.SettingsMenu` is at `MainMenu/Canvas/SETTINGS/SettingsMenu` on the title screen; the pause menu has its own. Its root has a `CanvasGroup` covering the whole screen: header, page buttons, rows and the Reset / Back prompts.
  - Under `Main/SettingsPageButtons` (a `VerticalLayoutGroup` with `BouncingArrowZone` and `BouncingButtons`) are the page buttons Gameplay, Controls, Language, Video and Audio.
  - Each button is a `ButtonWithMoreEvents` with explicit navigation (Audio's Down is empty), a `SettingsPageButton`, an `Image` (the highlight while its page is open) and an `Animator`.
  - The label is a child `TextMeshProUGUI` translated by a `TGBTools.UI.LocalizationUI`.
- **Page buttons:** `SettingsPageButton.Awake` sets `settingsPage.settingsMenuButton` to itself and hooks the button's click to `SettingsMenu.OpenSettingsPage(this)`. **xref**
- **Back and Reset:** these are input actions, not UI events. `SettingsMenu.ToggleActions(bool)` hooks or unhooks `BaseUIActions.Cancel` (`OnCancel`, which runs `Return`) and `Special` (`OnSpecial`, Reset). `OnOpened` turns them on and `OnClosing` turns them off. **xref**; with no page open, `Return` makes a virtual call that presumably closes Settings (**guess**).
- **Selection effects:**
  - `BouncingArrowZone.Awake` lists the `Selectable`s under it in `childs`, for the bouncing arrow.
  - `BouncingButtons.ButtonsData` is built the first time it's asked for. It maps each `ButtonWithMoreEvents` under it to a `ButtonData(label)` and hooks `OnPointerEntered` to `SelectOnPointerEnter`. `Toggle(interactable)` turns all of them on or off.
  - `SettingsMenu.Awake` calls `Toggle(false)`, which builds that list, and can run before the child `BouncingArrowZone.Awake`. **xref**; the order is confirmed in game.
- **What the mod does** (`Game/SettingsMenuPatches.cs`, `Editor/PaintColorsMenu.cs`):
  - In a `BouncingArrowZone.Awake` prefix it copies the Audio button below it, removes the copy's `LocalizationUI` and links it into the explicit navigation.
  - The copy's `Awake` points Audio's page at the copy, so the mod points it back.
  - It replaces the copy's click, so the copy never opens a settings page.
  - If `BouncingButtons.buttonsData` already exists, the mod adds the copy to it. Otherwise the copy, made from a button that's off, would never be turned on.
  - The copy's click opens the mod's Routes page. While it's open, the root `CanvasGroup`'s alpha is 0, `ToggleActions(false)` stops Back and Reset, and `GameApi.BlockMenuInput` stops the rest.

## Video settings page

Read from the interop assemblies, and confirmed in game with a route paint distance row the mod used to add here. That setting is on the mod's Routes page now (see [Settings menu](#settings-menu)).

- **Rows are fields built in code.** `VideoSettingsPage` keeps its rows as TGBTools.UI fields: `FieldListArrows` for the quality rows (`QualityField`, `EnviroQualityField`, `LightQualityField`, `RockQualityField`), plus `FieldToggle` and `FieldSlider`. The menu's shared `FieldsUI.Add(Field)` makes a row for a field from the template for its type and appends it.
- **`FieldListArrows(FieldLabel label, FieldLabel[] values, int index, OnValueChangedDelegate onValueChanged, int? defaultIndex)`.** A `FieldLabel` is a loc key or a plain string. Left and right wrap around at the ends. The Settings Reset prompt (R on keyboard) sets the field to `defaultIndex` and calls `onValueChanged`, with no confirmation.
- **Description line:** the text under the rows comes from the page's `infos` (`SettingsPage.Info`: `field`, `fieldUI`, `fieldLocKey`, `infoLocKey`). A field that isn't in it shows a blank description.
- **Back:** Escape doesn't leave a settings page; the on-screen Back control does.
