/*
 Config.cs - Si_Leaderboard JSON config.
 File: UserData\cfg\Leaderboard\Si_Leaderboard_Config.json (created with defaults on first run).
*/
using MelonLoader;
using Newtonsoft.Json;
using System;
using System.IO;

namespace Si_Leaderboard
{
    public class LbConfig
    {
        public bool Enabled = true;

        // ---- FPS duel rating (player-piloted unit kills player-piloted unit) ----
        public double StartRating = 1500;
        public double DuelK = 16;
        /// <summary>Duels needed on a faction before a player appears in that faction's top list.</summary>
        public int MinDuelsForRank = 20;
        /// <summary>Commander games on a faction before a commander appears in that faction's top list.</summary>
        public int MinCommanderGamesForRank = 5;
        public int TopN = 10;

        // ---- bookkeeping ----
        /// <summary>Presence poll interval (seconds). Adds play time per faction; cheap.</summary>
        public int PresencePollSeconds = 10;
        /// <summary>Also save during the round every N seconds (0 = only at round end).</summary>
        public int AutoSaveSeconds = 600;
        /// <summary>Count kills on AI units (kills_ai) and deaths by AI. Off saves a little work on AI-heavy servers.</summary>
        public bool TrackAiKills = true;
        public bool Debug = false;
    }

    public static class ConfigStore
    {
        public static string Dir => _dir ??= ResolveDir();
        static string? _dir;

        static string ResolveDir()
        {
            string userData = MelonLoader.Utils.MelonEnvironment.UserDataDirectory;
            return Path.Combine(userData, "cfg", "Leaderboard");
        }

        public static string ConfigPath => Path.Combine(Dir, "Si_Leaderboard_Config.json");
        public static string StatsPath => Path.Combine(Dir, "stats.json");
        public static string EloRatingsPath => Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "cfg", "EloMatchmaking", "ratings.json");

        public static LbConfig Load()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                if (File.Exists(ConfigPath))
                {
                    var cfg = JsonConvert.DeserializeObject<LbConfig>(File.ReadAllText(ConfigPath)) ?? new LbConfig();
                    Save(cfg);
                    return cfg;
                }
                var fresh = new LbConfig();
                Save(fresh);
                return fresh;
            }
            catch (Exception ex)
            {
                MelonLogger.Error("[LB] Config load failed, using defaults: " + ex.Message);
                return new LbConfig();
            }
        }

        public static void Save(LbConfig cfg)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(cfg, Formatting.Indented));
            }
            catch (Exception ex)
            {
                MelonLogger.Error("[LB] Config save failed: " + ex.Message);
            }
        }
    }
}
