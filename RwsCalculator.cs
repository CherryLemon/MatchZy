namespace MatchZy
{
    /// <summary>Pure Round Win Share allocation rules, isolated for deterministic testing.</summary>
    public static class RwsCalculator
    {
        public const float RoundPool = 100f;
        public const float ObjectiveBonus = 30f;

        public static Dictionary<int, float> CalculateRoundShares(
            IReadOnlyDictionary<int, int> winningPlayerDamage,
            int? objectivePlayerUserId = null)
        {
            Dictionary<int, float> shares = winningPlayerDamage.Keys.ToDictionary(userId => userId, _ => 0f);
            if (shares.Count == 0)
            {
                return shares;
            }

            bool hasObjectiveBonus = objectivePlayerUserId.HasValue && shares.ContainsKey(objectivePlayerUserId.Value);
            float damagePool = hasObjectiveBonus ? RoundPool - ObjectiveBonus : RoundPool;
            long winningTeamDamage = winningPlayerDamage.Values.Sum(damage => (long)Math.Max(damage, 0));

            if (winningTeamDamage > 0)
            {
                foreach ((int userId, int damage) in winningPlayerDamage)
                {
                    shares[userId] = Math.Max(damage, 0) / (float)winningTeamDamage * damagePool;
                }
            }
            else
            {
                // RWS promises a fixed 100-point payout. With no winning-team
                // damage there is no damage ratio, so use a neutral equal split.
                float equalShare = damagePool / shares.Count;
                foreach (int userId in winningPlayerDamage.Keys)
                {
                    shares[userId] = equalShare;
                }
            }

            if (hasObjectiveBonus)
            {
                shares[objectivePlayerUserId!.Value] += ObjectiveBonus;
            }

            return shares;
        }
    }
}
