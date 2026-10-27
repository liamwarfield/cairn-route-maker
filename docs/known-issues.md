# Known issues

## Routes that start on a wall aren't timed

Teleporting to a start on steep rock puts Ava on the wall (climbing). The attempt only arms when she is standing within 1.5 m of the start (`Routes/Attempt.cs`), so:

- A route whose start marker is on the wall never arms. Its timer never starts and off-route grabs aren't checked. Before wall teleports she arrived standing half inside the rock, so these routes used to arm.
- The holds she grabs when she arrives count as attachments. If they aren't painted on the route, the attempt could be flagged off-route at once.
- F7 with the editor closed doesn't reset the attempt. Pressing it mid-run carries the old state over: the clock keeps running from the previous start, and a red (off-route) or finished timer stays that way.

Waiting on a redesign of route starts.
