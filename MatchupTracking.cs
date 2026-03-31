using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;


namespace MatchZy
{
    /// <summary>
    /// Tracks per-player-vs-player matchup data across the entire match:
    /// kills, total damage, and damage dealt in rounds where the attacker killed the victim.
    /// </summary>
    public partial class MatchZy
    {
        // attackerSteamId → victimSteamId → MatchupEntry
        private Dictionary<ulong, Dictionary<ulong, MatchupEntry>> _matchMatchups = new();

        public void InitMatchupTracking()
        {
            _matchMatchups.Clear();
        }

        /// <summary>
        /// Called from EventPlayerHurt when match is live and teams differ.
        /// Accumulates total damage for the attacker→victim pair.
        /// </summary>
        public void TrackMatchupDamage(CCSPlayerController attacker, CCSPlayerController victim, int damage)
        {
            if (!isMatchLive) return;
            if (attacker.IsBot || victim.IsBot) return;

            ulong aSteamId = attacker.SteamID;
            ulong vSteamId = victim.SteamID;

            if (!_matchMatchups.TryGetValue(aSteamId, out var inner))
                _matchMatchups[aSteamId] = inner = new Dictionary<ulong, MatchupEntry>();
            if (!inner.TryGetValue(vSteamId, out var entry))
                inner[vSteamId] = entry = new MatchupEntry();

            entry.TotalDamage += damage;
        }

        /// <summary>
        /// Called from EventPlayerDeath when match is live.
        /// Increments kill count and records current-round damage as kill damage.
        /// Must be called AFTER UpdatePlayerDamageInfo so that per-round damage is up to date.
        /// </summary>
        public void TrackMatchupKill(CCSPlayerController attacker, CCSPlayerController victim)
        {
            if (!isMatchLive) return;
            if (attacker.IsBot || victim.IsBot) return;

            ulong aSteamId = attacker.SteamID;
            ulong vSteamId = victim.SteamID;

            if (!_matchMatchups.TryGetValue(aSteamId, out var inner))
                _matchMatchups[aSteamId] = inner = new Dictionary<ulong, MatchupEntry>();
            if (!inner.TryGetValue(vSteamId, out var entry))
                inner[vSteamId] = entry = new MatchupEntry();

            entry.Kills++;

            // Grab the damage dealt by attacker to victim THIS ROUND from the per-round tracker.
            // playerDamageInfo is keyed by UserId (int), not SteamID.
            int attackerUserId = (int)attacker.UserId!;
            int victimUserId = (int)victim.UserId!;
            if (playerDamageInfo.TryGetValue(attackerUserId, out var dmgDict) &&
                dmgDict.TryGetValue(victimUserId, out var dmgInfo))
            {
                entry.KillDamage += dmgInfo.DamageHP;
            }
        }

        /// <summary>Serialize matchup data for the webhook payload.</summary>
        public List<MatchupData> GetMatchupDataForWebhook()
        {
            var result = new List<MatchupData>();
            foreach (var (attackerSteamId, inner) in _matchMatchups)
            {
                foreach (var (victimSteamId, entry) in inner)
                {
                    if (entry.Kills == 0 && entry.TotalDamage == 0) continue;
                    result.Add(new MatchupData
                    {
                        AttackerSteamId = attackerSteamId.ToString(),
                        VictimSteamId = victimSteamId.ToString(),
                        Kills = entry.Kills,
                        TotalDamage = entry.TotalDamage,
                        KillDamage = entry.KillDamage,
                    });
                }
            }
            return result;
        }
    }

    public class MatchupEntry
    {
        public int Kills { get; set; }
        public int TotalDamage { get; set; }
        public int KillDamage { get; set; }
    }

    public class MatchupData
    {
        [JsonPropertyName("attacker_steamid")]
        public string AttackerSteamId { get; set; } = "";

        [JsonPropertyName("victim_steamid")]
        public string VictimSteamId { get; set; } = "";

        [JsonPropertyName("kills")]
        public int Kills { get; set; }

        [JsonPropertyName("total_damage")]
        public int TotalDamage { get; set; }

        [JsonPropertyName("kill_damage")]
        public int KillDamage { get; set; }
    }
}
