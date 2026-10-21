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
