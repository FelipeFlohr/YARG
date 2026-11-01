using UnityEngine;
using UnityEngine.AddressableAssets;
using YARG.Core.Engine.Vocals;
using YARG.Helpers.Extensions;
using YARG.Helpers;

namespace YARG.Menu.ScoreScreen
{
    public class VocalsScoreCard : ScoreCard<VocalsStats>
    {
        public override Sprite GetInstrumentSprite() => SyncAddressables.Load<Sprite>($"InstrumentIcons[{Player.Profile.CurrentInstrument.ToResourceName()}]");

        // Vocals has no advanced stats
        public override void SetAdvancedStatsShown(bool showAdvanced)
        {
        }
    }
}