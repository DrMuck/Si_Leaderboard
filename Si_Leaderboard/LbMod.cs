/*
 Si_Leaderboard - per-faction leaderboards for Silica dedicated servers.

 - FPS: duel Elo, K/D, kills per faction, collected from two cheap Harmony postfixes
   (MP_Strategy.OnUnitDestroyed / OnStructureDestroyed - the same hooks Si_Logging and
   Si_ReplayLogging use; GameEvents.OnUnitDestroyed hands over the wrong instigator).
 - Commanders: read from Si_EloMatchmaking's ratings.json (per-faction commander Elo).
 - /lboard chat menu (same /1-/N navigation as /buy), /lboard me, /lboard <name>.

 Cost: O(1) per kill, one presence poll every 10 s, one file write per round.
 Server-only. Deploy: <server>\Mods\Si_Leaderboard.dll
 Data:   UserData\cfg\Leaderboard\stats.json (+ Si_Leaderboard_Config.json)
*/
using HarmonyLib;
using MelonLoader;
using SilicaAdminMod;
using System;
using System.Collections.Generic;
using UnityEngine;

[assembly: MelonInfo(typeof(Si_Leaderboard.LbMod), "Leaderboard", Si_Leaderboard.LbMod.Version, "DrMuck")]
[assembly: MelonGame("Bohemia Interactive", "Silica")]
[assembly: MelonOptionalDependencies("Admin Mod", "EloMatchmaking")]

namespace Si_Leaderboard
{
    public partial class LbMod : MelonMod
    {
        public const string Version = "0.1.3";
        public static LbConfig Cfg = new LbConfig();
        public static StatsDb Db = null!;
        public static EloBridge Elo = null!;

        static bool _roundActive;
        static float _lastPresencePoll;
        static float _lastAutoSave;
        // players seen on a faction this round (for the rounds counter)
        static readonly Dictionary<string, (string name, string faction, double seconds)> _roundPresence = new Dictionary<string, (string, string, double)>();

        public override void OnInitializeMelon()
        {
            Cfg = ConfigStore.Load();
            Db = new StatsDb(ConfigStore.StatsPath, Cfg);
            Db.Load();
            Elo = new EloBridge(ConfigStore.EloRatingsPath);
            MelonLogger.Msg($"[LB] Si_Leaderboard v{Version} loaded. Enabled={Cfg.Enabled} duelK={Cfg.DuelK} minDuels={Cfg.MinDuelsForRank} elo={(Elo.Available ? "found" : "not found")} ({ConfigStore.EloRatingsPath})");
        }

        public override void OnLateInitializeMelon()
        {
            RegisterCommands();
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            ReHookChat();
        }

        public override void OnApplicationQuit()
        {
            try { Db.SaveNow(); } catch { }
        }

        public override void OnUpdate()
        {
            if (!_roundActive || !Cfg.Enabled) return;
            try
            {
                float now = Time.time;
                if (now - _lastPresencePoll >= Cfg.PresencePollSeconds)
                {
                    float dt = now - _lastPresencePoll;
                    _lastPresencePoll = now;
                    PollPresence(dt);
                }
                if (Cfg.AutoSaveSeconds > 0 && now - _lastAutoSave >= Cfg.AutoSaveSeconds)
                {
                    _lastAutoSave = now;
                    Db.Save();
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error("[LB] OnUpdate: " + ex.Message);
            }
        }

        // =====================================================================
        // helpers
        // =====================================================================
        public static string SteamId(Player p)
        {
            try { return p.PlayerID.m_ID.ToString(); } catch { return "0"; }
        }

        public static string Faction(Team? t)
        {
            if (t == null) return "";
            string n = t.TeamShortName ?? "";
            if (n.StartsWith("Sol", StringComparison.OrdinalIgnoreCase)) return "Sol";
            if (n.StartsWith("Cent", StringComparison.OrdinalIgnoreCase)) return "Centauri";
            if (n.StartsWith("Alien", StringComparison.OrdinalIgnoreCase)) return "Alien";
            return "";
        }

        public static string Short(string faction) => faction == "Centauri" ? "Cent" : faction;

        static bool IsServerPlayer(Player p)
        {
            try { return p == NetworkGameServer.GetServerPlayer(); } catch { return false; }
        }

        static void PollPresence(float dt)
        {
            foreach (var p in Player.Players)
            {
                if (p == null || IsServerPlayer(p)) continue;
                string f = Faction(p.Team);
                if (f.Length == 0) continue;
                string steam = SteamId(p);
                string name = p.PlayerName ?? steam;
                Db.AddPresence(steam, name, f, dt);
                _roundPresence.TryGetValue(steam, out var cur);
                // the faction with the most time this round gets the round credit
                if (cur.faction == f || cur.faction == null) _roundPresence[steam] = (name, f, cur.seconds + dt);
                else if (dt > cur.seconds) _roundPresence[steam] = (name, f, dt);
            }
        }

        // =====================================================================
        // round lifecycle
        // =====================================================================
        [HarmonyPatch(typeof(MusicJukeboxHandler), "OnGameStarted")]
        static class Patch_GameStarted
        {
            static void Postfix(GameMode __0)
            {
                try
                {
                    _roundActive = true;
                    _roundPresence.Clear();
                    _lastPresencePoll = Time.time;
                    _lastAutoSave = Time.time;
                }
                catch (Exception ex) { MelonLogger.Error("[LB] OnGameStarted: " + ex.Message); }
            }
        }

        [HarmonyPatch(typeof(MusicJukeboxHandler), "OnGameEnded")]
        static class Patch_GameEnded
        {
            static void Postfix(GameMode __0, Team __1)
            {
                try
                {
                    if (!_roundActive) return;
                    _roundActive = false;
                    foreach (var kv in _roundPresence)
                        if (kv.Value.seconds >= 60) Db.AddRound(kv.Key, kv.Value.name, kv.Value.faction);
                    _roundPresence.Clear();
                    Db.Data.rounds_live++;
                    Db.Save(true);
                    if (Cfg.Debug) MelonLogger.Msg("[LB] Round ended, stats saved.");
                }
                catch (Exception ex) { MelonLogger.Error("[LB] OnGameEnded: " + ex.Message); }
            }
        }

        // =====================================================================
        // kills
        // =====================================================================
        static Player? AttackerPlayer(BaseGameObject? attackerBase)
        {
            if (attackerBase == null) return null;
            try
            {
                var owner = attackerBase.NetworkComponent?.OwnerPlayer;
                if (owner != null) return owner;
            }
            catch { }
            try
            {
                if (attackerBase is Unit u) return u.ControlledBy;
            }
            catch { }
            return null;
        }

        [HarmonyPatch(typeof(MP_Strategy), "OnUnitDestroyed")]
        static class Patch_UnitDestroyed
        {
            static void Postfix(MP_Strategy __instance, Unit __0, GameObject __1)
            {
                if (!Cfg.Enabled || !_roundActive) return;
                try
                {
                    var victim = __0;
                    if (victim == null) return;
                    Player? victimPlayer = null;
                    try { victimPlayer = victim.ControlledBy; } catch { }
                    BaseGameObject? attackerBase = null;
                    if (__1 != null)
                    {
                        try { attackerBase = GameFuncs.GetBaseGameObject(__1); } catch { }
                    }
                    Player? attacker = AttackerPlayer(attackerBase);
                    if (attacker != null && IsServerPlayer(attacker)) attacker = null;
                    if (victimPlayer != null && IsServerPlayer(victimPlayer)) victimPlayer = null;
                    if (attacker == null && victimPlayer == null) return;   // AI vs AI: nothing to record

                    Team? victimTeam = null;
                    try { victimTeam = victim.Team; } catch { }
                    Team? attackerTeam = null;
                    try { attackerTeam = attackerBase?.Team; } catch { }
                    if (attacker != null && attackerTeam == null) attackerTeam = attacker.Team;

                    string vFaction = Faction(victimTeam);
                    string aFaction = Faction(attackerTeam);
                    bool friendly = victimTeam != null && attackerTeam != null && victimTeam == attackerTeam;

                    if (attacker != null && !friendly && aFaction.Length > 0)
                    {
                        Db.RecordKill(SteamId(attacker), attacker.PlayerName ?? "", aFaction,
                                      victimPlayer != null ? SteamId(victimPlayer) : null,
                                      victimPlayer?.PlayerName,
                                      vFaction.Length > 0 ? vFaction : null,
                                      victimIsStructure: false);
                    }
                    else if (victimPlayer != null && !friendly && vFaction.Length > 0)
                    {
                        // killed by AI (or unresolved attacker)
                        Db.RecordDeathByAi(SteamId(victimPlayer), victimPlayer.PlayerName ?? "", vFaction);
                    }
                }
                catch (Exception ex)
                {
                    if (Cfg.Debug) MelonLogger.Warning("[LB] OnUnitDestroyed: " + ex.Message);
                }
            }
        }

        [HarmonyPatch(typeof(MP_Strategy), "OnStructureDestroyed")]
        static class Patch_StructureDestroyed
        {
            static void Postfix(MP_Strategy __instance, Structure __0, GameObject __1)
            {
                if (!Cfg.Enabled || !_roundActive) return;
                try
                {
                    if (__0 == null || __1 == null) return;
                    BaseGameObject? attackerBase = null;
                    try { attackerBase = GameFuncs.GetBaseGameObject(__1); } catch { }
                    Player? attacker = AttackerPlayer(attackerBase);
                    if (attacker == null || IsServerPlayer(attacker)) return;
                    Team? sTeam = null;
                    try { sTeam = __0.Team; } catch { }
                    Team? aTeam = null;
                    try { aTeam = attackerBase?.Team; } catch { }
                    if (aTeam == null) aTeam = attacker.Team;
                    if (sTeam != null && aTeam != null && sTeam == aTeam) return;
                    string aFaction = Faction(aTeam);
                    if (aFaction.Length == 0) return;
                    Db.RecordKill(SteamId(attacker), attacker.PlayerName ?? "", aFaction, null, null, Faction(sTeam), victimIsStructure: true);
                }
                catch (Exception ex)
                {
                    if (Cfg.Debug) MelonLogger.Warning("[LB] OnStructureDestroyed: " + ex.Message);
                }
            }
        }
    }
}
