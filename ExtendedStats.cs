using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;


namespace MatchZy
{
    /// <summary>
    /// Tracks KAST, RWS, flash assists, first kill/death, trade kill/death, bomb plant/defuse, and 1k rounds.
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
        private HashSet<int> _roundAliveT = new();
        private HashSet<int> _roundAliveCt = new();
        private Dictionary<int, (int playerUserId, int opponents)> _roundClutchAttempts = new();
        private bool _roundFirstKillProcessed = false;

        // ---- Per-match accumulated state (cleared on match start/reset) ----
        private Dictionary<ulong, int> _kastRounds = new();
        private Dictionary<ulong, float> _rwsTotal = new();
        private Dictionary<ulong, int> _flashAssists = new();
        private Dictionary<ulong, int> _tradeKills = new();
        private Dictionary<ulong, int> _tradeDeaths = new();
        private Dictionary<ulong, int> _bombPlantsCount = new();
        private Dictionary<ulong, int> _bombDefusesCount = new();
        private Dictionary<ulong, int> _kills1Rounds = new();
        private Dictionary<ulong, int> _sniperKills = new();
        private Dictionary<ulong, int> _firstKillsT = new();
        private Dictionary<ulong, int> _firstKillsCt = new();
        private Dictionary<ulong, int> _firstDeathsT = new();
        private Dictionary<ulong, int> _firstDeathsCt = new();
        private Dictionary<ulong, int> _oneV1Count = new();
        private Dictionary<ulong, int> _oneV1Wins = new();
        private Dictionary<ulong, int> _oneV2Count = new();
        private Dictionary<ulong, int> _oneV2Wins = new();
        private Dictionary<ulong, int> _oneV3Count = new();
        private Dictionary<ulong, int> _oneV3Wins = new();
        private Dictionary<ulong, int> _oneV4Count = new();
        private Dictionary<ulong, int> _oneV4Wins = new();
        private Dictionary<ulong, int> _oneV5Count = new();
        private Dictionary<ulong, int> _oneV5Wins = new();
        private int _lastExtendedRoundProcessed = -1;
        private int? _roundBombPlantedUserId;
        private int? _roundBombDefusedUserId;

        private sealed class ExtendedPlayerStatsSnapshot
        {
            public int Kast { get; init; }
            public float Rws { get; init; }
            public int FlashAssists { get; init; }
            public int TradeKills { get; init; }
            public int TradeDeaths { get; init; }
            public int BombPlants { get; init; }
            public int BombDefuses { get; init; }
            public int Kills1 { get; init; }
            public int SniperKills { get; init; }
            public int FirstKillsT { get; init; }
            public int FirstKillsCt { get; init; }
            public int FirstDeathsT { get; init; }
            public int FirstDeathsCt { get; init; }
            public int OneV1Count { get; init; }
            public int OneV1Wins { get; init; }
            public int OneV2Count { get; init; }
            public int OneV2Wins { get; init; }
            public int OneV3Count { get; init; }
            public int OneV3Wins { get; init; }
            public int OneV4Count { get; init; }
            public int OneV4Wins { get; init; }
            public int OneV5Count { get; init; }
            public int OneV5Wins { get; init; }
        }

        /// <summary>Clear all accumulated stats — call when a match starts or resets.</summary>
        public void InitExtendedStats()
        {
            _kastRounds.Clear();
            _rwsTotal.Clear();
            _flashAssists.Clear();
            _tradeKills.Clear();
            _tradeDeaths.Clear();
            _bombPlantsCount.Clear();
            _bombDefusesCount.Clear();
            _kills1Rounds.Clear();
            _sniperKills.Clear();
            _firstKillsT.Clear();
            _firstKillsCt.Clear();
            _firstDeathsT.Clear();
            _firstDeathsCt.Clear();
            _oneV1Count.Clear();
            _oneV1Wins.Clear();
            _oneV2Count.Clear();
            _oneV2Wins.Clear();
            _oneV3Count.Clear();
            _oneV3Wins.Clear();
            _oneV4Count.Clear();
            _oneV4Wins.Clear();
            _oneV5Count.Clear();
            _oneV5Wins.Clear();
            _lastExtendedRoundProcessed = -1;
            ResetRoundExtendedStats();
        }

        public void StartRoundExtendedStats()
        {
            ResetRoundExtendedStats();

            foreach (int userId in playerData.Keys)
            {
                var player = playerData[userId];
                if (!player.IsValid) continue;

                if (player.TeamNum == 2)
                {
                    _roundAliveT.Add(userId);
                }
                else if (player.TeamNum == 3)
                {
                    _roundAliveCt.Add(userId);
                }
            }

            RegisterClutchAttemptIfNeeded(2);
            RegisterClutchAttemptIfNeeded(3);
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
            _roundAliveT.Clear();
            _roundAliveCt.Clear();
            _roundClutchAttempts.Clear();
            _roundFirstKillProcessed = false;
            _roundBombPlantedUserId = null;
            _roundBombDefusedUserId = null;
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
            bool isEnemyKill = IsPlayerValid(attacker) && attacker!.UserId.HasValue && attacker != victim && attacker.TeamNum != victim.TeamNum;

            if (IsPlayerValid(attacker) && attacker!.UserId.HasValue)
            {
                attackerId = (int)attacker.UserId;

                if (isEnemyKill)
                {
                    _roundKillCount.TryGetValue(attackerId, out int kc);
                    _roundKillCount[attackerId] = kc + 1;

                    if (!_roundFirstKillProcessed)
                    {
                        _roundFirstKillProcessed = true;
                        IncrementSideStat(attacker.SteamID, attacker.TeamNum, _firstKillsT, _firstKillsCt);
                        IncrementSideStat(victim.SteamID, victim.TeamNum, _firstDeathsT, _firstDeathsCt);
                    }

                    if (IsSniperWeapon(@event.Weapon))
                    {
                        IncrementStat(_sniperKills, attacker.SteamID);
                    }

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

                    // Trade kill: attacker killed someone who killed attacker's teammate within 5s.
                    foreach (var death in _roundDeathLog)
                    {
                        if (death.attackerId == victimId && gameTime - death.time <= 5.0f)
                        {
                            if (playerData.TryGetValue(death.victimId, out var deadTeammate) &&
                                deadTeammate.IsValid && deadTeammate.TeamNum == attacker.TeamNum)
                            {
                                IncrementStat(_tradeKills, attacker.SteamID);
                                break;
                            }
                        }
                    }
                }
            }

            // Track assist for KAST
            if (IsPlayerValid(assister) && assister!.UserId.HasValue)
            {
                _roundAssisted[(int)assister.UserId] = true;
            }

            RemoveAlivePlayer(victimId, victim.TeamNum);
            RegisterClutchAttemptIfNeeded(victim.TeamNum);

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
            if (player.UserId.HasValue)
                _roundBombPlantedUserId = (int)player.UserId.Value;
            IncrementStat(_bombPlantsCount, player.SteamID);
        }

        /// <summary>Call from EventBombDefused handler.</summary>
        public void TrackBombDefuse(CCSPlayerController player)
        {
            if (!isMatchLive || !IsPlayerValid(player)) return;
            if (player.UserId.HasValue)
                _roundBombDefusedUserId = (int)player.UserId.Value;
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

                int totalRoundDamage = 0;
                foreach (int damage in _roundEnemyDamage.Values)
                    totalRoundDamage += damage;

                int? objectiveBonusUserId = winnerTeamNum switch
                {
                    2 => _roundBombPlantedUserId,
                    3 => _roundBombDefusedUserId,
                    _ => null,
                };

                float damageSharePool = objectiveBonusUserId.HasValue ? 70f : 100f;

                // Determine traded players: a dead player whose killer was killed by the dead player's
                // teammate within 5s. This is also the T in KAST.
                HashSet<int> tradedPlayerIds = new();
                foreach (var death in _roundDeathLog)
                {
                    if (!playerData.TryGetValue(death.victimId, out var victim) || !victim.IsValid)
                        continue;
                    if (!playerData.TryGetValue(death.attackerId, out var killer) || !killer.IsValid)
                        continue;
                    if (killer.TeamNum == victim.TeamNum)
                        continue;

                    foreach (var subsequent in _roundDeathLog)
                    {
                        if (subsequent.victimId != death.attackerId ||
                            subsequent.time < death.time ||
                            subsequent.time - death.time > 5.0f)
                        {
                            continue;
                        }

                        if (!playerData.TryGetValue(subsequent.attackerId, out var tradeAttacker) || !tradeAttacker.IsValid)
                            continue;
                        if (tradeAttacker.TeamNum != victim.TeamNum)
                            continue;

                        tradedPlayerIds.Add(death.victimId);
                        break;
                    }
                }

                foreach (int tradedPlayerId in tradedPlayerIds)
                {
                    if (playerData.TryGetValue(tradedPlayerId, out var tradedPlayer) && tradedPlayer.IsValid)
                    {
                        IncrementStat(_tradeDeaths, tradedPlayer.SteamID);
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
                        int playerDamage = _roundEnemyDamage.GetValueOrDefault(userId, 0);
                        float rwsShare = totalRoundDamage > 0 ? (float)playerDamage / totalRoundDamage * damageSharePool : 0f;

                        if (objectiveBonusUserId.HasValue && objectiveBonusUserId.Value == userId)
                            rwsShare += 30f;

                        _rwsTotal.TryGetValue(steamId, out float rwsAcc);
                        _rwsTotal[steamId] = rwsAcc + rwsShare;
                    }
                }

                foreach (var clutchAttempt in _roundClutchAttempts.Values)
                {
                    if (!playerData.TryGetValue(clutchAttempt.playerUserId, out var player) || !player.IsValid)
                        continue;

                    if (player.TeamNum != winnerTeamNum)
                        continue;

                    if (deadPlayerIds.Contains(clutchAttempt.playerUserId))
                        continue;

                    IncrementClutchStat(clutchAttempt.opponents, player.SteamID, isWin: true);
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
        private ExtendedPlayerStatsSnapshot GetExtendedStatsForPlayer(ulong steamId, int roundsPlayed)
        {
            int kast = 0;
            if (roundsPlayed > 0 && _kastRounds.TryGetValue(steamId, out int kastR))
                kast = (int)Math.Round(100.0 * kastR / roundsPlayed);

            float rws = 0;
            if (roundsPlayed > 0 && _rwsTotal.TryGetValue(steamId, out float rwsT))
                rws = rwsT / roundsPlayed;

            _flashAssists.TryGetValue(steamId, out int fa);
            _tradeKills.TryGetValue(steamId, out int tk);
            _tradeDeaths.TryGetValue(steamId, out int td);
            _bombPlantsCount.TryGetValue(steamId, out int bp);
            _bombDefusesCount.TryGetValue(steamId, out int bd);
            _kills1Rounds.TryGetValue(steamId, out int k1);

            return new ExtendedPlayerStatsSnapshot
            {
                Kast = kast,
                Rws = rws,
                FlashAssists = fa,
                TradeKills = tk,
                TradeDeaths = td,
                BombPlants = bp,
                BombDefuses = bd,
                Kills1 = k1,
                SniperKills = _sniperKills.GetValueOrDefault(steamId, 0),
                FirstKillsT = _firstKillsT.GetValueOrDefault(steamId, 0),
                FirstKillsCt = _firstKillsCt.GetValueOrDefault(steamId, 0),
                FirstDeathsT = _firstDeathsT.GetValueOrDefault(steamId, 0),
                FirstDeathsCt = _firstDeathsCt.GetValueOrDefault(steamId, 0),
                OneV1Count = _oneV1Count.GetValueOrDefault(steamId, 0),
                OneV1Wins = _oneV1Wins.GetValueOrDefault(steamId, 0),
                OneV2Count = _oneV2Count.GetValueOrDefault(steamId, 0),
                OneV2Wins = _oneV2Wins.GetValueOrDefault(steamId, 0),
                OneV3Count = _oneV3Count.GetValueOrDefault(steamId, 0),
                OneV3Wins = _oneV3Wins.GetValueOrDefault(steamId, 0),
                OneV4Count = _oneV4Count.GetValueOrDefault(steamId, 0),
                OneV4Wins = _oneV4Wins.GetValueOrDefault(steamId, 0),
                OneV5Count = _oneV5Count.GetValueOrDefault(steamId, 0),
                OneV5Wins = _oneV5Wins.GetValueOrDefault(steamId, 0),
            };
        }

        private void RemoveAlivePlayer(int userId, int teamNum)
        {
            if (teamNum == 2)
            {
                _roundAliveT.Remove(userId);
            }
            else if (teamNum == 3)
            {
                _roundAliveCt.Remove(userId);
            }
        }

        private void RegisterClutchAttemptIfNeeded(int teamNum)
        {
            if (_roundClutchAttempts.ContainsKey(teamNum))
                return;

            HashSet<int> aliveSet = teamNum == 2 ? _roundAliveT : _roundAliveCt;
            HashSet<int> opponentAliveSet = teamNum == 2 ? _roundAliveCt : _roundAliveT;

            if (aliveSet.Count != 1)
                return;

            int opponents = opponentAliveSet.Count;
            if (opponents < 1 || opponents > 5)
                return;

            int playerUserId = aliveSet.First();
            if (!playerData.TryGetValue(playerUserId, out var player) || !player.IsValid)
                return;

            _roundClutchAttempts[teamNum] = (playerUserId, opponents);
            IncrementClutchStat(opponents, player.SteamID, isWin: false);
        }

        private void IncrementClutchStat(int opponents, ulong steamId, bool isWin)
        {
            Dictionary<ulong, int> target = opponents switch
            {
                1 => isWin ? _oneV1Wins : _oneV1Count,
                2 => isWin ? _oneV2Wins : _oneV2Count,
                3 => isWin ? _oneV3Wins : _oneV3Count,
                4 => isWin ? _oneV4Wins : _oneV4Count,
                5 => isWin ? _oneV5Wins : _oneV5Count,
                _ => throw new ArgumentOutOfRangeException(nameof(opponents)),
            };

            IncrementStat(target, steamId);
        }

        private static void IncrementSideStat(ulong steamId, int teamNum, Dictionary<ulong, int> tStats, Dictionary<ulong, int> ctStats)
        {
            if (teamNum == 2)
            {
                IncrementStat(tStats, steamId);
            }
            else if (teamNum == 3)
            {
                IncrementStat(ctStats, steamId);
            }
        }

        private static bool IsSniperWeapon(string weapon)
        {
            string normalized = weapon.Replace("weapon_", string.Empty).ToLowerInvariant();
            return normalized == "awp" || normalized == "ssg08" || normalized == "scar20" || normalized == "g3sg1";
        }

        private static void IncrementStat(Dictionary<ulong, int> dict, ulong key)
        {
            dict.TryGetValue(key, out int val);
            dict[key] = val + 1;
        }
    }
}
