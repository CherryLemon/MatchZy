using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;


namespace MatchZy
{
    /// <summary>
    /// Tracks KAST, RWS, flash assists, trade kills, bomb plant/defuse, and 1k rounds.
    /// All state is accumulated per-match and reset when the match ends.
    /// </summary>
    public partial class MatchZy
    {
        // ---- Per-round transient state (cleared each round) ----
        private Dictionary<int, int> _roundKillCount = new();          // userId -> kills this round
        private Dictionary<int, bool> _roundAssisted = new();          // userId -> had assist this round
        private List<(int victimId, int attackerId, float time)> _roundDeathLog = new();
        private Dictionary<int, int> _roundEnemyDamage = new();        // userId -> damage dealt to enemies this round
        private Dictionary<int, (int flasherUserId, float blindEnd)> _blindedPlayers = new();

        // ---- Per-match accumulated state (cleared on match start/reset) ----
        private Dictionary<ulong, int> _kastRounds = new();
        private Dictionary<ulong, float> _rwsTotal = new();
        private Dictionary<ulong, int> _rwsWonRounds = new();
        private Dictionary<ulong, int> _flashAssists = new();
        private Dictionary<ulong, int> _tradeKills = new();
        private Dictionary<ulong, int> _bombPlantsCount = new();
        private Dictionary<ulong, int> _bombDefusesCount = new();
        private Dictionary<ulong, int> _kills1Rounds = new();
        private int _lastExtendedRoundProcessed = -1;

        /// <summary>Clear all accumulated stats — call when a match starts or resets.</summary>
        public void InitExtendedStats()
        {
            _kastRounds.Clear();
            _rwsTotal.Clear();
            _rwsWonRounds.Clear();
            _flashAssists.Clear();
            _tradeKills.Clear();
            _bombPlantsCount.Clear();
            _bombDefusesCount.Clear();
            _kills1Rounds.Clear();
            _lastExtendedRoundProcessed = -1;
            ResetRoundExtendedStats();
        }

        public void ProcessRoundEndExtendedStatsIfNeeded(int winnerTeamNum, int roundNumber)
        {
            if (roundNumber <= _lastExtendedRoundProcessed)
                return;

            ProcessRoundEndExtendedStats(winnerTeamNum);
            _lastExtendedRoundProcessed = roundNumber;
        }

        private void ResetRoundExtendedStats()
        {
            _roundKillCount.Clear();
            _roundAssisted.Clear();
            _roundDeathLog.Clear();
            _roundEnemyDamage.Clear();
            _blindedPlayers.Clear();
        }

        // ---- Event hooks (called from MatchZy.cs event registrations) ----

        /// <summary>Call from EventPlayerDeath handler when isMatchLive.</summary>
        public void TrackKill(EventPlayerDeath @event)
        {
            if (!isMatchLive) return;

            var victim = @event.Userid;
            var attacker = @event.Attacker;
            var assister = @event.Assister;

            if (!IsPlayerValid(victim)) return;

            int victimId = (int)victim!.UserId!;
            float gameTime = Server.CurrentTime;
            int attackerId = -1;

            if (IsPlayerValid(attacker) && attacker!.UserId.HasValue)
            {
                attackerId = (int)attacker.UserId;
                _roundKillCount.TryGetValue(attackerId, out int kc);
                _roundKillCount[attackerId] = kc + 1;

                // Flash assist: victim was blinded by a teammate of the attacker
                if (_blindedPlayers.TryGetValue(victimId, out var blindInfo) && blindInfo.blindEnd > gameTime)
                {
                    int flasherUserId = blindInfo.flasherUserId;
                    if (flasherUserId != attackerId && playerData.TryGetValue(flasherUserId, out var flasher))
                    {
                        if (flasher.IsValid && flasher.TeamNum == attacker.TeamNum)
                        {
                            IncrementStat(_flashAssists, flasher.SteamID);
                        }
                    }
                }

                // Trade kill: attacker killed someone who killed attacker's teammate within 5s
                foreach (var death in _roundDeathLog)
                {
                    if (death.attackerId == victimId && gameTime - death.time <= 5.0f)
                    {
                        // The person we just killed had killed one of our teammates recently
                        if (playerData.TryGetValue(death.victimId, out var deadTeammate) &&
                            deadTeammate.IsValid && deadTeammate.TeamNum == attacker.TeamNum)
                        {
                            IncrementStat(_tradeKills, attacker.SteamID);
                            break;
                        }
                    }
                }
            }

            // Track assist for KAST
            if (IsPlayerValid(assister) && assister!.UserId.HasValue)
            {
                _roundAssisted[(int)assister.UserId] = true;
            }

            _blindedPlayers.Remove(victimId);
            _roundDeathLog.Add((victimId, attackerId, gameTime));
        }

        /// <summary>Call from EventPlayerHurt when isMatchLive and teams differ.</summary>
        public void TrackDamage(int attackerUserId, int damage)
        {
            if (!isMatchLive) return;
            _roundEnemyDamage.TryGetValue(attackerUserId, out int d);
            _roundEnemyDamage[attackerUserId] = d + damage;
        }

        /// <summary>Call from EventPlayerBlind when isMatchLive.</summary>
        public void TrackBlind(int victimUserId, int flasherUserId, float blindDuration)
        {
            if (!isMatchLive) return;
            _blindedPlayers[victimUserId] = (flasherUserId, Server.CurrentTime + blindDuration);
        }

        /// <summary>Call from EventBombPlanted handler.</summary>
        public void TrackBombPlant(CCSPlayerController player)
        {
            if (!isMatchLive || !IsPlayerValid(player)) return;
            IncrementStat(_bombPlantsCount, player.SteamID);
        }

        /// <summary>Call from EventBombDefused handler.</summary>
        public void TrackBombDefuse(CCSPlayerController player)
        {
            if (!isMatchLive || !IsPlayerValid(player)) return;
            IncrementStat(_bombDefusesCount, player.SteamID);
        }

        /// <summary>
        /// Call at end of each live round (from HandlePostRoundEndEvent), BEFORE ResetRoundExtendedStats.
        /// winnerTeamNum is 2 (T) or 3 (CT).
        /// </summary>
        public void ProcessRoundEndExtendedStats(int winnerTeamNum)
        {
            try
            {
                HashSet<int> deadPlayerIds = new();
                foreach (var death in _roundDeathLog)
                    deadPlayerIds.Add(death.victimId);

                // Determine traded players: a dead player whose killer was also killed within 5s
                HashSet<int> tradedPlayerIds = new();
                foreach (var death in _roundDeathLog)
                {
                    foreach (var subsequent in _roundDeathLog)
                    {
                        if (subsequent.victimId == death.attackerId &&
                            subsequent.time >= death.time &&
                            subsequent.time - death.time <= 5.0f)
                        {
                            tradedPlayerIds.Add(death.victimId);
                            break;
                        }
                    }
                }

                foreach (int userId in playerData.Keys)
                {
                    var player = playerData[userId];
                    if (!player.IsValid) continue;
                    if (player.TeamNum != 2 && player.TeamNum != 3) continue;

                    ulong steamId = player.SteamID;

                    // ---- KAST ----
                    bool k = _roundKillCount.ContainsKey(userId) && _roundKillCount[userId] > 0;
                    bool a = _roundAssisted.ContainsKey(userId);
                    bool s = !deadPlayerIds.Contains(userId);
                    bool t = tradedPlayerIds.Contains(userId);

                    if (k || a || s || t)
                    {
                        IncrementStat(_kastRounds, steamId);
                    }

                    // ---- 1k ----
                    if (_roundKillCount.TryGetValue(userId, out int kills) && kills == 1)
                    {
                        IncrementStat(_kills1Rounds, steamId);
                    }

                    // ---- RWS ----
                    if (player.TeamNum == winnerTeamNum)
                    {
                        _rwsWonRounds.TryGetValue(steamId, out int wonR);
                        _rwsWonRounds[steamId] = wonR + 1;

                        int teamTotalDamage = 0;
                        foreach (int uid in playerData.Keys)
                        {
                            var p = playerData[uid];
                            if (p.IsValid && p.TeamNum == winnerTeamNum)
                                teamTotalDamage += _roundEnemyDamage.GetValueOrDefault(uid, 0);
                        }

                        int playerDamage = _roundEnemyDamage.GetValueOrDefault(userId, 0);
                        float rwsShare = teamTotalDamage > 0 ? (float)playerDamage / teamTotalDamage * 100f : 0f;

                        _rwsTotal.TryGetValue(steamId, out float rwsAcc);
                        _rwsTotal[steamId] = rwsAcc + rwsShare;
                    }
                }
            }
            catch (Exception e)
            {
                Log($"[ProcessRoundEndExtendedStats FATAL] An error occurred: {e.Message}");
            }
            finally
            {
                ResetRoundExtendedStats();
            }
        }

        /// <summary>Get accumulated extended stats for populating PlayerStats.</summary>
        public (int kast, float rws, int flashAssists, int tradeKills, int bombPlants, int bombDefuses, int kills1)
            GetExtendedStatsForPlayer(ulong steamId, int roundsPlayed)
        {
            int kast = 0;
            if (roundsPlayed > 0 && _kastRounds.TryGetValue(steamId, out int kastR))
                kast = (int)Math.Round(100.0 * kastR / roundsPlayed);

            float rws = 0;
            if (_rwsWonRounds.TryGetValue(steamId, out int wonR) && wonR > 0 && _rwsTotal.TryGetValue(steamId, out float rwsT))
                rws = rwsT / wonR;

            _flashAssists.TryGetValue(steamId, out int fa);
            _tradeKills.TryGetValue(steamId, out int tk);
            _bombPlantsCount.TryGetValue(steamId, out int bp);
            _bombDefusesCount.TryGetValue(steamId, out int bd);
            _kills1Rounds.TryGetValue(steamId, out int k1);

            return (kast, rws, fa, tk, bp, bd, k1);
        }

        private static void IncrementStat(Dictionary<ulong, int> dict, ulong key)
        {
            dict.TryGetValue(key, out int val);
            dict[key] = val + 1;
        }
    }
}
