using System.Collections.Generic;

namespace YARG.Multiplayer.Gameplay
{
    public static class BandResult
    {
        /// <summary>
        /// Computes the band score (sum of every player's total and band bonus score) and the star count.
        /// A star is earned for each threshold the score is strictly above, with no fractional star.
        /// </summary>
        public static (int Score, int Stars) Compute(
            IEnumerable<(int TotalScore, int BandBonusScore)> players, int[] starScoreThresholds)
        {
            int score = 0;
            foreach (var (total, bonus) in players)
            {
                score += total + bonus;
            }

            int stars = 0;
            foreach (int threshold in starScoreThresholds)
            {
                if (score > threshold)
                {
                    stars++;
                }
            }

            return (score, stars);
        }
    }
}
