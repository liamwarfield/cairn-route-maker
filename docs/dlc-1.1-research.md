# Cairn 1.1 (On the Trail – Deep Water): research notes

What changed in the game, what broke in the mod, what the `dlc-update` branch does about it, and the
decisions still open. Written 2026-10-30 after a research pass. The exact offsets, disassembly and
evidence are in [game-internals.md](game-internals.md), in the paragraphs marked **1.1**.

This work lives on the `dlc-update` branch, based on `main` at 7061246. It was released as 0.4.1
(tag `v0.4.1`) so the mod works with the DLC now, but it isn't merged into `main`: it isn't
necessarily the long-term direction.

## Game versions

| | Before | After |
|---|---|---|
| Game | Steam build 23683671 | 1.1.593s_2337 (29 Oct), then 1.1.610s_2373 (30 Oct, a patch) |
| Unity | 6000.0.59f2 | 6000.0.72f1 |
| MelonLoader | 0.7.3 | 0.7.3, which regenerated the Il2Cpp assemblies on first launch after each update |

The branch builds against the 1.1.610s assemblies. The research was done on 1.1.593s; the patch
build passed the same in-game checks.

## The big change: `summitMode` now means On the Trail

Free climb has always launched as a Summit game (`GameSetup.summitMode = true` plus the free-climb
constraints), because that gave it a save slot of its own and story-free play. In 1.1 that flag
switches on all of On the Trail's rules at once:

- **Energy instead of health.** `GameSetup.UseEnergyInsteadOfHealth` returns `summitMode`. The game
  reads it inline in about 280 places (survival, damage, HUD, game over, rewind), so it can't be
  patched in one spot.
- **Gearing up.** `RequiresGearUp` also returns `summitMode`. The loading screen waits on "Pack your
  bag", which opens the bag menu.
- **Needs off.** `ArePhysiologicalNeedsDisabled` is `NoPhysiologicalNeeds || summitMode`, so a route
  loadout can't switch hunger, thirst and cold on in a summit game.
- **Saves.** `GetSaveType` is `summitMode ? Summit : Story`, and the save code reads the flag itself.
- **The pause menu** gains "New run" (a restart through the gear-up screen).

Kami's own Summit mode is gone in 1.1: Kami's `isSelectableForSummitMode` is 0, and On the Trail only
offers the DLC crags. So free climb on Kami now rests on a combination the game no longer uses. That's
why the packing screen was empty and couldn't be left there.

## Other game changes that touched the mod

| Area | Before 1.1 | 1.1 |
|---|---|---|
| Survival stats | `GameDataManager` / `GameData.climberData` | `SurvivalManager` (`survivalState`, `fingersState`). `ModifyStat` has 3 arguments. |
| End-game flag | `GameDataManager.InEndGame` / `GameData.SetIsInEndGame` | `StoryEventManager.InEndGame` / `SetIsInEndGame` (still a plain flag) |
| Title-menu button | `MainMenuModeSelectElement.summitModeButton` | `summitButton`, labelled "On the Trail" |
| Summit launch step | `MainMenu.Step.LaunchNewSummitGame`, reads `SummitMenu` | `LaunchSummitGame`, reads `MainMenuLaunchGameElement.summitMenu`. `SummitMenu` still exists but nothing reads it. |
| Summit saves | one summit save | one per mountain (`SUMMIT/SLOT_{A+n}.CPK`, `GetSlotForSummitSave(world)`); `nextGameStartOptions.forceSummitNewGame` picks between continuing it and wiping it |
| Input | `InputManager.disableInputs`, `assetToInstanceCache` | `disabledInputs`. The cache is gone: TGBTools' `ResolveAction` hands back actions it has no resolver for unchanged, so the mod's actions need no registering. |
| Sound | `SoundManager.BackToMainMenu` | `MenuToLoading` (same bool) |
| Loading-screen art | `possibleSprites` (`Sprite[]`) | `possibleSpritesReferences` (Addressables), plus new `worldLoadingScreens` (per crag and character) and `LocalizedSprite` references |
| Worlds | Kami, Tenzen, … | adds Lamu (`Crux_01_TowerGym`), Haken (`Crux_02_DeepWaterPiton`), Ruka (`Crux_03_Mallorca`). Pika (`Crux_04_Pika`) and Crux 05–07 have world metadata but no zones yet: a later DLC. |
| Characters | Aava | adds Marco (`PawnManager.Character.Marco`); Ruka has a loading screen for each |

## What the branch fixes

| Area | Fix | Files | Checked in game? |
|---|---|---|---|
| Build errors | Every rename and removal in the table above | several | Builds against both 1.1 builds |
| Title menu | Free Climb is back under Story | `MainMenuPatches.cs` | Yes |
| Free-climb menu | Lists every installed world with zones, so Lamu, Haken and Ruka appear, and Pika will appear when it ships. Crag, zone and spawn-point names are tidied (Tower Gym, Deep Water, Mallorca, "Needle 2"). | `GameApi.Worlds`, `ZoneNames.cs`, `LevelSelect.cs`, tests | Menu and names yes. Launching on the DLC crags: you reported it works. |
| Launch | Fills in `summitMenu` and uses `LaunchSummitGame` | `GameApi.TryLaunchFreeClimb` | Yes (Kami) |
| On the Trail saves | Free climb shares the mountain's summit slot with your On the Trail run. `FreeClimbSaves.cs` remembers which slots hold a free climb, refuses to launch over one of your runs, and replaces it if you choose that mountain again within 15 s. | `FreeClimbSaves.cs`, launcher, session | No; see open questions |
| Gear-up screen | A free-climb launch, or New run inside free climb, gets the `Default` loading screen instead of `WorldSpecific`, so no "Pack your bag" | `LoadingScreenPatches.cs` | Yes. On the Trail itself still gears up (checked on Lamu). |
| Health, not energy | Once Ava is in, the mod clears `summitMode` on the running game, so free climb plays like a story game: health, stamina, needs controlled by the route. The save code is then kept pointed at the summit slot (next section). | `ClimbingModePatches.cs`, `FreeClimbSession.cs`, `GameApi.IsFreeClimbGame` | Yes, on Kami: heart health bar, zone trip, quick save |
| Route loadouts | Moved from `GameDataManager` to `SurvivalManager` and `StoryEventManager` | `AvaLoadout.cs` | No |
| Controls remap | The rows reach their actions without the old cache | `ControlsMenuPatches.cs` | No |
| Loading-art viewer | Loads pictures on demand through Addressables and releases them on close. Shows three sets: Story (11), On the Trail (4: Lamu, Haken, Ruka with Aava, Ruka with Marco) and Tenzen to Kami (4). A row of set names runs along the bottom, Up/Down or LB/RB jump between sets, and "Tenzing" is now spelt "Tenzen" like the game. | `LoadingArtGallery.cs` | Yes |

### How the health fix keeps saves safe

With `summitMode` cleared, the game would save a free climb as a **story** game, into your active
story slot. The patch prevents that in two ways:

1. **Summit scopes.** These `SavegameManager` methods read the running setup to pick Story or Summit,
   and the patch sets `summitMode` again while each one runs: `ChangeActiveSlot`, `Delete`,
   `GetActiveSlot`, `EnsureActivePackageOpenedForWrite`, `Load`, `Reload`, `QuickSave`, `Save`,
   `AutoSave`, `SaveAndClose` and `GlobalGaleManager_OnGameStart`. I found them by scanning the binary
   for callers of `GetCurrentGameSetup`.
2. **Story firewall.** During free climb, a `Load`, `Delete` or `ChangeActiveSlot` that names
   `SaveType.Story` outright is sent to `Summit` instead. The game-over and map screens work out a
   channel from the flag and pass it in like this.

Checked: a quick save and quit from free climb on Kami wrote only `SUMMIT/SLOT_X.CPK`, and every
story slot was byte-identical before and after. **Not checked:** dying and retrying, which is the
path most likely to load a save.

Side effects of health mode:

- The pause menu is now the story one: no "New run".
- A save made during free climb may record `summitMode = false` in its setup.

## Open questions and risks

- **Death and retry in health mode** haven't been tried. If anything touches a story save, it'll be
  here.
- **Health mode on the DLC crags** hasn't been tried. Lamu, Haken and Ruka were built for energy
  climbing (Haken is deep-water soloing), so falls and routes may play differently from what the
  designers intended.
- **On the Trail stats.** A free-climb launch over an existing slot calls
  `IncrementSummitStatsAndDestroySave`, which by its name adds to that crag's On the Trail stats.
  Lamu showed "Runs: 2" after some free climbs there, but I didn't confirm where they came from. Whether free climbs also count towards the crag's objectives
  or trophies is unknown.
- **Kami's summit slot** (`SLOT_X`) doesn't seem to be seen by `HasAnySummitSaveForWorld(Kami)`: the
  launch never logged replacing it, even with the file there. So the save guard never fires for
  Kami. That's harmless, since nobody else uses that slot now.
- **The free-climb save guard is untested:** refusing to launch, replacing within 15 s, and releasing
  a mountain once you've played your own run there.
- **What's in Ava's bag** when free climb skips gearing up wasn't checked. Before 1.1 it came from
  the setup's survival and climbing packages.
- **The patch analysis was done on 1.1.593s.** The 1.1.610s build passed the same checks, but
  `ClimbingModePatches` finds methods by name, so a later update that renames or adds save methods
  should be checked again.
- **Locked crags:** free climb ignores On the Trail's unlocks. Ruka is listed even if you haven't
  unlocked it by exploring Haken.

## Decisions for you

How much of the DLC to bring into the mod. The options for each area:

1. **Climbing rules in free climb**
   - *Original (what the branch does now):* health and stamina everywhere. It's closest to the
     pre-DLC mod, but needs the save patches above and changes how the DLC crags feel.
   - *Energy:* On the Trail's rules, the game's default for a summit game. No save patches needed,
     but Kami plays differently from before and route loadouts lose needs and health control.
   - *Per mountain or per route:* original rules on Kami and Tenzen, and energy (or a route setting)
     on the crags. The mechanism exists; it would be a choice at launch.
2. **Gear-up**: keep skipping it (now), or allow it on the DLC crags, where it works, perhaps as part
   of a route's loadout.
3. **Where free climb saves**
   - *Shared summit slot with a guard (now):* resumable, but tangled with your On the Trail runs and
     stats.
   - *Never save:* free climb always starts fresh anyway. It would remove all slot conflicts, but
     every write path has to be blocked, and quick save and quit would need a replacement.
   - *A dedicated slot:* clean separation, but needs the story or summit menus to ignore it.
4. **Characters:** free climb always uses Aava; it has to set a character, or finger taping breaks.
   Marco could be offered, at least on Ruka.
5. **New worlds:** the free-climb menu picks up new worlds automatically. Do you want them all, or a
   curated list with your own names?
6. **Loading-art viewer:** keep the On the Trail set, or show Story only, as before.

## Testing notes

- **Real controllers reach the fake screen.** Wine's gamepad driver is system-wide, so your
  controller drove a fake-screen test game while you played something else. It started a story game
  and autosaved over story slot C, which I restored from a backup. Before a fake-screen test, ask
  about controllers, or launch with physical pads ignored (not built yet).
- **Back up and checksum.** Every test here backed up `Cairn_RETAIL` first and compared `md5sum`s
  afterwards. Backups are in `UserData/CairnRouteMaker-backups/`:
  - `before-dlc-update-20261029-2028`: the v0.3.0 DLL, routes, saves and the accidental story slots
  - `before-gearup-test-20261029-2204`
  - `before-health-test-20261030-0856`
- **Steam updates silently.** 1.1.610s arrived overnight, between test sessions. Check the version
  in the corner of the title screen before trusting earlier disassembly.
- **Research tools are in `/tmp/dlc-api`:** the interop API dump, an RVA map, a symbolised
  disassembler and UnityPy scripts. `/tmp` won't survive a reboot, and the RVA map is for 1.1.593s.
