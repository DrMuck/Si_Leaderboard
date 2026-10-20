using MelonLoader;
using System;

namespace Si_Leaderboard
{
    public partial class LbMod
    {
        // Si_ModUI export: mirrors the /lboard chat commands, built from in-memory state only.
        public static string? ModUIData()
        {
            try
            {
                return ModUI.ModUIExport.New()
                    .Info("Leaderboard tracking " + (Cfg.Enabled ? "ON" : "OFF") + " | " + Db.Data.players.Count + " FPS players")
                    .Info("Rank after " + Cfg.MinDuelsForRank + " duels (FPS) or " + Cfg.MinCommanderGamesForRank + " commander games | top " + Cfg.TopN)
                    .Action("/lboard me", "My stats", "Your commander rating, FPS rating, ranks, K/D and play time.")
                    .Picker("/lboard com", "Commander top list", "Top commanders of a faction.", admin: false, arg: "team")
                    .Picker("/lboard fps", "FPS top list", "Top FPS duelists of a faction.", admin: false, arg: "team")
                    .OnOff("/lboard enabled", "Tracking", "Record kills and play time for the leaderboard.", admin: true, on: Cfg.Enabled)
                    .Action("/lboard reload", "Reload stats", "Re-read the stats file from disk.", admin: true)
                    .Action("/lboard reset", "New season", "Back up and wipe the FPS stats. Arguments: confirm", admin: true)
                    .Build();
            }
            catch (Exception exception) { MelonLogger.Warning("[LB] ModUIData: " + exception.Message); return null; }
        }
    }
}
