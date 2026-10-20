/*
 EloBridge.cs - read-only view of Si_EloMatchmaking's ratings.json (commander ratings).
 The file is re-read only when its modification time changes (the Elo mod writes it once per
 round), so a /lboard call normally costs nothing beyond a File.GetLastWriteTime.
*/
using MelonLoader;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Si_Leaderboard
{
    public class EloFactionStat
    {
        public int games;
        public int wins;
        public double rating;
    }

    public class EloCommander
    {
        public string name = "";
        public double rating;
        public int games;
        public int wins;
        public int losses;
        public Dictionary<string, EloFactionStat> factions = new Dictionary<string, EloFactionStat>();
    }

    class EloFile
    {
        public Dictionary<string, EloCommander> commanders = new Dictionary<string, EloCommander>();
        public Dictionary<string, EloCommander> players = new Dictionary<string, EloCommander>();
    }

    public class EloBridge
    {
        readonly string _path;
        DateTime _loadedStamp = DateTime.MinValue;
        EloFile _data = new EloFile();
        readonly Dictionary<string, List<KeyValuePair<string, EloCommander>>> _rankCache = new Dictionary<string, List<KeyValuePair<string, EloCommander>>>();
        public const string ALL = "All";

        public EloBridge(string path) { _path = path; }

        public bool Available => File.Exists(_path);

        void Refresh()
        {
            try
            {
                if (!File.Exists(_path))
                {
                    if (_data.commanders.Count > 0) { _data = new EloFile(); _rankCache.Clear(); }
                    return;
                }
                var stamp = File.GetLastWriteTimeUtc(_path);
                if (stamp == _loadedStamp) return;
                var d = JsonConvert.DeserializeObject<EloFile>(File.ReadAllText(_path));
                _data = d ?? new EloFile();
                _data.commanders ??= new Dictionary<string, EloCommander>();
                _data.players ??= new Dictionary<string, EloCommander>();
                _loadedStamp = stamp;
                _rankCache.Clear();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[LB] Could not read Elo ratings: " + ex.Message);
            }
        }

        public EloCommander? Commander(string steam)
        {
            Refresh();
            return _data.commanders.TryGetValue(steam, out var c) ? c : null;
        }

        public EloCommander? PlayerElo(string steam)
        {
            Refresh();
            return _data.players.TryGetValue(steam, out var c) ? c : null;
        }

        public static double FactionRating(EloCommander c, string faction)
        {
            if (faction == ALL) return c.rating;
            if (c.factions != null && c.factions.TryGetValue(faction, out var f) && f.rating > 0) return f.rating;
            return c.rating;   // older files without per-faction rating
        }

        public static int FactionGames(EloCommander c, string faction)
        {
            if (faction == ALL) return c.games;
            return c.factions != null && c.factions.TryGetValue(faction, out var f) ? f.games : 0;
        }

        public static int FactionWins(EloCommander c, string faction)
        {
            if (faction == ALL) return c.wins;
            return c.factions != null && c.factions.TryGetValue(faction, out var f) ? f.wins : 0;
        }

        /// <summary>Commanders ranked by (faction) rating with at least minGames games on that faction.</summary>
        public List<KeyValuePair<string, EloCommander>> Ranking(string faction, int minGames)
        {
            Refresh();
            string key = faction + "|" + minGames;
            if (_rankCache.TryGetValue(key, out var cached)) return cached;
            var list = _data.commanders
                .Where(kv => FactionGames(kv.Value, faction) >= minGames)
                .OrderByDescending(kv => FactionRating(kv.Value, faction))
                .ToList();
            _rankCache[key] = list;
            return list;
        }

        public int RankOf(string steam, string faction, int minGames)
        {
            var list = Ranking(faction, minGames);
            for (int i = 0; i < list.Count; i++) if (list[i].Key == steam) return i + 1;
            return 0;
        }
    }
}
