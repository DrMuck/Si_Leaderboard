"""
compute_stats.py - build the FPS leaderboard seed (stats.json) from rounds.jsonl
(produced by Si_EloMatchmaking/tools/parse_logs.py, which also emits the per-round
duel list).

Usage:
    python -I compute_stats.py ../Si_EloMatchmaking/data/rounds.jsonl -o ../data/stats.json [--report ../data/report.md]

Same model as StatsDb.cs: per faction + "All" bucket; duel Elo K=16, start 1500,
killer S=1 / victim S=0, faction buckets rated separately from the All bucket.
"""
from __future__ import annotations

import argparse
import json
import sys
from datetime import datetime, timezone

START = 1500.0
K = 16.0
MIN_DUELS = 20
TOP_N = 10
FACTIONS = ("Sol", "Centauri", "Alien")
ALL = "All"
MIN_ROUND_PRESENCE_S = 60


def fresh():
    return {"rounds": 0, "time_s": 0.0, "kills_h": 0, "kills_ai": 0, "deaths_h": 0, "deaths_ai": 0,
            "structure_kills": 0, "duels": 0, "duel_wins": 0, "rating": START}


def expected(ra, rb):
    return 1.0 / (1.0 + 10 ** ((rb - ra) / 400.0))


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("rounds")
    ap.add_argument("-o", "--out", required=True)
    ap.add_argument("--report")
    a = ap.parse_args(argv)

    players = {}

    def P(steam, name):
        p = players.get(steam)
        if p is None:
            p = {"name": name, "last_seen": None, "f": {}}
            players[steam] = p
        if name:
            p["name"] = name
        return p

    def F(p, faction):
        s = p["f"].get(faction)
        if s is None:
            s = fresh()
            p["f"][faction] = s
        return s

    n_rounds = 0
    with open(a.rounds, encoding="utf-8") as f:
        for line in f:
            r = json.loads(line)
            if r.get("aborted"):
                continue
            n_rounds += 1
            # counters and presence
            for team in FACTIONS:
                tv = r["teams"].get(team)
                if not tv:
                    continue
                for steam, q in tv["players"].items():
                    p = P(steam, q.get("name", ""))
                    p["last_seen"] = r["end"]
                    for bucket in (team, ALL):
                        s = F(p, bucket)
                        s["time_s"] += q.get("seconds", 0)
                        if q.get("seconds", 0) >= MIN_ROUND_PRESENCE_S:
                            s["rounds"] += 1
                        s["kills_h"] += q.get("kills_h", 0)
                        s["kills_ai"] += q.get("kills_ai", 0)
                        s["deaths_h"] += q.get("deaths_h", 0)
                        s["deaths_ai"] += q.get("deaths_ai", 0)
                        s["structure_kills"] += q.get("structure_kills", 0)
            # duels in log order
            for att, ateam, vic, vteam in r.get("duels", []):
                if ateam not in FACTIONS or vteam not in FACTIONS or att == vic:
                    continue
                pa, pv = P(att, ""), P(vic, "")
                for ab, vb in ((ateam, vteam), (ALL, ALL)):
                    sa, sv = F(pa, ab), F(pv, vb)
                    e = expected(sa["rating"], sv["rating"])
                    sa["rating"] += K * (1.0 - e)
                    sv["rating"] -= K * (1.0 - e)
                    sa["duels"] += 1
                    sa["duel_wins"] += 1
                    sv["duels"] += 1

    for p in players.values():
        for s in p["f"].values():
            s["rating"] = round(s["rating"], 1)
            s["time_s"] = round(s["time_s"], 0)

    db = {"schema": 1, "generated": datetime.now(timezone.utc).isoformat(timespec="seconds"),
          "source": a.rounds, "rounds_live": 0, "players": players}
    with open(a.out, "w", encoding="utf-8") as f:
        json.dump(db, f, indent=1, ensure_ascii=False)

    if a.report:
        lines = [f"# FPS leaderboard seed\n", f"Rounds: {n_rounds}. Players: {len(players)}.\n"]
        for faction in FACTIONS + (ALL,):
            ranked = [(s, p) for p in players.values() for b, s in p["f"].items()
                      if b == faction and s["duels"] >= MIN_DUELS]
            ranked.sort(key=lambda x: -x[0]["rating"])
            lines += [f"\n## {faction} ({len(ranked)} ranked, >= {MIN_DUELS} duels)\n",
                      "| # | player | rating | duels W/N | K/D vs players | AI kills | rounds |", "|---|---|---|---|---|---|---|"]
            for i, (s, p) in enumerate(ranked[:TOP_N], 1):
                kd = s["kills_h"] / s["deaths_h"] if s["deaths_h"] else s["kills_h"]
                lines.append(f"| {i} | {p['name'].replace('|', ' ')} | {s['rating']:.0f} | {s['duel_wins']}/{s['duels']} | "
                             f"{kd:.2f} ({s['kills_h']}/{s['deaths_h']}) | {s['kills_ai']} | {s['rounds']} |")
        with open(a.report, "w", encoding="utf-8") as f:
            f.write("\n".join(lines) + "\n")
    print(f"rounds={n_rounds} players={len(players)} -> {a.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
