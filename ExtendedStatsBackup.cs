using System.Text.Json;


namespace MatchZy
{
    public partial class MatchZy
    {
        private const int ExtendedStatsBackupVersion = 1;

        private sealed class ExtendedStatsBackupSnapshot
        {
            public int Version { get; set; } = ExtendedStatsBackupVersion;
            public int LastExtendedRoundProcessed { get; set; }
            public Dictionary<ulong, int> KastRounds { get; set; } = new();
            public Dictionary<ulong, float> RwsTotal { get; set; } = new();
            public Dictionary<ulong, int> FlashAssists { get; set; } = new();
            public Dictionary<ulong, int> TradeKills { get; set; } = new();
            public Dictionary<ulong, int> TradeDeaths { get; set; } = new();
            public Dictionary<ulong, int> BombPlantsCount { get; set; } = new();
            public Dictionary<ulong, int> BombDefusesCount { get; set; } = new();
            public Dictionary<ulong, int> Kills1Rounds { get; set; } = new();
            public Dictionary<ulong, int> SniperKills { get; set; } = new();
            public Dictionary<ulong, int> FirstKillsT { get; set; } = new();
            public Dictionary<ulong, int> FirstKillsCt { get; set; } = new();
            public Dictionary<ulong, int> FirstDeathsT { get; set; } = new();
            public Dictionary<ulong, int> FirstDeathsCt { get; set; } = new();
            public Dictionary<ulong, int> OneV1Count { get; set; } = new();
            public Dictionary<ulong, int> OneV1Wins { get; set; } = new();
            public Dictionary<ulong, int> OneV2Count { get; set; } = new();
            public Dictionary<ulong, int> OneV2Wins { get; set; } = new();
            public Dictionary<ulong, int> OneV3Count { get; set; } = new();
            public Dictionary<ulong, int> OneV3Wins { get; set; } = new();
            public Dictionary<ulong, int> OneV4Count { get; set; } = new();
            public Dictionary<ulong, int> OneV4Wins { get; set; } = new();
            public Dictionary<ulong, int> OneV5Count { get; set; } = new();
            public Dictionary<ulong, int> OneV5Wins { get; set; } = new();
            public Dictionary<ulong, SidePlayerStatsAccumulator> TSideStats { get; set; } = new();
            public Dictionary<ulong, SidePlayerStatsAccumulator> CtSideStats { get; set; } = new();
        }

        private string SerializeExtendedStatsBackup()
        {
            ExtendedStatsBackupSnapshot snapshot = new()
            {
                LastExtendedRoundProcessed = _lastExtendedRoundProcessed,
                KastRounds = _kastRounds,
                RwsTotal = _rwsTotal,
                FlashAssists = _flashAssists,
                TradeKills = _tradeKills,
                TradeDeaths = _tradeDeaths,
                BombPlantsCount = _bombPlantsCount,
                BombDefusesCount = _bombDefusesCount,
                Kills1Rounds = _kills1Rounds,
                SniperKills = _sniperKills,
                FirstKillsT = _firstKillsT,
                FirstKillsCt = _firstKillsCt,
                FirstDeathsT = _firstDeathsT,
                FirstDeathsCt = _firstDeathsCt,
                OneV1Count = _oneV1Count,
                OneV1Wins = _oneV1Wins,
                OneV2Count = _oneV2Count,
                OneV2Wins = _oneV2Wins,
                OneV3Count = _oneV3Count,
                OneV3Wins = _oneV3Wins,
                OneV4Count = _oneV4Count,
                OneV4Wins = _oneV4Wins,
                OneV5Count = _oneV5Count,
                OneV5Wins = _oneV5Wins,
                TSideStats = _tSideStats,
                CtSideStats = _ctSideStats,
            };
            return JsonSerializer.Serialize(snapshot);
        }

        private bool TryReadExtendedStatsBackup(
            Dictionary<string, string> backupData,
            int roundNumber,
            out ExtendedStatsBackupSnapshot? snapshot)
        {
            snapshot = null;
            if (!backupData.TryGetValue("extended_stats", out string? serialized) || string.IsNullOrWhiteSpace(serialized))
            {
                // Legacy backups are safe only for .stop on the current round,
                // where the in-memory accumulators already represent its start.
                return roundNumber == _lastExtendedRoundProcessed ||
                    (roundNumber == 0 && _lastExtendedRoundProcessed == -1);
            }

            try
            {
                snapshot = JsonSerializer.Deserialize<ExtendedStatsBackupSnapshot>(serialized);
            }
            catch (JsonException e)
            {
                Log($"[TryReadExtendedStatsBackup] Invalid extended stats JSON: {e.Message}");
                return false;
            }

            if (snapshot == null || snapshot.Version != ExtendedStatsBackupVersion)
            {
                Log($"[TryReadExtendedStatsBackup] Unsupported extended stats backup version: {snapshot?.Version}");
                return false;
            }

            if (!HasCompleteExtendedStatsBackup(snapshot))
            {
                Log("[TryReadExtendedStatsBackup] Extended stats backup is incomplete.");
                return false;
            }

            bool roundMatches = snapshot.LastExtendedRoundProcessed == roundNumber ||
                (roundNumber == 0 && snapshot.LastExtendedRoundProcessed == -1);
            if (!roundMatches)
            {
                Log($"[TryReadExtendedStatsBackup] Round mismatch: backup={snapshot.LastExtendedRoundProcessed}, requested={roundNumber}");
            }
            return roundMatches;
        }

        private static bool HasCompleteExtendedStatsBackup(ExtendedStatsBackupSnapshot snapshot)
        {
            bool SideStatsAreComplete(Dictionary<ulong, SidePlayerStatsAccumulator>? sideStats) =>
                sideStats != null && sideStats.Values.All(stats =>
                    stats != null &&
                    stats.ClutchCounts != null && stats.ClutchCounts.Length >= 6 &&
                    stats.ClutchWins != null && stats.ClutchWins.Length >= 6);

            return snapshot.KastRounds != null &&
                snapshot.RwsTotal != null &&
                snapshot.FlashAssists != null &&
                snapshot.TradeKills != null &&
                snapshot.TradeDeaths != null &&
                snapshot.BombPlantsCount != null &&
                snapshot.BombDefusesCount != null &&
                snapshot.Kills1Rounds != null &&
                snapshot.SniperKills != null &&
                snapshot.FirstKillsT != null &&
                snapshot.FirstKillsCt != null &&
                snapshot.FirstDeathsT != null &&
                snapshot.FirstDeathsCt != null &&
                snapshot.OneV1Count != null &&
                snapshot.OneV1Wins != null &&
                snapshot.OneV2Count != null &&
                snapshot.OneV2Wins != null &&
                snapshot.OneV3Count != null &&
                snapshot.OneV3Wins != null &&
                snapshot.OneV4Count != null &&
                snapshot.OneV4Wins != null &&
                snapshot.OneV5Count != null &&
                snapshot.OneV5Wins != null &&
                SideStatsAreComplete(snapshot.TSideStats) &&
                SideStatsAreComplete(snapshot.CtSideStats);
        }

        private void ApplyExtendedStatsBackup(ExtendedStatsBackupSnapshot? snapshot)
        {
            if (snapshot != null)
            {
                _kastRounds = snapshot.KastRounds;
                _rwsTotal = snapshot.RwsTotal;
                _flashAssists = snapshot.FlashAssists;
                _tradeKills = snapshot.TradeKills;
                _tradeDeaths = snapshot.TradeDeaths;
                _bombPlantsCount = snapshot.BombPlantsCount;
                _bombDefusesCount = snapshot.BombDefusesCount;
                _kills1Rounds = snapshot.Kills1Rounds;
                _sniperKills = snapshot.SniperKills;
                _firstKillsT = snapshot.FirstKillsT;
                _firstKillsCt = snapshot.FirstKillsCt;
                _firstDeathsT = snapshot.FirstDeathsT;
                _firstDeathsCt = snapshot.FirstDeathsCt;
                _oneV1Count = snapshot.OneV1Count;
                _oneV1Wins = snapshot.OneV1Wins;
                _oneV2Count = snapshot.OneV2Count;
                _oneV2Wins = snapshot.OneV2Wins;
                _oneV3Count = snapshot.OneV3Count;
                _oneV3Wins = snapshot.OneV3Wins;
                _oneV4Count = snapshot.OneV4Count;
                _oneV4Wins = snapshot.OneV4Wins;
                _oneV5Count = snapshot.OneV5Count;
                _oneV5Wins = snapshot.OneV5Wins;
                _tSideStats = snapshot.TSideStats;
                _ctSideStats = snapshot.CtSideStats;
                _lastExtendedRoundProcessed = snapshot.LastExtendedRoundProcessed;
            }
            ResetRoundExtendedStats();
        }
    }
}
