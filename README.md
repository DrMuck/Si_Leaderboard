# Si_Leaderboard

Per-faction leaderboards for Silica dedicated servers (MelonLoader, Mono server):
top 10 commanders and top 10 FPS players for Sol, Centauri, Alien and overall, plus personal
stats and ranks, all through the `/lboard` chat menu.

Supported game versions: the Steam **default** branch (0.9.47) and the **test** branch (1.0.2).
The source is the same for both; only the reference DLLs differ. Git branch `master` is built
against the default branch, git branch `test` against the test branch (see Build and deploy).

## Where the numbers come from

| | source | cost |
|---|---|---|
| Commander ranking | Si_EloMatchmaking's `UserData\cfg\EloMatchmaking\ratings.json` (overall Elo and a per-faction Elo updated with every rated round). Re-read only when the file's timestamp changes. | nothing per event |
| FPS stats | own postfixes on `MP_Strategy.OnUnitDestroyed` / `OnStructureDestroyed` (the hooks Si_Logging and Si_ReplayLogging also use). Per kill: resolve attacker player, victim player, update a few counters. | a few dictionary lookups per kill |
| Play time / rounds | presence poll every 10 s | negligible |
| Persistence | `UserData\cfg\Leaderboard\stats.json`, written once per round end (and every 10 min during a round) | one file write per round |

The replay mod (.srpl) was checked as a source and rejected: it stores no Steam IDs, no commander
and no winner, and exposes no API. The HL log has all of that, so the seed is built from the logs.

### Why a third hook on the same callback (design note, 2026-10-20)

Si_Logging, Si_ReplayLogging and this mod each put a postfix on `MP_Strategy.OnUnitDestroyed`.
That looks redundant, but only the hook is duplicated, not the work: Harmony chains the three
postfixes, each resolves attacker and victim with a few lookups, and the chaining itself costs
tens of nanoseconds per kill. The alternatives were weighed and rejected:

- **One shared event raised by another mod.** Si_Logging is databomb's; carrying a patched DLL
  across their releases is fragile (the updater already replaced a same-version build once).
  Si_ReplayLogging is ours and could raise an event in ~20 lines, but the leaderboard would then
  go blind whenever replay logging is off or fails, and inherit that mod's attacker resolution
  (it uses `Unit.ControlledBy`; Si_Logging uses `NetworkComponent.OwnerPlayer`; this mod checks
  both).
- **Tailing the L-log live.** Text parsing per line, dependent on Si_Logging's log settings, plus
  a parser to maintain. Strictly worse than the callback.

An independent hook costs ~60 lines and nothing measurable, and cannot be broken by the other two
mods.

**Possible simplification, to verify once the server runs:** the game's own
`GameEvents.OnUnitDestroyed` needs no Harmony patch at all. Si_ReplayLogging stopped using it in
July 2026 because it delivered the wrong instigator (attacker == victim). Si_Buy still pays kill
rewards from it. Check: let Si_Buy pay a kill reward on 1.0.2 and see whether the right player is
credited. If the event is correct again, switch `Patch_UnitDestroyed` / `Patch_StructureDestroyed`
in `LbMod.cs` to subscribing `GameEvents.OnUnitDestroyed` / `OnStructureDestroyed` (re-subscribe
in `OnSceneWasLoaded`, the game nulls these on scene change).

## Measured cost (seed with 4705 players, .NET 8 on the dev box; the Mono server will be a bit slower)

| operation | when | main-thread cost |
|---|---|---|
| kill / death update | per kill | 0.5 us |
| ranking re-sort | first `/lboard` after a change | 0.35 ms |
| presence poll | every 10 s | ~5 us per connected player |
| delta save (collect touched players + queue) | round end, every 10 min | 0.03 ms |
| delta save (serialize + append) | same, **pool thread** | ~1 ms, ~30 KB per round |
| full rewrite (every 10th save, reset, shutdown) | pool thread | ~60 ms, 3.6 MB |
| OnUpdate when idle | per frame | one bool check |

Saving is diff-based: a save appends one JSON line with only the players touched since the last
save to `stats.json.journal.jsonl`; every tenth save, on reset and on shutdown the full file is
rewritten and the journal truncated. Loading reads the full file and replays the journal (verified
bit-identical in a harness). All file work runs on a thread-pool thread, which the OS schedules on
whichever core is free, not the one running the game loop; the main thread only collects a few
dozen references. Per-player objects are shared with the live data, which is safe because every
player's bucket dictionary is created complete up front and counter writes are atomic.

## FPS rating

A **duel** is a player-piloted unit killing another player's piloted unit on an enemy team.
Killer scores 1, victim 0, Elo with K = 16 from 1500. Each faction has its own rating (the faction
the player was on at the kill) plus an overall rating. Kills on AI units, deaths by AI, structure
kills, K/D against players, rounds and hours are tracked alongside but do not enter the rating, so
farming AI does not move the ladder. A player appears in a faction's top 10 after 20 duels on it
(`MinDuelsForRank`); commanders after 5 games on the faction (`MinCommanderGamesForRank`).

Seed from the 2025-09 to 2026-08 logs: 4705 players, about 400 ranked per faction. Top Sol
duelists: Vee 1829, Lycan 1809, game8breaker 1802 (report in `data/report.md`).

## Commands

```
/lboard                 open / close the menu
  /1 Commanders  -> /1 Sol  /2 Centauri  /3 Alien  /4 Overall
  /2 FPS players -> /1 Sol  /2 Centauri  /3 Alien  /4 Overall
  /3 My stats
  /back   /0 close
/lboard com sol|cent|alien|all     commander top list directly
/lboard fps sol|cent|alien|all     FPS top list directly
/lboard me                         own stats: commander rating + rank overall and per faction,
                                   FPS rating + rank, K/D, AI kills, structures, rounds, hours
/lboard <name>                     another player's stats (online name or stored name)
```

Admin (`Power.Generic`): `/lboard on|off` or `/lboard enabled on|off` (tracking), `/lboard reload`, `/lboard reset confirm`
(new season: backup + wipe FPS stats; commander ratings are reset with `!elo reset confirm`).

Menu navigation hooks `Event_Chat.OnRequestPlayerChat` like `/buy`; `/1`-`/4` are consumed only
while the menu is open, and opening `/buy` or `/b` closes it. Lists go only to the requesting
player (10 lines + header).

## Files

```
Si_Leaderboard/        C# mod (netstandard2.1; references in include/netstandard2.1)
  LbMod.cs             entry, kill/structure postfixes, presence, round save
  StatsDb.cs           stats.json model, duel Elo, rankings cache
  EloBridge.cs         read-only view of the Elo mod's ratings.json
  Menu.cs              /lboard menu and commands
  Config.cs            JSON config (UserData\cfg\Leaderboard\Si_Leaderboard_Config.json)
tools/compute_stats.py rounds.jsonl (from Si_EloMatchmaking/tools/parse_logs.py) -> stats.json seed
data/                  seed + report from the Serverdata logs
```

## Build and deploy

```
cd Si_Leaderboard
dotnet build -c Release                          (Steam default branch -> bin\Release\default\)
dotnet build -c Release -p:SilicaBranch=test     (Steam test branch    -> bin\Release\test\)
copy bin\Release\default\Si_Leaderboard.dll "<server>\Mods\"
copy data\stats.json "<server>\UserData\cfg\Leaderboard\"      (optional seed)
```

`include/default` and `include/test` hold the reference DLLs of the respective Silica branch
(SilicaCore.dll + Unity modules from that server's Silica_Data\Managed, MelonLoader net6
`MelonLoader.dll` + `0Harmony.dll`, `Newtonsoft.Json.dll`, `Si_AdminMod.dll`). `SilicaBranch`
defaults to `default` on git `master` and to `test` on git `test`. Both builds passed a
Mono.Cecil reference check against both game versions on 2026-10-20 (the two hooks,
`MP_Strategy.OnUnitDestroyed(Unit, GameObject)` / `OnStructureDestroyed(Structure, GameObject)`,
and `MusicJukeboxHandler.OnGameStarted/OnGameEnded` have the same signatures in 0.9.47 and 1.0.2).

Rebuild the seed:

```
python -I ..\Si_EloMatchmaking\tools\parse_logs.py "<log dir>" -o ..\Si_EloMatchmaking\data\rounds.jsonl
python -I tools\compute_stats.py ..\Si_EloMatchmaking\data\rounds.jsonl -o data\stats.json --report data\report.md
```

## 1.0 launch (2026-11-03)

Everyone starts from zero: run `/lboard reset confirm` and `!elo reset confirm` once on the
launch server (both back up the old files first). Until then the log-derived seeds serve as test
data.

## Status

Built 2026-10-20 for both Silica branches; the default-branch build is deployed to the local
server (which went back to the default branch the same day). Not yet exercised in a live round;
first things to check in the MelonLoader log: "[LB] Registered /lboard" and that a kill between
two players shows up in `/lboard me`.
