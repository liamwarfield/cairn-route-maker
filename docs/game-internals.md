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
    - `GameOverMenu.Retry` sets `SoundManager.BackToMainMenu`. Only `SoundManager.OnLevelLoading` clears it, and a CanWarp retry never loads a level. **xref**
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
  - It has one `Button` field per entry: `storyModeButton`, `summitModeButton`, `settingsButton`, `extrasButton`, `creditsButton`, `steamPageButton`, `quitButton`. Clicks are wired up in its `Awake`.
  - The base class gets the element's buttons from the virtual `GetButtons()` (`Button[]`) for navigation, the selection offset and colour, and the bouncing arrow. `Initialize()` records `basePositions` and `textMeshes` from it.
  - Button labels use `Il2Cpp.LocalizedText`, which rewrites the `TextMeshProUGUI` text.
  - The selection look lives on `MainMenuElement`: `colorWhenSelected` / `colorWhenNotSelected` (if `applySelectionColor`), `offsetXWhenSelected` (if `applySelectionOffset`), `selectTransitionDuration`, and its `group` (`CanvasGroup`). The arrow is `MainMenu.bouncingArrow` (`BouncingArrow`: `image`, `rect`, `activeColor`, `bouncingArrowXAmplitude`). Fonts are TGBTools `DynamicFont`s loaded through Addressables, so the mod copies `font` and `fontSharedMaterial` from a button's text instead of loading one.
- **Extras page:** `MainMenuExtraSelectElement` (step `ExtraSelect`), with `deluxePDFButton` (`Button`) and `tshirtButton` (`MainMenuButton`), which lead to the `DeluxePDF` and `TshirtMaker` steps. It overrides `GetButtons()`, and `NonGenericBackButton` is false. **sig**
  - The mod copies the Deluxe PDF button into a "Loading Screen Art" button below the last one and appends it to `GetButtons()`, the same way it adds Free Climb. **guess** until checked in game: that the page has no layout group, and that the PDF button shows for players without the Deluxe edition.
- **Loading screen art:** `TheGameBakers.Cairn.UI.LoadingScreen` has `possibleSprites` (`Sprite[]`), the paintings shown while loading, drawn into `image` and cross-faded through `imageForCrossFade`. `tenzenToKamiSprites` (`LocalizedSprite[]`) is a separate story sequence (template `KamiIntro`, stepped with a "Next" prompt). Each `LocalizedSprite` (a ScriptableObject) has `defaultSprite`, `spritesByLanguage` and `Get()`, which picks the current language's picture. Templates: `Default`, `KamiIntro`, `AfterCredits`. The mod's viewer shows `possibleSprites`, then `tenzenToKamiSprites` through `Get()`. **sig**; that the `LoadingScreen` is loaded on the title screen (the mod finds it with `Resources.FindObjectsOfTypeAll`) is a **guess**.
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
