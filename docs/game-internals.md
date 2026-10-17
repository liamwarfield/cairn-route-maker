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
  3. `PawnManager.UnEquipAllIceClimbingDevices`.
  4. `TransitionToMode`.
  5. `RobotPawnController.TeleportNearClimber`.
  6. Clean up pitons via `Lifeline`.
  7. `PathRecorder.ResumePath()`.
  8. Save.
- CairnMP's approach (**mp**):
  1. Teleport only while Walking.
  2. If the target is in another zone (`StreamingManager.Instance.GetBestZoneAt(pos)` differs from `CurrentZone`), call `CairnSceneManager.Instance.TravelToZone`, then wait for loading to finish.
  3. Write the transform directly and repeat until Ava stays put.

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
