/*
 StatsDb.cs - per-player FPS statistics (UserData\cfg\Leaderboard\stats.json).

 Per player and per faction ("Sol", "Centauri", "Alien") plus the "All" bucket:
   rounds, time_s, kills_h (player-piloted victims), kills_ai, deaths_h, deaths_ai,
   structure_kills, duels, duel_wins, rating (duel Elo).
 A duel = a player-piloted unit kills another player's piloted unit (different Steam IDs,
 different teams). Killer S=1, victim S=0, K = DuelK, start 1500. The faction rating uses the
 faction the killer / victim was on at that moment; "All" is faction-independent.

 Cost: every kill is a few dictionary lookups and two additions. Rankings are sorted on demand
 and cached until the next change (sorting a few thousand entries is sub-millisecond anyway).
*/
using MelonLoader;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace Si_Leaderboard
{
    public class FpsStat
    {
        public int rounds;
        public double time_s;
        public int kills_h;
        public int kills_ai;
        public int deaths_h;
        public int deaths_ai;
        public int structure_kills;
        public int duels;
        public int duel_wins;
        public double rating;

        public double Kd => deaths_h > 0 ? kills_h / (double)deaths_h : kills_h;
    }

    public class PlayerStats
    {
        public string name = "";
        public string? last_seen;
        public Dictionary<string, FpsStat> f = new Dictionary<string, FpsStat>();

        public FpsStat Get(string faction, double start)
        {
            if (!f.TryGetValue(faction, out var s)) { s = new FpsStat { rating = start }; f[faction] = s; }
            if (s.rating <= 0) s.rating = start;
            return s;
        }

        /// <summary>Create all buckets up front so the dictionary never changes shape while a background save reads it.</summary>
        public void EnsureBuckets(double start)
        {
            foreach (var fac in StatsDb.Factions) Get(fac, start);
            Get(StatsDb.ALL, start);
        }
    }

    public class StatsData
    {
        public int schema = 1;
        public string? generated;
        public string? source;
        public int rounds_live;
        public Dictionary<string, PlayerStats> players = new Dictionary<string, PlayerStats>();
    }

    public class StatsDb
    {
        public const string ALL = "All";
        public static readonly string[] Factions = { "Sol", "Centauri", "Alien" };

        public StatsData Data = new StatsData();
        readonly string _path;
        readonly LbConfig _cfg;
        bool _dirty;
        readonly Dictionary<string, List<KeyValuePair<string, PlayerStats>>> _rankCache = new Dictionary<string, List<KeyValuePair<string, PlayerStats>>>();

        public StatsDb(string path, LbConfig cfg)
        {
            _path = path;
            _cfg = cfg;
        }

        public bool Dirty => _dirty;

        public void Load()
        {
            try
            {
                if (!File.Exists(_path))
                {
                    Data = new StatsData();
                    int replayedNew = ReplayJournal();
                    MelonLogger.Msg($"[LB] No stats file at {_path}; starting empty" + (replayedNew > 0 ? $" (+{replayedNew} journal entries)" : "") + ".");
                    return;
                }
                Data = JsonConvert.DeserializeObject<StatsData>(File.ReadAllText(_path)) ?? new StatsData();
                Data.players ??= new Dictionary<string, PlayerStats>();
                int replayed = ReplayJournal();
                foreach (var p in Data.players.Values) p.EnsureBuckets(_cfg.StartRating);
                MelonLogger.Msg($"[LB] Loaded stats for {Data.players.Count} players" + (replayed > 0 ? $" (+{replayed} journal entries)" : "") + ".");
                if (replayed > 0) _journalSaves = FullSaveEvery;   // compact on the next save
            }
            catch (Exception ex)
            {
                MelonLogger.Error("[LB] Failed to load stats: " + ex);
                Data = new StatsData();
            }
            _rankCache.Clear();
            _dirty = false;
        }

        // ---- persistence: journal + periodic full rewrite -----------------------
        //
        // Save() appends ONE line holding only the players touched since the last save
        // ("delta") to stats.journal.jsonl; every FullSaveEvery-th save, on reset and on
        // shutdown the whole file is rewritten and the journal truncated. Load() reads the
        // full file and replays the journal. All file work runs on a pool thread; the main
        // thread only snapshots the dirty entries (a few dozen references).
        int _saveRunning;
        bool _savePending;
        int _journalSaves;
        public int FullSaveEvery = 10;
        readonly HashSet<string> _dirtyKeys = new HashSet<string>();
        readonly object _fileLock = new object();
        string JournalPath => _path + ".journal.jsonl";

        class Delta
        {
            public string? t;
            public int rounds_live;
            public Dictionary<string, PlayerStats> players = new Dictionary<string, PlayerStats>();
        }

        public void Save(bool force = false)
        {
            if (!_dirty && !force) return;
            if (Interlocked.CompareExchange(ref _saveRunning, 1, 0) != 0) { _savePending = true; return; }
            _dirty = false;
            _savePending = false;
            bool full = force || _journalSaves >= FullSaveEvery;
            string path = _path, journal = JournalPath;
            if (full)
            {
                _journalSaves = 0;
                _dirtyKeys.Clear();
                var snapshot = new StatsData
                {
                    schema = Data.schema,
                    generated = DateTime.UtcNow.ToString("o"),
                    source = Data.source,
                    rounds_live = Data.rounds_live,
                    players = new Dictionary<string, PlayerStats>(Data.players),
                };
                ThreadPool.QueueUserWorkItem(_ => RunSave(() => { lock (_fileLock) { WriteFull(path, snapshot); TruncateJournal(journal); } }));
            }
            else
            {
                _journalSaves++;
                var delta = new Delta { t = DateTime.UtcNow.ToString("o"), rounds_live = Data.rounds_live };
                foreach (var k in _dirtyKeys)
                    if (Data.players.TryGetValue(k, out var p)) delta.players[k] = p;
                _dirtyKeys.Clear();
                ThreadPool.QueueUserWorkItem(_ => RunSave(() =>
                {
                    string line = JsonConvert.SerializeObject(delta, Formatting.None);
                    lock (_fileLock) File.AppendAllText(journal, line + "\n");
                }));
            }
        }

        void RunSave(Action work)
        {
            try { work(); }
            catch (Exception ex) { MelonLogger.Error("[LB] Background save failed: " + ex.Message); _dirty = true; }
            finally
            {
                Interlocked.Exchange(ref _saveRunning, 0);
                if (_savePending) { _savePending = false; _dirty = true; }
            }
        }

        /// <summary>Synchronous full save (shutdown, reset).</summary>
        public void SaveNow()
        {
            try
            {
                int spins = 0;
                while (Volatile.Read(ref _saveRunning) != 0 && spins++ < 300) Thread.Sleep(10);
                lock (_fileLock) { WriteFull(_path, Data); TruncateJournal(JournalPath); }
                _dirty = false;
                _dirtyKeys.Clear();
                _journalSaves = 0;
            }
            catch (Exception ex) { MelonLogger.Error("[LB] Failed to save stats: " + ex.Message); }
        }

        static void WriteFull(string path, StatsData data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(data, Formatting.None));
            if (File.Exists(path)) File.Replace(tmp, path, path + ".bak");
            else File.Move(tmp, path);
        }

        static void TruncateJournal(string journal)
        {
            try { if (File.Exists(journal)) File.Delete(journal); } catch { }
        }

        /// <summary>Replay journal lines written since the last full save. Returns the number applied.</summary>
        int ReplayJournal()
        {
            string journal = JournalPath;
            if (!File.Exists(journal)) return 0;
            int n = 0;
            try
            {
                foreach (var line in File.ReadLines(journal))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    Delta? d = null;
                    try { d = JsonConvert.DeserializeObject<Delta>(line); } catch { continue; }
                    if (d == null) continue;
                    foreach (var kv in d.players) Data.players[kv.Key] = kv.Value;
                    if (d.rounds_live > Data.rounds_live) Data.rounds_live = d.rounds_live;
                    n++;
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[LB] Journal replay: " + ex.Message); }
            return n;
        }

        public string ResetSeason()
        {
            string backup = Path.Combine(Path.GetDirectoryName(_path)!, $"stats_backup_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
            try
            {
                int spins = 0;
                while (Volatile.Read(ref _saveRunning) != 0 && spins++ < 300) Thread.Sleep(10);
                if (File.Exists(_path)) File.Copy(_path, backup, true);
                string j = JournalPath;
                if (File.Exists(j)) File.Copy(j, backup + ".journal.jsonl", true);
            }
            catch (Exception ex) { MelonLogger.Error("[LB] Backup before reset failed: " + ex.Message); }
            Data = new StatsData { source = "season reset" };
            _rankCache.Clear();
            _dirtyKeys.Clear();
            SaveNow();
            return backup;
        }

        // ---- access ---------------------------------------------------------
        public PlayerStats? Get(string steam) => Data.players.TryGetValue(steam, out var p) ? p : null;

        public PlayerStats GetOrCreate(string steam, string name)
        {
            if (!Data.players.TryGetValue(steam, out var p))
            {
                p = new PlayerStats { name = name };
                p.EnsureBuckets(_cfg.StartRating);
                Data.players[steam] = p;
            }
            if (!string.IsNullOrEmpty(name)) p.name = name;
            _dirtyKeys.Add(steam);
            return p;
        }

        void Touch(PlayerStats p)
        {
            p.last_seen = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            _dirty = true;
            _rankCache.Clear();
        }

        static double Expected(double ra, double rb) => 1.0 / (1.0 + Math.Pow(10.0, (rb - ra) / 400.0));

        // ---- events ---------------------------------------------------------
        /// <summary>A player-piloted unit killed something. victimSteam null = AI victim.</summary>
        public void RecordKill(string killerSteam, string killerName, string killerFaction,
                               string? victimSteam, string? victimName, string? victimFaction, bool victimIsStructure)
        {
            var k = GetOrCreate(killerSteam, killerName);
            var kf = k.Get(killerFaction, _cfg.StartRating);
            var ka = k.Get(ALL, _cfg.StartRating);
            if (victimIsStructure)
            {
                kf.structure_kills++; ka.structure_kills++;
                Touch(k);
                return;
            }
            if (victimSteam == null)
            {
                if (!_cfg.TrackAiKills) return;
                kf.kills_ai++; ka.kills_ai++;
                Touch(k);
                return;
            }
            if (victimSteam == killerSteam) return;

            var v = GetOrCreate(victimSteam, victimName ?? victimSteam);
            var vf = v.Get(victimFaction ?? ALL, _cfg.StartRating);
            var va = v.Get(ALL, _cfg.StartRating);
            kf.kills_h++; ka.kills_h++;
            vf.deaths_h++; va.deaths_h++;

            // duel Elo, faction buckets and the All bucket separately
            double eF = Expected(kf.rating, vf.rating);
            kf.rating += _cfg.DuelK * (1.0 - eF);
            vf.rating -= _cfg.DuelK * (1.0 - eF);
            double eA = Expected(ka.rating, va.rating);
            ka.rating += _cfg.DuelK * (1.0 - eA);
            va.rating -= _cfg.DuelK * (1.0 - eA);
            kf.duels++; ka.duels++; kf.duel_wins++; ka.duel_wins++;
            vf.duels++; va.duels++;
            Touch(k); Touch(v);
        }

        /// <summary>An AI unit (or unresolved attacker) killed a player-piloted unit.</summary>
        public void RecordDeathByAi(string victimSteam, string victimName, string victimFaction)
        {
            if (!_cfg.TrackAiKills) return;
            var v = GetOrCreate(victimSteam, victimName);
            v.Get(victimFaction, _cfg.StartRating).deaths_ai++;
            v.Get(ALL, _cfg.StartRating).deaths_ai++;
            Touch(v);
        }

        public void AddPresence(string steam, string name, string faction, double seconds)
        {
            var p = GetOrCreate(steam, name);
            p.Get(faction, _cfg.StartRating).time_s += seconds;
            p.Get(ALL, _cfg.StartRating).time_s += seconds;
            _dirty = true;
        }

        public void AddRound(string steam, string name, string faction)
        {
            var p = GetOrCreate(steam, name);
            p.Get(faction, _cfg.StartRating).rounds++;
            p.Get(ALL, _cfg.StartRating).rounds++;
            Touch(p);
        }

        // ---- rankings -------------------------------------------------------
        /// <summary>Players ranked by duel rating on a faction (or All), only those with enough duels.</summary>
        public List<KeyValuePair<string, PlayerStats>> Ranking(string faction)
        {
            if (_rankCache.TryGetValue(faction, out var cached)) return cached;
            var list = Data.players
                .Where(kv => kv.Value.f.TryGetValue(faction, out var s) && s.duels >= _cfg.MinDuelsForRank)
                .OrderByDescending(kv => kv.Value.f[faction].rating)
                .ToList();
            _rankCache[faction] = list;
            return list;
        }

        /// <summary>1-based rank of a player on a faction, 0 if not ranked.</summary>
        public int RankOf(string steam, string faction)
        {
            var list = Ranking(faction);
            for (int i = 0; i < list.Count; i++) if (list[i].Key == steam) return i + 1;
            return 0;
        }
    }
}
