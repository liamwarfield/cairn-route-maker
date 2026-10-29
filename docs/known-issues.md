# Known issues

## Routes that start on a wall aren't timed

Teleporting to a start on steep rock puts Ava on the wall (climbing). The clock only starts when she leaves the ground to climb (`Routes/Attempt.cs`), so after F7 to a start on the wall, nothing is timed until she has been back on her feet. Before wall teleports she arrived standing half inside the rock, so these routes used to be timed.

## Taping fingers in the bivouac breaks the game

Choosing the action that tapes Ava's fingers in the bivouac breaks the game. Not looked into yet.
