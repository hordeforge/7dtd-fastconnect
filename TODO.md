# TODO: 7dtd-fastconnect

Agent ownership: mark `[-] in progress: <agent>, YYYY-MM-DD` before editing;
`[x]` on completion; restore `[ ]` with a reason if released or blocked.

## Backlog

- [ ] In-world frame hitches on a Local host (user-reported, reproduced by
  observation, not yet by measurement): seconds-long stalls during which GPU
  utilisation *drops*, with the GPU otherwise pinned near 99 %. Entity creation
  in-world is fast (max 116 ms over 7 spawns in the 2026-08-23 session), so this
  is not the startup addressables deadlock recurring. The instrument is already
  shipped: `diag on` in the F1 console, play a few minutes, read the
  `[7dtd-fastconnect] hitch` lines (README § Diagnosing in-world frame hitches).
  First question to settle is whether the frame cap reaches the renderer at all
  — set Options → Video → FPS Limit In Game to 60 and check whether the next
  hitch line reports `targetFps 60`. If it stays `-1` under a 60 cap, that is a
  separate bug from the stutter and should be fixed first. Log gaps are not a
  hitch measure: the game does not log per frame.
