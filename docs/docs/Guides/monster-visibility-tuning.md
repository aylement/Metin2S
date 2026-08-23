---
tags: [developer]
---

# Developer: Monster Visibility & Movement Tuning

This documents a long investigation into a bug reported as "monsters take a long time to appear while
walking, and seem to trail behind the player" (and the opposite: things staying visible long after they
should have disappeared). The real root cause turned out to be a single mistuned constant, but several
other real bugs were found and fixed along the way. All of them are worth understanding before touching
this part of the codebase again.

## The real root cause: player position lag

`Entity.Goto()` (`Libraries/Game.Server/World/Entities/Entity.cs`) times server-side movement
interpolation using real per-model animation data (`walk.msa`/`run.msa`) when available, and falls back to
a flat assumed speed (`FallbackMovementUnitsPerSecond`) when it isn't. **No `.msa` file ships in this
repo at all**, for any entity - so this fallback is always used, players included.

The fallback speed was a single shared constant (`250.0` units/sec). That's far slower than real player
movement (especially mounted), so the server's own dead-reckoned `PositionX`/`PositionY` for the player
fell further and further behind the client's true on-screen position the longer a player moved
continuously in one direction. That position is:

- the center of the player's nearby-entity view circle (see below) - so it looked like monsters were
  always revealed/hidden relative to somewhere *behind* the player, not their real position, and
- what gets persisted to the database on disconnect - confirmed by reconnecting mid-run: the character
  always reappeared behind where it visually was, exactly matching the server's lagging tracked position.

**Fix**: `FallbackMovementUnitsPerSecond` is now a `protected virtual` property on `Entity`, defaulting to
`250.0`. `PlayerEntity` overrides it to `1200.0` - live-tuned and confirmed correct while mounted on a
level 30 horse. This has **not** been separately re-validated unmounted or on other mount levels; the
existing `MovementSpeed`-based percentage multiplier in `Goto()` should scale it down automatically for
slower states, but if unmounted movement is ever reported as feeling wrong (too fast, or the lag symptoms
above come back), retune this value for that case specifically before looking anywhere else.

Do **not** raise the monster-side default (still `250.0`, deliberately conservative/slow) to fix an
unrelated problem: an earlier bug had `Goto()` fall back to `MovementDuration = 0` (instant server-side
teleport) when animation data was missing, which let monsters land server-validated melee hits from far
away, well before they visually arrived on anyone's screen. The slow flat speed is what fixed that; it
just happens to be the wrong choice for the player's own position tracking, which is why the two are now
split.

## The reveal/hide system itself (`Map.cs`)

`Map.Update()` keeps every entity's `NearbyEntities` list (used to decide who gets a spawn/despawn packet)
in sync via a per-entity rescan: query the quadtree for everything within `Entity.VIEW_DISTANCE` of the
entity's current position, diff against what it already knows about, reveal what's new, hide what fell out
of range.

Two real bugs were found and fixed in this loop:

1. **Rescans were entirely gated on "did this entity's position just change this tick."** A player who
   stopped moving never rescanned again - a monster that wandered out of true view range near where the
   player started stayed visible until, by chance, *that monster's own* movement triggered a correcting
   rescan from its side. Unpredictable, sometimes 10+ seconds. Fixed: player entities also rescan on a
   plain time interval (`NEARBY_RESCAN_INTERVAL`, 1 second) even while stationary. Non-player entities keep
   the original movement-gated behaviour - not a correctness gap, since the affected player's own periodic
   sweep now guarantees correctness from their side regardless, and it avoids rescanning potentially
   thousands of idle monsters every tick.
2. **`_lastNearbyScanTime` didn't get cleaned up on entity despawn** - fixed in the `_pendingRemovals`
   dequeue loop. Would otherwise leak one dictionary entry per monster death/respawn cycle.

Several other rescan-cadence designs were tried and reverted before landing on the current one - a naive
"only rescan every N units moved" throttle, and later a sector-crossing batch design matching the real
game's `sectree_manager.cpp`. Both had real, measured downsides (see the in-code comment history in
`Map.cs`'s `Update()`, intentionally kept). The current design - rescan any entity at most once per second,
always against its *exact* live position, with a query radius of exactly `VIEW_DISTANCE` and no margin - is
correct without needing a margin because each rescan is a fresh ground-truth query, not an extrapolation
from a stale reference point.

## Packet send-loop batching (`Core/Core/Networking/Connection.cs`)

Unrelated to the position bug, but found and fixed in the same investigation: `SendPacketsWhenAvailableAsync`
used to dequeue and send exactly one packet at a time, each a full `WriteAsync`+`FlushAsync` async round
trip, before even looking at the next queued packet. A burst - initial connection revealing everyone
already in view, or entering a dense area - can queue 100+ packets (2 per revealed entity) within
milliseconds. Measured: a 68-entity connect burst that the server decided to reveal in 21ms took the client
~14 real seconds to finish receiving, entirely explained by this loop.

Fixed: the loop now drains everything currently queued into one buffer and does a single
`WriteAsync`+`FlushAsync` per batch (capped at 512 packets/batch to bound buffer size), instead of one pair
per packet. Per-packet plugin hooks and debug logging still run per packet - only the actual socket I/O is
coalesced.

## `Entity.VIEW_DISTANCE`

Currently `10000`, matching the real official value. It was briefly dropped to `7500` mid-investigation
because monsters seemed visible from further away than expected - that turned out to be a visible symptom
of the position-lag bug above (a lagging view circle can *look* oversized in the direction of travel), not
the radius itself. Reverted back to `10000` once the real fix landed.

## Leftover diagnostics

Several `// TEMP DIAGNOSTIC` blocks (writing to `qcx_reveal_timing.log`, `qcx_send_queue.log`,
`qcx_create_profile.log`, `qcx_actor_update_debug.log` on both server and client) are still in place from
this investigation, clearly marked in code. They're safe to strip now that the underlying bug is fixed
and confirmed, but were left in for now in case of a regression report. The `dist=` field added to the
reveal/hide log lines is worth keeping permanently if cheap - it directly answers "is the view circle
actually centered where it should be" for any future investigation in this area.
