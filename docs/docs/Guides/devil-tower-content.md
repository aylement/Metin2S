---
tags: [developer]
---

# Developer: Devil Tower Content (WIP)

Custom, hand-built content added to this dev server: the Devil Tower dungeon (`metin2_map_deviltower1`)
and its access point, Hwang/Dark Temple (`metin2_map_milgyo`). Neither map shipped with this repo
originally - both were reconstructed from the real client's extracted packs and the real server source,
then populated by hand since this server has no event-driven quest engine (see below). Player's own
reference for the intended full design: `docs/Demon Tower infos.txt` (a real community run-guide, 12
floors, only floors 1-4 are built so far).

## Where the map data came from

Neither map's raw terrain/collision/spawn data exists anywhere in this repo's original `data/` folder, or
in the real server source tarball's included map subset. Both were recovered from the CLIENT's own packed
resources (`Client\pack\indoordeviltower1.epk`/`.eix` and `outdoormilgyo1.epk`/`.eix`), extracted via
EterNexus (see [[client-server-original-source]] memory / the client archaeology notes) into
`F:\GitHub\Metin2\ClientMT2\Extractions\`. Each extraction contains a `metin2_map_<name>` folder with a
`Setting.txt`, an (empty, dynamic-content-only) `regen.txt`, and one subfolder per map cell with
`attr.atr` (collision - **not converted, see Known limitations**), `height.raw`, `tile.raw`, etc.

Both maps are registered like any other map: an entry in `data/atlasinfo.txt` (position/size) and the
`maps` array in `appsettings.json`.

Real vnums for everything placed here were found by extracting the real server source's plaintext
`mob_proto.txt`/`mob_proto.csv` (`metin2_server+src.tar.gz`, see [[client-server-original-source]]) - the
binary `mob_proto` this repo ships can't be read directly, but that tarball's copy can. Note the
`share/locale/english/mob_proto.csv` file is actually in **German**, not English, despite its path.

## Known limitations (read before extending this further)

- **No real collision data.** `attr.atr` (client, per-cell, 256×256 bytes) was never converted to this
  server's `server_attr` format (LZO-compressed per-sectree `uint32` grid, see
  `MapAttributeProvider.cs`) - the byte-level meaning of the client's flags was never confirmed. Both maps
  are fully walkable everywhere as a result. Monster placement in `regen.txt`/`npc.txt` is therefore a
  **guess based on user live-testing feedback** ("there's a mob behind a wall here"), not verified
  geometry - expect to keep adjusting positions as more of the tower gets explored.
- **No event-driven quest engine.** The real game's `deviltower_zone.quest` (extracted from the real
  server source, real floors use `when X.kill begin ... end`, `server_timer`, `d.spawn_mob`, etc.) has no
  equivalent here - `Quest.cs` is dialogue-only ([NEXT]/[QUESTION] pages). All floor-progression logic
  lives as narrow special cases in `MonsterEntity.HandleDevilTowerFloorTrigger()`, hardcoded to this one
  map by name. If this keeps growing, it should become its own small service instead of living inline in
  `MonsterEntity.Die()`.
- **Single shared instance, not per-group dungeons.** There is no "dungeon instancing" concept - one
  tower, shared by every player on the server, no isolation between groups. Floor-progression triggers
  (`WarpAllPlayersOnMap`) warp *everyone currently on the map*, not just the group that earned it.
  `_floor4RealMetinVid`'s "which of the 10 is real" pick is a `static` field for the same reason -
  resets only on server restart, not per attempt.
- **No box/lootbox item support at all.** Checked live: `ItemUseHandler.cs` has no case for box-type
  items - using one silently does nothing. Not devil-tower-specific, just noted here since it came up
  testing drops. Not implemented as of this writing (explicitly deferred).
- **NPCs can't be renamed per spawn.** The Demon Tower Guard (see below) reuses vnum 11000's own proto
  name/model - there's no per-instance override, so its in-game display name is whatever that vnum's
  real proto name is, not literally "Demon Tower Guard".

## Hwang/Dark Temple (`metin2_map_milgyo`)

Real map, position/size `537600,51200` `4×4` (from the client's own atlas). Locale-mapped to `MAP_TEMPLE`
in the client's `localeinfo.py`. Landing point (`Town.txt`): local `(150,150)`.

Content (`regen.txt`/`npc.txt`):
- Tree Frog family: `1302` Tree Frog Leader, `1331` Big Tree Frog Soldier, `1332` Big Tree Frog Leader -
  clustered near the landing point.
- `791` Dark Leader boss, placed at `(802,848)` (user-specified live position).
- **Demon Tower Guard NPC** at `(528,593)`: vnum `11000` (City Guard skin). Right-click warps the player
  to the Devil Tower's floor 1 landing point. Registered world-scoped (not per-player) in
  `World.RegisterDevilTowerGuard()`, called once from `World.InitAsync()` right after `LoadShops()` -
  scoped by a `player.Map?.Name == "metin2_map_milgyo"` condition so it doesn't hijack every vnum-11000
  NPC that might exist elsewhere.

## Devil Tower (`metin2_map_deviltower1`)

Real map, position/size `204800,665600` `3×3`. All floors live inside this ONE map file - which floor
you're "on" is purely a local (map-relative) position band, exactly like the real client's own
`__GetDevilTowerFloor(x, y)` in `uimapnameshower.py` (locale-mapped to `MAP_SKELTOWER` - the tower's
internal name is literally "Skeleton Tower"). Every floor transition is therefore a *same-map* hard warp
(`PlayerEntity.WarpTo()`), not a cross-map one.

`VIEW_DISTANCE` was bumped from the (real, official) `10000` to `15000` specifically because of user
feedback while testing inside this tower - a deliberate live tuning choice, not "the correct value" in
general.

### Floor 1 (ground floor)

- Landing point (`Town.txt`): local `(115,612)`.
- **Metin of Toughness** ("Metin der Härte" in the real German locale), vnum `8010`, level 50, placed at
  `(203,683)`. Breaking it triggers the floor 1→2 warp.
- Composition: exactly one tight group of Vile Demons (`1031`/`1032`/`1034`/`1037`), the rest plain Demons
  (`1001` Soldier, `1002` Archer, `1004` Shaman). No Ghost of Grudge, no Brutal Demons, no boss here.

### Floor 2

- Local Y band `35000-50000` (`FLOOR2_LOCAL_Y_MIN`/`MAX` in `MonsterEntity.cs`) - only Y is checked when
  deciding whether a kill counts toward this floor's objective (no real walls to enforce X too).
- Densely populated ("teeming"): ~16 plain Demons + 3 distinct Vile Demon groups, arranged as a **ring**
  around the landing point, not a filled disc - a wide pillar sits at the room's center (live-confirmed by
  the user) and would otherwise hide spawns behind it.
- All floor-2 spawns use a 99999s respawn time - the objective is "kill them all", the population must be
  finite, not passively refill mid-fight.
- **Trigger**: killing the LAST living floor-2 monster (checked by re-scanning the whole floor-2 Y band on
  every floor-2 monster death) warps to floor 3.

### Floor 3

- Local zone `x 10000-25000, y 10000-25000`.
- A modest ring of plain Demons plus the **Demon King** boss (vnum `1091`).
- **Trigger**: unlike floor 2, this floor advances on the BOSS's death specifically (user's explicit call)
  - the plain Demons don't gate progression, they're just there for flavor/difficulty.

### Floor 4

- Local zone `x 35000-43500, y 61000-70500`.
- **10 identical Metin of the Fall** ("Metin des Sturzes"), vnum `8012`, level 60, arranged in a ring
  around the landing point.
- One of the 10 is secretly the "real" one, picked at random (`RandomNumberGenerator`) the first time ANY
  floor-4 Metin dies in a run (`_floor4RealMetinVid`, static). The other 9 behave like any ordinary Metin
  - normal drops, no special code path. Breaking the real one triggers the floor 4→5 warp.
- Triggers the floor 4→5 warp (`localX: 39250, localY: 43000`).

### Floor 5

- Local zone `x 35000-43500, y 38000-48000` (`FLOOR5_LOCAL_*` in `MonsterEntity.cs`) - same X band as
  floor 4, disambiguated by Y (no overlap), so both X and Y are checked here.
- **Real design (docs/Demon Tower infos.txt, floor 5)**: 5 Seals, broken by farming "Unlock Stones" from
  monsters (drastically reduced respawn) and dragging them onto Pillars, over ~20 minutes of waves - no
  pillar/drag-item interaction system exists on this server at all (see Known limitations). **Simplified,
  at the user's explicit request, to 2 "kill them all" waves** as a starting point, not the real grind.
- **Wave 1**: plain Demons (`1001`/`1002`/`1004`) in a ring around the landing point, defined in
  `regen.txt` like every other floor, 99999s respawn (finite population, same convention as floor 2).
- **Wave 2**: heavier on Vile Demons (`1031`/`1032`/`1034`/`1037`), spawned once wave 1's last monster
  dies. There's no "spawn on kill" primitive for `regen.txt`, so this one is spawned directly in code
  (`MonsterEntity.SpawnFloor5Wave2()`) - the same construction path `/spawn` uses, just triggered from
  `HandleDevilTowerFloorTrigger()` instead of a command. Gate flag (`_floor5Wave2Spawned`) is `static`,
  same single-shared-tower/reset-on-restart caveat as `_floor4RealMetinVid`.
- **Trigger**: killing the last living wave-2 monster warps to floor 6 (`localX: 39200, localY: 19200`).
  This landing point IS user-verified walkable (via `/goto`, no rebuild needed to test - same-map
  `/goto <x> <y>` is a walk, not a hard warp). Floor 6's real zone
  (`x 14000-43500, y 14000-24500` per `__GetDevilTowerFloor()`) is oddly wide - its geometric center
  (`~28750, 19250`) was tried first and is an actual void, no floor there at all. The working point reuses
  floors 4/5's own X column (`35000-43500`), just at floor 6's Y - a real design commonality. **Did NOT**
  generalize to floor 7's own landing point though - see that floor's section, live-confirmed void.

### Floor 6

- Local zone `x 14000-43500, y 14000-24500` per `__GetDevilTowerFloor()` - much wider X span than
  floors 1-5, real client geometry. **`FLOOR6_LOCAL_X_MIN` in `MonsterEntity.cs` is DELIBERATELY set to
  `30000`, not the real `14000`** - live-confirmed bug: floor 3's real zone (`x 10000-25000, y 10000-25000`)
  sits entirely inside the real floor-6 zone, and floor 3's own Demons are left alive after its boss dies
  (only the boss gates that floor). The "any floor-6 monster still alive" scan was counting those leftover
  floor-3 Demons forever - floor 6 could never register as cleared no matter how thoroughly it actually was
  (confirmed live: killed everything including the boss, "rien ne se passe"). `30000` cleanly separates our
  actual floor-6 spawns (`x 35700-42700`) from floor 3's Demons (`x 14000-21000`) - same X-disambiguation
  fix already used for floors 2/5's Y-overlap. **Confirmed working after this fix** - floor 6→7 warp fired
  correctly on a full clear.
- **Real design (docs/Demon Tower infos.txt, floor 6)**: kill the Proud Demon King, who then guards a boss
  chest and 3 of 4 blacksmiths - killing him spawns a random blacksmith NPC with a dialogue-choice upgrade
  interaction (Armor/Weapon/Acc). **No dialogue-choice system exists on this server yet** (confirmed
  live: bugs out) - **simplified, at the user's explicit request, to skip the blacksmith entirely**: a
  full room of Demons plus the boss, and unlike floor 3, EVERYONE (boss included) must die to advance -
  not just the boss.
- **Boss**: Proud Demon King ("Stolzer Damonenkonig" in the real German locale), vnum `1092`, level 75,
  BOSS rank - same base stats as floor 3's Demon King (vnum `1091`) but roughly double the HP (48961 vs
  24481, confirmed via the real `mob_proto.txt`).
- **Trigger**: last living floor-6 entity (any monster or the boss) dying warps to floor 7 (same
  "last one standing" pattern as floors 2/5, not floor 3's boss-only pattern).

### Floor 7

- Local zone `x 56000-68000, y 60000-73000` per `__GetDevilTowerFloor()` - a genuinely NEW region of the
  map file, distinct from floors 1-6's `x 10000-43500`. No `FLOOR7_LOCAL_*` zone-band constants exist in
  code - unlike floors 2/5/6, this floor's trigger doesn't need a "last one standing" check at all (see
  below), so there was nothing to hook a band check to.
- **Real design (docs/Demon Tower infos.txt, floor 7)**: destroy 4 Level 65 Metins first, which spawns a
  monster wave plus a single Level 70 Metin; breaking THAT grants the "Unknown Old Chest" -> "Elite Demon
  Tower Map" item, right-clicked to proceed to floor 8. **Simplified, same as floor 5, to "everyone present
  from the start"**: no 4-Metin gate, no chest/map item chain (no item-drag/right-click interaction system
  exists here) - just the monsters and the Level 70 Metin from the moment you arrive.
- **Monsters**: a tier not used on any earlier floor - Ghost of Grudge (`1035`), Ghost of Chaos (`1036`),
  Skull Swordmaster (`1038`), Flying Skull (`1039`), level 71-74, plus a couple of Vile Demon Leader
  (`1037`) clusters for density. Deliberately does NOT use Immortal Ghost (`1040`) - that vnum is reserved
  for floor 8 per the real design (its key-drop mob, see docs/Demon Tower infos.txt floor 8).
- **The target Metin**: "Metin des Mordes" (Metin of Murder), vnum `8014`, level 70 - confirmed via the
  real `mob_proto.txt`/`mob_proto.csv` the same way as every other floor's Metin.
- **Trigger**: breaking the Level 70 Metin warps directly to floor 8 (`localX: 62000, localY: 43500`,
  center-ish of floor 8's real zone) - same explicit-`EntityClass` pattern as floor 4, not a zone-band
  check (there's only one Metin, no "pick the real one of N" logic needed).
- **Special mechanic (user's explicit request)**: every monster spawned here stays aggressive toward a
  player for as long as that player is within view/minimap range (`Entity.VIEW_DISTANCE`), not the normal
  small chase-give-up distance - and immediately re-acquires the nearest in-range player rather than
  returning to spawn, for as long as anyone is in range at all. Implemented in `SimpleBehaviour` (tagged at
  `Init()` time by spawn position falling inside `MonsterEntity.FLOOR7_LOCAL_*`, exposed `internal` from
  `MonsterEntity` for exactly this) - scoped to floor 7 only, not a general AI change. The 4 new mob types
  above get it; so does any `1037` spawned inside floor 7's zone specifically (its floors 1/2/5/6 spawns are
  unaffected - the check is by position, not vnum).
- **Landing point is CONFIRMED BROKEN (live-tested, not just an unverified guess anymore).** Floor 7's
  whole real zone (`x 56000-68000, y 60000-73000`) sits in the far corner cell of this map's 3×3 grid -
  live-confirmed void: the user fell straight through with no ground, and even `/goto -m deviltower1`
  (floor 1's own long-working `(115,612)` landing point) showed the same void/wrong-region-name symptom
  right after visiting floor 7's area, then a full reconnect put them back on solid ground. Checked for an
  atlas registration conflict first (`atlasinfo.txt` has no other map anywhere near `deviltower1`'s block -
  ruled out) and for a second client pack that might hold real floor 7-9 content (`Client\pack\` has only
  `indoordeviltower1.epk`/`.eix` - no second devil-tower pack exists to extract instead). Working theory:
  this extracted 3×3 map's real designed geometry only ever covered floors 1-6's actual footprint
  (`x ≤ 43500`ish) - floors 7+ in the real game most likely live in a wholly different, not-yet-identified
  map file, and `__GetDevilTowerFloor()`'s floor 7-9 coordinate bands just happen to be defined the same
  function regardless of which physical map they're really meant for. **Not yet decided how to proceed**:
  relocate floor 7's content into already-working space within this same map (loses the correct floor-7
  icon/name, since the client's `__GetDevilTowerFloor()` band wouldn't match, but guarantees real terrain),
  or pause floor 7+ until a real map for it is found. Floor 8's target (`localX: 62000, localY: 43500`,
  same corner-cell logic) is presumed equally broken, untested.

## Real bugs found and fixed while building this

None of these are devil-tower-specific - they were surfaced by exercising features (movement, combat,
warps, commands) more heavily than previous testing had.

- **`/pull`'s direction math was broken** (a comment in the code already admitted this: "moves to the
  wrong side of the player... good enough for now"). Fixed to just walk the pulled monster straight to
  the player's live position, and set `Target` so it's real aggro (attacks once in range), not just a
  relocation. Also excluded Metin stones from being pulled at all - they have `NOMOVE` in the real game,
  and repeatedly `/pull`-ing one used to drag it further away each time until it went out of view distance
  ("disappeared"). Range bumped `3000 → 10000` per user request.
- **Server tick lag (up to ~10s delayed) during active combat**, traced to `MonsterEntity.GetPoint()`
  logging a warning on EVERY unimplemented stat-point query (`ATTACK_BONUS`, `MAGIC_ATTACK_BONUS`,
  `CRITICAL_PERCENTAGE`, `PENETRATE_PERCENTAGE`, `RESIST_CRITICAL`, `RESIST_PENETRATE`) - these are
  legitimately always 0 for monsters (player-only gear/skill stats), not "unimplemented". Fixed by
  returning 0 for them directly instead of falling through to the log-every-call fallback.
- **`Connection.Close()` could run twice for the same connection** (`ExecuteAsync`'s read loop called it
  once from inside a catch block, then unconditionally again right after, with no `return` in between) -
  the second pass re-ran the full player despawn/persist chain on an entity (and DB scope) the first pass
  had already torn down. Made idempotent via an `Interlocked` guard.
- **Warp-triggered reconnects could still intermittently fail** even after the above fix, with `"Cannot
  access a disposed context instance"` / `"Cannot move player that does not exist"`. Root cause: a
  comment in `PlayerEntity.Warp()` had always claimed the resulting client-initiated close would reach
  `GameConnection.OnCloseAsync` as `expected: true` (the lightweight, non-persisting path) - that was
  never actually true, `ExecuteAsync`'s read loop has no way to distinguish "we told the client to
  reconnect" from any other socket closure, so it always passed `false` (the heavy, full-persist path),
  redundantly re-persisting on a connection that's mid-teardown. Fixed with a real
  `Connection.MarkExpectedClose()` flag, set by `Warp()` right before sending the warp packet and
  consulted by the read loop's own `Close(...)` calls.
- **Collection-modified-during-enumeration crashes** in the new floor-trigger code
  (`HandleDevilTowerFloorTrigger`/`WarpAllPlayersOnMap`): `Map.Entities` can be mutated by the map's own
  tick thread concurrently with packet-handler threads, with no synchronization anywhere in `Map.cs`.
  Mitigated locally by snapshotting to an array/list before any LINQ query over it in the three spots this
  hit live - **not a real fix**, `Map.Entities` still isn't thread-safe in general; if this pattern shows
  up elsewhere, `Map.cs` needs actual locking, not another local snapshot.
- **Floor 6's "last one standing" check was contaminated by a DIFFERENT floor's leftover monsters** - see
  Floor 6's own section above for the full writeup (`FLOOR6_LOCAL_X_MIN` tightened `14000 -> 30000`).
  Same root SHAPE as the floor-2/5 Y-overlap bug (a real, wide zone from `__GetDevilTowerFloor()`
  overlapping another floor's zone) but this time the overlap was live-missed initially - a code comment
  even flagged the theoretical overlap and wrongly concluded it was safe because floor 3 "never runs a
  zone-band check", without considering that floor 6's OWN scan still counts floor 3's alive monsters
  regardless of what floor 3's own trigger code does. Worth remembering when giving any floor a wide real
  zone: check for other floors' actual monsters inside it, not just whether that other floor runs its own
  band check.
- **A NEW manifestation of the still-unresolved intermittent hard-disconnect bug** (see
  `[[client-optimization-prefetch]]` memory) surfaced while chasing the floor-7 void: a connection that
  never completed its handshake sat accepted-but-idle for ~1h52m, then finally hit its close/despawn path
  and crashed with `System.ObjectDisposedException: Cannot access a disposed context instance` on
  `SqliteGameDbContext`, inside `QuickSlotBar.PersistAsync()` (called from
  `PlayerEntity.OnDespawnAsync()` via the read loop's unexpected-close path). Not fatal to the player - a
  fresh reconnect right after succeeded normally - but it's a real crash on a real code path, logged as
  `[ERR] Failed to accept TCP connection` despite having nothing to do with accepting the NEW connection
  that happened to log around the same moment (misleading message, different connection object entirely).
  Not yet root-caused - a candidate lead for whoever picks the intermittent-disconnect investigation back
  up: whatever DI scope owns that `SqliteGameDbContext` outlived the connection it belonged to for
  hours, then got used long after something else had already disposed it.

## Next steps

- **Floor 7 needs a real landing spot before it's usable at all** - its whole real zone is a live-confirmed
  void (see Floor 7's section). Undecided: relocate into already-working map space (loses the correct
  floor-7 client icon) vs. find/extract a real second map vs. pause floor 7+ entirely. Do this before any
  further floor-7 polish (the always-aggro mechanic and monster roster are otherwise believed complete).
- Floor 5 is now 2 simplified waves (see above), not the real 5-seal/pillar grind - build the real
  Unlock-Stone-drop + drag-onto-pillar interaction later if the simplified version isn't enough, and/or
  extend past 2 waves.
- Floor 6 skips the Proud Demon King's blacksmith-upgrade reward entirely (no dialogue-choice system
  exists here yet) - build that once a dialogue-choice interaction exists in `Quest.cs`.
- Floor 7 skips the real 4-Metin gate and the chest/map item chain (no item-drag/right-click interaction
  system exists here) - build those once floor 7 has a real place to stand.
- Build floor 8 content (currently empty - Elite Demon Key drops from Immortal Ghosts (`1040`, deliberately
  unused on floor 7 to keep it available here), dragged onto a gravestone - same "no drag interaction"
  limitation as above). Floor 8's own landing point is presumed void too, unverified - same open question
  as floor 7's.
- Continue through the remaining floors per `docs/Demon Tower infos.txt` (seals/keys/blacksmiths from
  floor 8 onward get progressively more involved - may need real state tracking, not just "kill X").
- A separate, intermittent hard-disconnect bug (NOT the already-fixed `MarkExpectedClose` warp-reconnect
  issue) is still being investigated - see `[[client-optimization-prefetch]]` memory for its full history,
  and this doc's Real bugs section above for a NEW lead found this session (a disposed `SqliteGameDbContext`
  crash on a connection abandoned for ~1h52m). Diagnostic connection-ID logging is in
  `Connection.cs`/`CharacterMoveHandler.cs`/`ClientVersionPacketHandler.cs`/`World.cs`; TEMP diagnostic
  logging was also added this session directly around `HandleDevilTowerFloorTrigger()`'s floor-6 check and
  `PlayerEntity.Warp()`'s blocking persist call (both still in place, not yet cleaned up) while chasing what
  turned out to be the floor-6/floor-3 zone-overlap bug above, not a hang - worth removing once the
  intermittent-disconnect investigation itself is closed out, since they were left in as a live trap for it.
- If collision data ever becomes available for these two maps (a real `map.epk`/converted `attr.atr`),
  revisit every hand-placed spawn position - several were moved reactively based on "I can't reach this
  mob" reports, not verified against real geometry.
