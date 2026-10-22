---
name: fake-screen
description: Run Cairn on an offscreen 1920x1080 display with a virtual keyboard and mouse, so you can launch, screenshot, and drive the game while the user's desktop is locked or in use. Use this whenever you need to run the game to test a change, instead of `steam steam://rungameid/1588550`.
---

# Fake screen for running Cairn

`steam steam://rungameid/1588550` puts the game on the user's real KDE desktop. When the desktop is locked, KDE stops drawing windows behind the lock screen and sends all input to the lock screen, so the game can't be watched or driven. When the user is at the desktop, it takes over their screen.

`fake-screen` (in this folder) avoids both. It starts a headless sway compositor with its own Xwayland: an offscreen 1920x1080 "monitor" rendered on the NVIDIA GPU, not connected to any real display or input device. It launches the game into it straight through Proton, using the Steam Linux Runtime and Proton build from Steam's last launch of the game, plus `WINEDLLOVERRIDES="version=n,b"` for MelonLoader. Steam must be running, as it normally is. The Steam API works, but there's no Steam overlay and the Steam client won't show the game as running.

## Testing a change in game

```sh
S=.claude/skills/fake-screen/fake-screen    # from the repo root (worktrees included)

# 1. Build and deploy. Stop the game first; MelonLoader only loads the DLL at startup.
#    Check you're not deploying something older than what's installed (see below).
$S stop
dotnet build -c Release 2>&1 | grep -E "error|Deployed"

# 2. Launch. Returns once the title screen shows "Press any key" (~30 s).
$S game

# 3. Drive it, screenshotting between steps.
$S key Return                    # "Press any key" -> main menu, "Story" selected
$S key Down; $S key Return       # Free Climb -> the mod's level select
$S shot /tmp/cairn-shot.png 50%  # then Read the PNG

# 4. Check the log.
grep -nE "Cairn Route Maker|ERROR|Exception" ~/.local/share/Steam/steamapps/common/Cairn/MelonLoader/Latest.log | tail -30

# 5. Tear down when done, so the game isn't left running on the GPU.
$S stop
```

The build deploys `CairnRouteMaker.dll` into the game's `Mods` folder, which every worktree shares. Before building, run `git fetch` and make sure your branch isn't missing work that's already on `origin/main`. Otherwise you can replace a newer installed DLL with an older one, and the user's saved routes may stop loading.

## Commands

```sh
$S game                  # start the screen if needed, launch Cairn, wait for "Press any key"
$S start                 # just the screen and virtual input, no game
$S shot [file] [scale%]  # screenshot (default /tmp/cairn-fake-screen/shot.png); prints the path
$S key <keysym> [hold_s] # tap a key: Return Escape Up Down Left Right Tab space F6 w ...
$S key w 1.5             # hold for 1.5 s
$S down shift; $S up shift
$S type "Lunch Break"
$S move 960 540          # absolute pointer position, screen pixels
$S rel 40 0              # relative mouse motion (camera look)
$S click [x y] [left|right|middle]
$S scroll 3              # positive = down
$S status
$S stop                  # quit the game on the fake screen and tear everything down
```

Key names are X keysyms. Coordinates are in the 1920x1080 screen, so halve what you measure on a 50% screenshot.

## Getting around the game

- The mod prints its hotkeys near the top of `Latest.log` (the "Choose Free Climb on the title screen" line). F10 dumps the world state to the log from anywhere.
- Menus are keyboard driven: arrows, Enter, and Escape for back. The level select's footer says the same.
- Mouse clicks work but hovering doesn't move the menu highlight. Be careful clicking near the bottom of the title menu, because Quit is there and exits the game.
- Screenshot before and after each input, and give the game a second or two to react. Loading into a mountain takes longer, so watch the log or keep screenshotting.

## Rules

- Only one copy of the game can run per Proton prefix. `game` refuses to start if Cairn is already running anywhere. If the user has it open on their desktop, ask before quitting it, and never kill a copy you didn't start.
- Other agents may share this machine and its single fake screen (`/tmp/cairn-fake-screen`). If `status` shows a game you didn't launch, ask the user before stopping it.
- Don't put a VNC server or anything else network-facing on the fake screen. Right now nothing on it listens on the network, and its X display only accepts the user's own processes.

## Troubleshooting

- Logs for the screen itself: `/tmp/cairn-fake-screen/sway.log`, `vinput.log` and `game.log` (Proton/Wine output).
- If `game` says Cairn exited, check `game.log` and the end of `Latest.log`.
- `vinput.py` is a small pure-Python Wayland client. It adds a virtual keyboard (`zwp_virtual_keyboard_v1`) and pointer (`zwlr_virtual_pointer_v1`) to the headless seat, which otherwise has no input devices. Without them Xwayland never gets keyboard focus, and XTEST/xdotool-style input is ignored.
