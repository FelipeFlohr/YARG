using System;
using System.Linq;
using UnityEngine.SceneManagement;
using YARG.Core;
using YARG.Core.Extensions;
using YARG.Core.Game;
using YARG.Core.Logging;
using YARG.Helpers;
using YARG.Menu.DifficultySelect;
using YARG.Menu.Web;
using YARG.Player;

namespace YARG.Song.Web
{
    /// <summary>
    /// Commands from the browser smoke tests (<c>window.yargTest.songCommand</c>), which cannot click through the
    /// difficulty menu of a fresh profile. Only honored when the page was opened with <c>?web-test</c>.
    /// </summary>
    internal static class WebSongTestHooks
    {
        private const string LOG_PREFIX = "[WebSongTest] ";

        private static bool? _enabled;

        private static bool Enabled
        {
            get
            {
                _enabled ??= Array.IndexOf(PlatformInfo.GetArgs(), "-web-test") >= 0;
                return _enabled.Value;
            }
        }

        /// <summary>Logs scene changes so the tests can wait for gameplay and the score screen.</summary>
        public static void Initialize()
        {
            if (!Enabled)
            {
                return;
            }

            SceneManager.activeSceneChanged += (_, scene) =>
                YargLogger.LogFormatInfo(LOG_PREFIX + "Scene: {0}", ((SceneIndex) scene.buildIndex).ToString());
        }

        public static void Handle(string message)
        {
            if (!Enabled)
            {
                YargLogger.LogWarning(LOG_PREFIX + "Ignoring a test command; open the page with ?web-test.");
                return;
            }

            int split = message.IndexOf('|');
            string command = split < 0 ? message : message.Substring(0, split);

            try
            {
                switch (command)
                {
                    case "bot":
                        EnsureBotPlayer();
                        break;
                    case "play":
                        Play();
                        break;
                    default:
                        YargLogger.LogFormatWarning(LOG_PREFIX + "Unknown command `{0}`", command);
                        return;
                }
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, LOG_PREFIX + $"Command `{command}` failed");
                return;
            }

            YargLogger.LogFormatInfo(LOG_PREFIX + "Done: {0}", command);
        }

        /// <summary>Makes sure exactly one active bot player exists, playing five-fret guitar.</summary>
        private static void EnsureBotPlayer()
        {
            var active = PlayerContainer.Players.FirstOrDefault(p => !p.SittingOut);
            if (active == null)
            {
                var profile = PlayerContainer.Profiles.FirstOrDefault(p => !PlayerContainer.IsProfileTaken(p));
                if (profile == null)
                {
                    var guitar = Instrument.FiveFretGuitar;
                    profile = new YargProfile
                    {
                        Name = "Web Test",
                        GameMode = guitar.ToNativeGameMode(),
                        CurrentInstrument = guitar,
                    };
                    PlayerContainer.AddProfile(profile);
                }
                active = PlayerContainer.CreatePlayerFromProfile(profile, true);
            }

            foreach (var player in PlayerContainer.Players)
            {
                player.SittingOut = player != active;
            }
            active.Profile.IsBot = true;
            YargLogger.LogFormatInfo(LOG_PREFIX + "Bot player: {0}", active.Profile.Name);
        }

        /// <summary>
        /// Starts the current web song with every active player on their first playable part at the hardest
        /// difficulty, skipping the difficulty menu.
        /// </summary>
        private static void Play()
        {
            var song = WebSongLoader.Current;
            if (song == null)
            {
                throw new InvalidOperationException("No song loaded");
            }

            foreach (var player in PlayerContainer.Players.Where(p => !p.SittingOut))
            {
                var profile = player.Profile;
                var instrument = profile.GameMode.PossibleInstrumentsForSong(song)
                    .First(i => PartDifficulty.HasPlayablePart(song, i));
                profile.CurrentInstrument = instrument;
                profile.CurrentDifficulty = EnumExtensions<Difficulty>.Values.Reverse()
                    .First(d => PartDifficulty.HasPlayableDifficulty(song, instrument, d));
                YargLogger.LogFormatInfo(LOG_PREFIX + "{0}: {1} {2}", profile.Name, profile.CurrentInstrument,
                    profile.CurrentDifficulty);
            }

            WebSongMenu.StartGameplay(song);
        }
    }
}
