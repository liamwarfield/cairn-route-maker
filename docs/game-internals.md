# Cairn game internals

Notes on the game code this mod depends on. They were found by reading the IL2CPP interop assemblies MelonLoader generates (`MelonLoader/Il2CppAssemblies/`), the game's call-graph cache, and the [CairnMP](https://github.com/CairnMP/cairnmp-mod) source (AGPL-3.0, read for names only).

Checked against Cairn Steam build 23683671, Unity 6000.0.59f2, MelonLoader 0.7.3.

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
  - Passing `Mode.Climbing` may place her straight onto a wall, which would give us wall starts later. **guess**
- Full sequence in `FreeRoamManager.WarpRoutine` (**xref**):
  1. Wait while `CairnSceneManager.IsBusy()`.
  2. `PathRecorder.HaltPath()`.
  3. Equip or unequip ice gear as the warp point says (see "Ice gear").
  4. `TransitionToMode`.
  5. `RobotPawnController.TeleportNearClimber`.
  6. Clean up pitons via `Lifeline`.
  7. `PathRecorder.ResumePath()`.
  8. Save.
- CairnMP's approach (**mp**):
  1. Teleport only while Walking.
  2. If the target is in another zone (`StreamingManager.Instance.GetBestZoneAt(pos)` differs from `CurrentZone`), call `CairnSceneManager.Instance.TravelToZone`, then wait for loading to finish.
  3. Write the transform directly and repeat until Ava stays put.

## Survival stats and fingers (restoring Ava)

- `SurvivalStat` enum: `Hunger`, `Thirst`, `Cold`, `Hp`, `Exhaustion`. **sig**
- `GameDataManager.Instance` has `RefillStat(stat)`, `GetStat(stat)`, `SetCurrentHp(value)`, `CurrentMaxHp` and `AbsoluteMaxHp`. Max HP can be eroded below the absolute max. **sig**; whether `RefillStat(Exhaustion)` clears exhaustion is a **guess** (the mod logs the values before and after).
- `GameDataManager.GameData.climberData` (`ClimberData`):
  - `fingerHP01` (one 0-1 value per finger) and `SetFingerHealth01(fingerIndex, health01, ignoreCooldown, useTapingMultiplier)`.
  - `EndCriticalStatTimer(stat)` stops a stat's pass-out countdown. **sig**
- Climbing stamina is per limb: `ClimbingV2PawnLimb.ResetStamina()`. **sig**
- `GameDataManager.ResetSurvivalValues()` / `ResetStats()` also exist. They aren't used because what else they reset is unknown.

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
  - `CurrentState` (`LimbState`, where `Holding = 2`).
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
  - To block the game: `disableInputs = true` + `SetIgnoreInputEvents(true)`. To restore: `SetIgnoreInputEvents(false)`, `EnableInputs()`, `UpdateInputContext()`. **sig/mp**

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
- **Decals added from code need their texture transform set on the material.** Their `MaterialPropertyBlock` stays empty (even after `UpdateMPB()`), so `_TilingOffset` and `_MainTex_ST` come from the material:
  - Set `_TilingOffset = (1, 1, 0, 0)`.
  - Reset `mainTextureScale` and `mainTextureOffset`.

  Materials borrowed from the game's tape decals have `_MainTex_ST = (-0.5, 1, 0, 0)`. With clamped sampling, that pins every pixel to one texel column.
- **Layers:** `TGBDecal.Layer.Enviro0` paints environment surfaces. Other values are `Background = -1`, `Enviro1`, `VFX = 10` and `Wetness = 20`.
- **IL2CPP performance:** under IL2CPP, every `UnityEngine.Vector3` / `Mathf` call from a mod crosses into native code. Per-texel loops written with them took seconds, so use `System.Numerics` and `MathF` in hot loops.
- **Collider meshes:** rock `MeshCollider` meshes are not CPU-readable (`isReadable = false`).

## Warping and camera streaming

- **Safe teleport:** `FreeRoamManager.Instance.WarpToPoint(FreeRoamWarpPoint wp, Disk.IOOperationCallback onDone)`. This is the game's map warp. It shows a loading screen, then `WarpRoutine` runs (**xref**):
  1. Wait for `CairnSceneManager.IsBusy()` to clear.
  2. `PathRecorder.HaltPath()`.
  3. Put on or take off ice gear as the warp point's `setIceEquipment` says.
  4. `TransitionToMode(wp.WarpMode, wp.TeleportDestination, ...)`.
  5. `RobotPawnController.TeleportNearClimber`.
  6. Detach and remove the last securing piton from the `Lifeline`.
  7. `PawnManager.Respawn` when needed.
  8. `ResumePath()`, then save.

  A bare `TransitionToMode` skips these steps. In testing it sometimes left Ava T-posed and unusable, and it does nothing useful while she's climbing.
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
  - It has one `Button` field per entry: `storyModeButton`, `summitModeButton`, `settingsButton`, `extrasButton`, `creditsButton`, `steamPageButton`, `quitButton`. Clicks are wired up in its `Awake`.
  - The base class gets the element's buttons from the virtual `GetButtons()` (`Button[]`) for navigation, the selection offset and colour, and the bouncing arrow. `Initialize()` records `basePositions` and `textMeshes` from it.
  - Button labels use `Il2Cpp.LocalizedText`, which rewrites the `TextMeshProUGUI` text.
  - The selection look lives on `MainMenuElement`: `colorWhenSelected` / `colorWhenNotSelected` (if `applySelectionColor`), `offsetXWhenSelected` (if `applySelectionOffset`), `selectTransitionDuration`, and its `group` (`CanvasGroup`). The arrow is `MainMenu.bouncingArrow` (`BouncingArrow`: `image`, `rect`, `activeColor`, `bouncingArrowXAmplitude`). Fonts are TGBTools `DynamicFont`s loaded through Addressables, so the mod copies `font` and `fontSharedMaterial` from a button's text instead of loading one.
- **Screens:** `MainMenu.TransitionToStep(MainMenu.Step)`. Confirmed by disassembly (`GameAssembly.dll`, RVA `0x32A5F40`):
  - It returns without doing anything while `isAnimating` or `isDownloadingPreviews` is set, or while `demoPopUp` is showing.
  - It stores `EventSystem.current.currentSelectedGameObject` as `lastConfirmedElement`, so it **throws if `EventSystem.current` is null**. To block menu input, disable the event system's input modules, not the `EventSystem` itself.
  - Cpp2IL's stub DLLs under `MelonLoader/Dependencies/Il2CppAssemblyGenerator/Cpp2IL/cpp2il_out` have `[Address(RVA=…)]` on methods and `[FieldOffset]` on fields. With those, `objdump -d -M intel --start-address=0x180000000+RVA GameAssembly.dll` shows the real code.
- **Cancel (B / Esc):** `MainMenuElement.Cancel_performed` is an Input System action callback, so turning off UI input modules doesn't stop it. It calls the virtual `OnCancel` when the element is shown and `CanCancel` is true. On the title screen, `OnCancel` is `RequestTransitionTo(PressStart)`.
- **Step list:** `Step` is a fixed enum (`ModeSelect`, `StoryModeManageSave`, `SummitModeWorldSelect` … `SummitModeConfirm`, `LaunchNewSummitGame`, `LaunchSavedGame`, …).
- **Summit selection:** `SummitMenu` holds `SelectedWorld` (`WorldZoneData`), `SelectedZone` (`ZoneSceneData`), `SelectedSpawner` (`ZoneSceneData.SpawnerInfo`) and `GameSetup`.
  - Spawn points come from `ZoneSceneData.spawnersForMainMenu`.
  - `WorldZoneData.IsSelectableForSummitMode` marks the worlds Summit offers.
- **`GameSetup`:** `spawner`, `summitMode`, and `constraints`, a `GamemodeConstraints` flag set: `PermaDeath`, `NoStory`, `NoClimbot`, `MeaninglessDeath`, `NoBoosts`, `NoPhysiologicalNeeds`, `NoTutorials`, `CanWarp`, `NoAssistMode`.
  - `GetSaveType()` returns `SaveType.Story` or `SaveType.Summit`.
  - The running setup is `CairnSceneManager.Instance.GameSetup`.
- **Save slots:** saves are kept per type, under `Saved Games/TheGameBakers/Cairn_RETAIL/SAVEGAMES/RETAIL/{STORY,SUMMIT,DEV}/SLOT_*.CPK` in the Proton prefix.
- **Leaderboards:** `GameEventManager.Cheats()` raises `OnCheat`. That marks the run (`ClimbingStatistics.DeclareCheating`, `PathRecorder.IsCheating`), and scoreboard requests leave cheating records out by default (`ScoreboardRequest.includeCheatingRecords`).
- **Free climb (this mod):**
  - The mod fills in `SummitMenu` with a `GameSetup(spawner)` that has `summitMode = true` and `NoStory | NoTutorials | NoPhysiologicalNeeds | CanWarp | MeaninglessDeath`, then calls `TransitionToStep(LaunchNewSummitGame)`.
  - A game is free climb only while the running setup has exactly those rules.
  - **Zone trips:** `CairnSceneManager.TravelToZone` loads the zone but doesn't place Ava. In a free-climb game she shows up in `01_FirstRidge` for a moment, then goes back to the setup's spawner, which is the zone the game started in. The zone load (`<LoadMountainZoneAndReleaseHandle>d__137.MoveNext`) calls `GameEventManager.RestartLevel`, and `SpawnerMC.OnRestartLevel` moves Ava to the loaded spawner whose name matches `GameSetup.GetCurrentGameSetup().spawner`. That reads `CairnSceneManager.GameSetup`.
    - `GameSetup` is a **struct** (`spawner` at offset 0, stored inline at `CairnSceneManager+0x80`). Reading the property from a mod gives a copy, so a change has to be assigned back.
    - `spawner` is the spawner's **name** (a string). The mod sets it to the destination's first `spawnersForMainMenu` entry before travelling.
  - **Stale spawn point names:** Ava spawns only at a loaded `SpawnerMC` whose object name matches `GameSetup.spawner`. Some `spawnersForMainMenu` names don't match: `26_LastBridge` lists `SpawnerMC_Kami_24_LastBridge`, but its object is `SpawnerMC_Kami_26_LastBridge`, and no `24_LastBridge` zone exists. With no match, Ava never spawns and the camera stays at the origin (inside `01_FirstRidge`), so every other zone shows only its `_LOD` scene. When a listed name points at no zone of the world, the mod uses `SpawnerMC_<world>_<zone>` instead.

## Remap controls screen

Read by disassembly (`GameAssembly.dll`; RVAs from the Cpp2IL stubs). The mod's use of it is untested in game.

- **Building the screen:** `TheGameBakers.Cairn.RebindSheet.Start` (RVA `0x30D1490`) runs once per sheet. It calls `FieldRebind<,>.ResetStatics()` for both field types, then walks `gamepadRebindList.rebinds` and `kbmRebindList.rebinds` (`RebindList` ScriptableObjects, shared by the title-screen and pause-menu sheets). For each `RebindData`:
  1. If `headerLabelLocKey` isn't the "none" key, it instantiates `headerPrefab` (a `LocalizedText`), calls `ChangeLocKey(header)` and parents it to the fields. Only the first row of each context has a header ("Context: …", keys `REBIND_CONTEXT_*`).
  2. It builds a `FieldGamepadRebind` / `FieldKbmRebind(rebind, sheet)`, adds it to `gamepadFields` / `kbmFields`, and adds the row to the scroll handler.
- **`RebindData`:** `labelLocKey`, `headerLabelLocKey`, `actionWrapper` (`InputActionReferenceWrapper`), `rebindContext`, `firstBindingId`, `secondBindingId`, `subActionWrappers`.
  - The field constructor loops over `subActionWrappers` without a null check, so it must be at least an empty array.
  - The row's label comes from `LocalizationManager.Get(labelLocKey)` (called in the `FieldRebind<,>` constructor and on language change). Headers and the rebind popup go through `LocalizedText.Refresh`, which also calls `Get`.
- **Finding the action:** `InputActionReferenceWrapper.InputAction` (RVA `0x2C52790`) needs `InputManager` to be ready. It looks `reference.action` up in `InputManager.assetToInstanceCache` (`Dictionary<InputAction, InputAction>`) first. On a miss it searches `InputManager.InputActions.asset` by map id and action id and caches the result, or returns null if nothing matches. So an action from another asset works if it's in the cache.
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
  - It puts its actions in `assetToInstanceCache`, mapped to themselves, and adds context bits `1 << 24` (Freecam) and `1 << 25` (Route Editor).
  - It answers made-up loc keys in a `LocalizationManager.Get` prefix.
  - It saves its rebinds to `UserData/CairnRouteMaker/Controls.json` from a `RebindSheet.OnRebind` postfix.
