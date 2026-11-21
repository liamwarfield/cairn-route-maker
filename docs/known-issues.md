# Known issues

## The level select shows over the pause menu

The mod's level select can be open at the same time as the game's pause menu. Both draw at once, and their entries overlap. Not looked into yet.

![The level select drawn over the pause menu](images/level-select-over-pause-menu.jpg)

## Free climb on Cairn 1.1 (On the Trail)

0.4.1 gets free climb working with the DLC by playing it with the original rules: health and
stamina instead of On the Trail's energy, and no "Pack your bag" screen. To keep its saves out of
the story slots while it does that, the mod redirects the game's save code. That is checked for
quick save and quit, but not for dying and retrying. Free climb still shares each DLC crag's summit
save slot with your On the Trail run there; the mod warns before replacing a run of yours. See
[dlc-1.1-research.md](dlc-1.1-research.md).
