/*
 Menu.cs - /lboard chat menu and direct commands (registered through Si_AdminMod).

   /lboard                 open the menu (toggle)
     /1 Commanders  -> /1 Sol  /2 Centauri  /3 Alien  /4 Overall
     /2 FPS players -> /1 Sol  /2 Centauri  /3 Alien  /4 Overall
     /3 My stats
     /back, /0 close
   /lboard com [sol|cent|alien|all]      commander top list directly
   /lboard fps [sol|cent|alien|all]      FPS top list directly
   /lboard me                            own stats and ranks
   /lboard <name>                        another player's stats
   admin (Power.Generic): /lboard reset confirm | /lboard reload | /lboard on|off | /lboard enabled on|off

 Navigation uses Event_Chat.OnRequestPlayerChat like Si_Buy, so /1-/N are consumed only while
 this menu is open. Opening /buy or /b while the menu is open closes it silently.
*/
using MelonLoader;
using SilicaAdminMod;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Si_Leaderboard
{
    public partial class LbMod
    {
        enum Level { Closed, Main, CommanderFaction, FpsFaction }

        class MenuState
        {
            public Level Level = Level.Closed;
        }

        static readonly Dictionary<long, MenuState> _menuStates = new Dictionary<long, MenuState>();
        static readonly string[] FactionChoices = { "Sol", "Centauri", "Alien", EloBridge.ALL };
        const string TAG = "<color=#5AD6C8>[LB]</color> ";

        static void RegisterCommands()
        {
            try
            {
                HelperMethods.RegisterPlayerCommand("lboard", OnLboard, true);
                HelperMethods.RegisterPlayerCommand("leaderboard", OnLboard, true);
                HookChat();
                MelonLogger.Msg("[LB] Registered /lboard.");
            }
            catch (Exception ex)
            {
                MelonLogger.Error("[LB] Command registration failed (Admin Mod missing?): " + ex.Message);
            }
        }

        static bool _chatHooked;
        static void HookChat()
        {
            if (_chatHooked) return;
            try
            {
                Event_Chat.OnRequestPlayerChat -= OnRequestChat_Nav;
                Event_Chat.OnRequestPlayerChat += OnRequestChat_Nav;
                _chatHooked = true;
            }
            catch (Exception ex) { MelonLogger.Warning("[LB] Chat hook failed: " + ex.Message); }
        }
        static void ReHookChat() { _chatHooked = false; HookChat(); }

        static void Say(Player? p, string msg)
        {
            try
            {
                if (p == null) MelonLogger.Msg("[LB] " + msg);
                else HelperMethods.SendChatMessageToPlayer(p, TAG + msg);
            }
            catch { }
        }

        static long Key(Player p)
        {
            try { return (long)p.PlayerID.m_ID; } catch { return 0; }
        }

        static bool IsAdmin(Player? p)
        {
            if (p == null) return true;
            try { return p.CanAdminExecute(Power.Generic); } catch { return false; }
        }

        // ---- other mods' menus (reflection, no compile-time dependency) ----------
        static System.Reflection.FieldInfo? _buyStates, _ubStates;
        static bool _probed;
        static bool OtherMenuOpen(long key)
        {
            if (!_probed)
            {
                _probed = true;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        var tb = asm.GetType("Si_Buy.Buy");
                        if (tb != null) _buyStates = tb.GetField("_buyStates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                        var tu = asm.GetType("Si_UnitBalance.Si_UnitBalance");
                        if (tu != null) _ubStates = tu.GetField("_menuStates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    }
                    catch { }
                }
            }
            return StateOpen(_buyStates, key) || StateOpen(_ubStates, key);
        }

        static bool StateOpen(System.Reflection.FieldInfo? field, long key)
        {
            if (field == null) return false;
            try
            {
                var dict = field.GetValue(null);
                if (dict == null) return false;
                var tryGet = dict.GetType().GetMethod("TryGetValue");
                if (tryGet == null) return false;
                var args = new object?[] { key, null };
                if (!(tryGet.Invoke(dict, args) is bool found) || !found || args[1] == null) return false;
                var state = args[1]!;
                var lvl = state.GetType().GetField("Level")?.GetValue(state) ?? state.GetType().GetProperty("Level")?.GetValue(state);
                if (lvl == null) return true;            // unknown shape: assume open while present
                return lvl.ToString() != "Closed";
            }
            catch { return false; }
        }

        // ---- entry -----------------------------------------------------------
        static void OnLboard(Player? caller, string args)
        {
            var parts = (args ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
            string rest = parts.Length > 2 ? string.Join(" ", parts.Skip(2)) : "";

            switch (sub)
            {
                case "on": case "off": case "enabled": case "reload": case "reset":
                    if (!IsAdmin(caller)) { Say(caller, "Admin only."); return; }
                    // "enabled on|off" = switch alias for the on/off words (used by Si_ModUI; "on"/"off" keep working).
                    if (sub == "enabled")
                    {
                        string en = rest.Trim().ToLowerInvariant();
                        if (en == "on" || en == "1" || en == "true") sub = "on";
                        else if (en == "off" || en == "0" || en == "false") sub = "off";
                        else { Say(caller, "Leaderboard tracking " + (Cfg.Enabled ? "ON" : "OFF") + "."); return; }
                    }
                    if (sub == "on") { Cfg.Enabled = true; ConfigStore.Save(Cfg); Say(caller, "Leaderboard tracking ON."); }
                    else if (sub == "off") { Cfg.Enabled = false; ConfigStore.Save(Cfg); Say(caller, "Leaderboard tracking OFF (lists still readable)."); }
                    else if (sub == "reload") { Db.Load(); Say(caller, $"Stats reloaded: {Db.Data.players.Count} players."); }
                    else
                    {
                        if (!rest.StartsWith("confirm", StringComparison.OrdinalIgnoreCase))
                        {
                            Say(caller, $"This wipes the FPS stats of {Db.Data.players.Count} players after a backup. Type: /lboard reset confirm   (commander ratings: !elo reset confirm)");
                            return;
                        }
                        string b = Db.ResetSeason();
                        try { HelperMethods.SendChatMessageToAll(TAG + "Season reset: FPS leaderboard starts from zero."); } catch { }
                        Say(caller, "Backup: " + System.IO.Path.GetFileName(b));
                    }
                    return;
                case "com": case "commander": case "commanders": case "rts":
                    ShowCommanders(caller, ParseFaction(rest));
                    return;
                case "fps": case "player": case "players":
                    ShowFps(caller, ParseFaction(rest));
                    return;
                case "me": case "my": case "stats":
                    if (caller == null) { Say(null, "Console has no stats."); return; }
                    ShowPersonal(caller, SteamId(caller), caller.PlayerName ?? "");
                    return;
                case "help": case "?":
                    Say(caller, "/lboard (menu) | /lboard com sol|cent|alien|all | /lboard fps sol|cent|alien|all | /lboard me | /lboard <name>");
                    return;
                case "":
                    break;
                default:
                {
                    if (int.TryParse(sub, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) || sub == "back" || sub == "0")
                    {
                        if (caller != null && _menuStates.TryGetValue(Key(caller), out var st)) { HandleNav(caller, st, sub); return; }
                    }
                    string needle = string.Join(" ", parts.Skip(1));
                    LookupByName(caller, needle);
                    return;
                }
            }

            if (caller == null) { Say(null, "Use /lboard com|fps <faction> from the console."); return; }
            long key = Key(caller);
            if (OtherMenuOpen(key))
            {
                Say(caller, "Close your open /buy or /b menu first, then /lboard again.");
                return;
            }
            if (!_menuStates.TryGetValue(key, out var state)) { state = new MenuState(); _menuStates[key] = state; }
            if (state.Level == Level.Closed) { state.Level = Level.Main; ShowMain(caller); }
            else { state.Level = Level.Closed; Say(caller, "Menu closed."); }
        }

        static string ParseFaction(string s)
        {
            s = (s ?? "").Trim().ToLowerInvariant();
            if (s.StartsWith("sol")) return "Sol";
            if (s.StartsWith("cen")) return "Centauri";
            if (s.StartsWith("ali")) return "Alien";
            return EloBridge.ALL;
        }

        // ---- navigation --------------------------------------------------------
        static void OnRequestChat_Nav(object? sender, OnRequestPlayerChatArgs args)
        {
            try
            {
                if (args.Player == null) return;
                long key = Key(args.Player);
                if (!_menuStates.TryGetValue(key, out var state) || state.Level == Level.Closed) return;
                string text = (args.Text ?? "").Trim();
                if (text.Length < 2 || text[0] != '/') return;
                string sub = text.Substring(1).ToLowerInvariant();
                // another menu is being opened: step aside
                if (sub == "buy" || sub.StartsWith("buy ") || sub == "b" || sub.StartsWith("b ") || sub == "balance")
                {
                    state.Level = Level.Closed;
                    return;
                }
                bool isNum = int.TryParse(sub, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
                if (!isNum && sub != "back" && sub != "0" && sub != "exit" && sub != "close") return;
                HandleNav(args.Player, state, sub);
                args.Block = true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[LB] chat nav: " + ex.Message);
            }
        }

        static void HandleNav(Player caller, MenuState state, string sub)
        {
            if (sub == "0" || sub == "exit" || sub == "close") { state.Level = Level.Closed; Say(caller, "Menu closed."); return; }
            if (sub == "back")
            {
                if (state.Level == Level.Main) { state.Level = Level.Closed; Say(caller, "Menu closed."); }
                else { state.Level = Level.Main; ShowMain(caller); }
                return;
            }
            if (!int.TryParse(sub, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1) { Say(caller, "Use /1-/4, /back, /0."); return; }
            switch (state.Level)
            {
                case Level.Main:
                    if (n == 1) { state.Level = Level.CommanderFaction; ShowFactionChoice(caller, "Commanders"); }
                    else if (n == 2) { state.Level = Level.FpsFaction; ShowFactionChoice(caller, "FPS players"); }
                    else if (n == 3) { ShowPersonal(caller, SteamId(caller), caller.PlayerName ?? ""); }
                    else Say(caller, "Invalid choice.");
                    break;
                case Level.CommanderFaction:
                    if (n <= FactionChoices.Length) ShowCommanders(caller, FactionChoices[n - 1]); else Say(caller, "Invalid choice.");
                    break;
                case Level.FpsFaction:
                    if (n <= FactionChoices.Length) ShowFps(caller, FactionChoices[n - 1]); else Say(caller, "Invalid choice.");
                    break;
            }
        }

        static void ShowMain(Player p)
        {
            Say(p, "Leaderboard:  /1 Commanders   /2 FPS players   /3 My stats   /0 close");
        }

        static void ShowFactionChoice(Player p, string what)
        {
            Say(p, $"{what}:  /1 Sol   /2 Centauri   /3 Alien   /4 Overall   /back");
        }

        // ---- views ---------------------------------------------------------------
        static string FColor(string faction)
        {
            switch (faction)
            {
                case "Sol": return "<color=#548DFF>";
                case "Centauri": return "<color=#FF5454>";
                case "Alien": return "<color=#54CC54>";
                default: return "<color=#FFFFFF>";
            }
        }

        static string Fmt(double v) => v.ToString("0", CultureInfo.InvariantCulture);
        static string Fmt1(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

        static void ShowCommanders(Player? p, string faction)
        {
            if (!Elo.Available) { Say(p, "Commander ratings not available (Si_EloMatchmaking ratings.json not found)."); return; }
            var list = Elo.Ranking(faction, faction == EloBridge.ALL ? Cfg.MinCommanderGamesForRank : Cfg.MinCommanderGamesForRank);
            Say(p, $"{FColor(faction)}Top {Cfg.TopN} commanders {(faction == EloBridge.ALL ? "overall" : faction)}</color> (rating, W/games, min {Cfg.MinCommanderGamesForRank} games) - {list.Count} ranked");
            int i = 1;
            foreach (var kv in list.Take(Cfg.TopN))
            {
                var c = kv.Value;
                Say(p, $"{i++}. {c.name}  {Fmt(EloBridge.FactionRating(c, faction))}  ({EloBridge.FactionWins(c, faction)}/{EloBridge.FactionGames(c, faction)})");
            }
            if (list.Count == 0) Say(p, "nobody ranked yet");
        }

        static void ShowFps(Player? p, string faction)
        {
            var list = Db.Ranking(faction);
            Say(p, $"{FColor(faction)}Top {Cfg.TopN} FPS players {(faction == EloBridge.ALL ? "overall" : faction)}</color> (duel rating, K/D vs players, min {Cfg.MinDuelsForRank} duels) - {list.Count} ranked");
            int i = 1;
            foreach (var kv in list.Take(Cfg.TopN))
            {
                var s = kv.Value.f[faction];
                Say(p, $"{i++}. {kv.Value.name}  {Fmt(s.rating)}  K/D {Fmt1(s.Kd)} ({s.kills_h}/{s.deaths_h}), {s.duel_wins}/{s.duels} duels");
            }
            if (list.Count == 0) Say(p, "nobody ranked yet");
        }

        static void ShowPersonal(Player? p, string steam, string name)
        {
            var c = Elo.Commander(steam);
            if (c == null) Say(p, $"{name} - Commander: no rated games yet.");
            else
            {
                int rank = Elo.RankOf(steam, EloBridge.ALL, Cfg.MinCommanderGamesForRank);
                var parts = new List<string>();
                foreach (var f in StatsDb.Factions)
                {
                    int g = EloBridge.FactionGames(c, f);
                    if (g == 0) continue;
                    int r = Elo.RankOf(steam, f, Cfg.MinCommanderGamesForRank);
                    parts.Add($"{FColor(f)}{Short(f)}</color> {Fmt(EloBridge.FactionRating(c, f))} {(r > 0 ? "#" + r : "-")} ({EloBridge.FactionWins(c, f)}/{g})");
                }
                Say(p, $"{name} - Commander: {Fmt(c.rating)} {(rank > 0 ? "#" + rank : "(unranked)")} {c.wins}-{c.losses} | " + string.Join("  ", parts));
            }

            var ps = Db.Get(steam);
            if (ps == null || !ps.f.TryGetValue(StatsDb.ALL, out var all)) { Say(p, $"{name} - FPS: no stats yet."); return; }
            int rAll = Db.RankOf(steam, StatsDb.ALL);
            Say(p, $"{name} - FPS overall: {Fmt(all.rating)} {(rAll > 0 ? "#" + rAll : "(unranked, " + all.duels + "/" + Cfg.MinDuelsForRank + " duels)")} | K/D {Fmt1(all.Kd)} ({all.kills_h}/{all.deaths_h} vs players), {all.kills_ai} AI kills, {all.deaths_ai} deaths by AI, {all.structure_kills} structures, {all.rounds} rounds, {Fmt(all.time_s / 3600)}h");
            var fparts = new List<string>();
            foreach (var f in StatsDb.Factions)
            {
                if (!ps.f.TryGetValue(f, out var s) || (s.kills_h + s.deaths_h + s.kills_ai) == 0) continue;
                int r = Db.RankOf(steam, f);
                fparts.Add($"{FColor(f)}{Short(f)}</color> {Fmt(s.rating)} {(r > 0 ? "#" + r : "-")} K/D {Fmt1(s.Kd)} ({s.duel_wins}/{s.duels})");
            }
            if (fparts.Count > 0) Say(p, "FPS by faction: " + string.Join("  ", fparts));
        }

        static void LookupByName(Player? caller, string needle)
        {
            if (string.IsNullOrWhiteSpace(needle)) { Say(caller, "Usage: /lboard <name>"); return; }
            string n = needle.ToLowerInvariant();
            // online player first
            Player? online = null;
            try { online = HelperMethods.FindTargetPlayer(needle); } catch { }
            if (online != null) { ShowPersonal(caller, SteamId(online), online.PlayerName ?? needle); return; }
            var hits = Db.Data.players.Where(kv => kv.Value.name.ToLowerInvariant().Contains(n))
                                      .OrderByDescending(kv => kv.Value.f.TryGetValue(StatsDb.ALL, out var a) ? a.rounds : 0)
                                      .Take(3).ToList();
            if (hits.Count == 0) { Say(caller, $"No player named like '{needle}'."); return; }
            foreach (var kv in hits) ShowPersonal(caller, kv.Key, kv.Value.name);
        }
    }
}
