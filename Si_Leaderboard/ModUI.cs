using System.Collections.Generic;
using MelonLoader;
using System;

namespace Si_Leaderboard
{
    public partial class LbMod
    {
        // Si_ModUI export: mirrors the /lboard chat commands, built from in-memory state only.
        // Si_ModUI per-player hook: the caller's own stats as tables on the Leaderboard page (same numbers as /lboard me).
        public static string? ModUIPlayerData(ulong steamId)
        {
            try
            {
                string steam = steamId.ToString();
                var x = ModUI.ModUIExport.ForPlayer();
                var c = Elo?.Commander(steam);
                if (c != null)
                {
                    var rows = new List<string[]>();
                    int rank = Elo.RankOf(steam, EloBridge.ALL, Cfg.MinCommanderGamesForRank);
                    rows.Add(new[] { "Overall", Fmt(c.rating), rank > 0 ? "#" + rank : "-", c.wins + "-" + c.losses, c.games.ToString() });
                    foreach (var f in StatsDb.Factions)
                    {
                        int g = EloBridge.FactionGames(c, f); if (g == 0) continue;
                        int r = Elo.RankOf(steam, f, Cfg.MinCommanderGamesForRank);
                        int wins = EloBridge.FactionWins(c, f);
                        rows.Add(new[] { Short(f), Fmt(EloBridge.FactionRating(c, f)), r > 0 ? "#" + r : "-", wins + "-" + (g - wins), g.ToString() });
                    }
                    x.Table("Commander", new[] { "", "Rating", "Rank", "W-L", "Games" }, rows.ToArray());
                }
                var ps = Db?.Get(steam);
                if (ps != null && ps.f.TryGetValue(StatsDb.ALL, out var all))
                {
                    var rows = new List<string[]>();
                    int rAll = Db.RankOf(steam, StatsDb.ALL);
                    rows.Add(new[] { "Overall", Fmt(all.rating), rAll > 0 ? "#" + rAll : all.duels + "/" + Cfg.MinDuelsForRank + " duels", Fmt1(all.Kd), all.kills_h + " / " + all.deaths_h, all.duel_wins + " / " + all.duels });
                    foreach (var f in StatsDb.Factions)
                    {
                        if (!ps.f.TryGetValue(f, out var s) || (s.kills_h + s.deaths_h + s.kills_ai) == 0) continue;
                        int r = Db.RankOf(steam, f);
                        rows.Add(new[] { Short(f), Fmt(s.rating), r > 0 ? "#" + r : "-", Fmt1(s.Kd), s.kills_h + " / " + s.deaths_h, s.duel_wins + " / " + s.duels });
                    }
                    x.Table("FPS", new[] { "", "Rating", "Rank", "K/D", "Kills / deaths", "Duels won" }, rows.ToArray());
                    x.Table("Activity", new[] { "Rounds", "Hours", "AI kills", "Deaths by AI", "Structures" },
                        new[] { all.rounds.ToString(), Fmt(all.time_s / 3600), all.kills_ai.ToString(), all.deaths_ai.ToString(), all.structure_kills.ToString() });
                }
                return x.Build();
            }
            catch (Exception exception) { MelonLogger.Warning("[LB] ModUIPlayerData: " + exception.Message); return null; }
        }

        public static string? ModUIData()
        {
            try
            {
                return ModUI.ModUIExport.New()
                    .Enabled(Cfg.Enabled)
                    .Info("Leaderboard tracking " + (Cfg.Enabled ? "ON" : "OFF"))
                    .Info("Rank after " + Cfg.MinDuelsForRank + " duels (FPS) or " + Cfg.MinCommanderGamesForRank + " commander games | top " + Cfg.TopN)
                    .Action("/lboard me", "My stats", "Your commander rating, FPS rating, ranks, K/D and play time.")
                    .Picker("/lboard com", "Commander top list", "Top commanders overall or of one faction.", admin: false, arg: "choice",
                            ("all", "Overall"), ("sol", "Sol"), ("cen", "Centauri"), ("ali", "Alien"))
                    .Picker("/lboard fps", "FPS top list", "Top FPS duelists overall or of one faction.", admin: false, arg: "choice",
                            ("all", "Overall"), ("sol", "Sol"), ("cen", "Centauri"), ("ali", "Alien"))
                    .OnOff("/lboard enabled", "Tracking", "Record kills and play time for the leaderboard.", admin: true, on: Cfg.Enabled)
                    .Action("/lboard reload", "Reload stats", "Re-read the stats file from disk.", admin: true)
                    .Picker("/lboard reset", "New season", "Back up and wipe the FPS stats; asks for confirmation.", admin: true, arg: "choice", ("confirm", "Confirm wipe"))
                    .Build();
            }
            catch (Exception exception) { MelonLogger.Warning("[LB] ModUIData: " + exception.Message); return null; }
        }
    }
}
