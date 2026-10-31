using System;
using System.Linq;
using Cysharp.Threading.Tasks;
using UnityEngine;
using YARG.Core;
using YARG.Core.Extensions;
using YARG.Core.Logging;
using YARG.Core.Song;
using YARG.Menu.ScoreScreen;
using YARG.Multiplayer.Protocol;
using YARG.Multiplayer.Session;
using YARG.Player;
using YARG.Song;

namespace YARG.Multiplayer
{
    /// <summary>
    /// Plays online matches without anybody at the keyboard, for testing two instances on one machine:
    /// <c>-mp-autotest host [-mp-song name] [-mp-rounds n]</c> or <c>-mp-autotest join address</c>.
    /// The local profile is turned into a bot. Every step is logged with an "[AutoTest]" prefix and the
    /// game quits when the run is over ("[AutoTest] PASS" or "[AutoTest] FAIL ...").
    /// </summary>
    public static class MultiplayerAutoTest
    {
        private const double STEP_TIMEOUT  = 120;
        private const double MATCH_TIMEOUT = 600;
        private const float  SCORE_SCREEN_SECONDS = 3;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "-mp-autotest");
            if (index < 0 || index + 1 >= args.Length)
            {
                return;
            }

            string role = args[index + 1];
            string address = index + 2 < args.Length && !args[index + 2].StartsWith("-")
                ? args[index + 2]
                : "127.0.0.1";
            Run(role, address, args).Forget();
        }

        private static string GetArg(string[] args, string name, string fallback)
        {
            int index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
        }

        private static void Log(string message)
        {
            YargLogger.LogFormatInfo("[AutoTest] {0}", message);
        }

        private static async UniTaskVoid Run(string role, string address, string[] args)
        {
            bool passed = false;
            _leaveAfter = double.Parse(GetArg(args, "-mp-leave-after", "-1"), System.Globalization.CultureInfo.InvariantCulture);
            _expectLeave = Array.IndexOf(args, "-mp-expect-leave") >= 0;
            try
            {
                passed = role switch
                {
                    "host"    => await RunHost(GetArg(args, "-mp-song", ""), int.Parse(GetArg(args, "-mp-rounds", "1"))),
                    "offline" => await RunOffline(GetArg(args, "-mp-song", ""), int.Parse(address)),
                    _         => await RunClient(address),
                };
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "[AutoTest] FAIL: exception");
            }

            Log(passed ? "PASS" : "FAIL");
            await UniTask.Delay(TimeSpan.FromSeconds(2));
            Application.Quit(passed ? 0 : 1);
        }

        private static async UniTask<bool> WaitFor(string what, Func<bool> condition, double timeout = STEP_TIMEOUT)
        {
            Log($"Waiting for {what}");
            float deadline = Time.realtimeSinceStartup + (float) timeout;
            await UniTask.WaitUntil(() => condition() || Time.realtimeSinceStartup >= deadline);
            if (!condition())
            {
                Log($"FAIL: timed out waiting for {what}");
                return false;
            }
            Log($"Done: {what}");
            return true;
        }

        private static async UniTask<bool> PrepareLocalPlayer()
        {
            if (!await WaitFor("the main menu", () => GlobalVariables.Instance.CurrentScene == SceneIndex.Menu))
            {
                return false;
            }
            // Lets the menu finish setting itself up
            await UniTask.Delay(TimeSpan.FromSeconds(3));

            foreach (var extra in PlayerContainer.Players.Where(p => !p.SittingOut).Skip(1).ToList())
            {
                extra.SittingOut = true;
            }

            var player = PlayerContainer.Players.FirstOrDefault(p => !p.SittingOut);
            if (player == null)
            {
                var profile = PlayerContainer.Profiles.FirstOrDefault(p => p.IsBot) ?? PlayerContainer.Profiles.FirstOrDefault();
                if (profile == null)
                {
                    Log("FAIL: no profile to play with");
                    return false;
                }
                player = PlayerContainer.CreatePlayerFromProfile(profile, true);
            }

            player.Profile.IsBot = true;
            Log($"Playing as {player.Profile.Name} (bot)");
            return true;
        }

        private static void PickPart(IMultiplayerSession session, SongEntry song)
        {
            var profile = MultiplayerManager.Instance.LocalPlayer!.Profile;
            foreach (var instrument in profile.GameMode.PossibleInstrumentsForSong(song))
            {
                foreach (var difficulty in EnumExtensions<Difficulty>.Values.Reverse())
                {
                    if (song[instrument][difficulty])
                    {
                        profile.CurrentInstrument = instrument;
                        profile.CurrentDifficulty = difficulty;
                        session.SetLocalPart(instrument, difficulty);
                        Log($"Picked {instrument} {difficulty}");
                        return;
                    }
                }
            }
            Log("No part available for the song");
        }

        /// <summary>
        /// Seconds into the song when this instance presses pause twice to leave, or negative to play it out.
        /// </summary>
        private static double _leaveAfter = -1;

        /// <summary>
        /// Whether the other side leaving mid-song is part of the test.
        /// </summary>
        private static bool _expectLeave;

        private static async UniTask<bool> LeaveMidSong(IMultiplayerSession session)
        {
            var manager = MultiplayerManager.Instance;
            await UniTask.Delay(TimeSpan.FromSeconds(_leaveAfter));
            var gameManager = UnityEngine.Object.FindAnyObjectByType<YARG.Gameplay.GameManager>();
            if (gameManager == null || manager.Session != session)
            {
                Log("FAIL: not in the match anymore when about to leave");
                return false;
            }

            Log("Pressing pause twice to leave");
            gameManager.TogglePause();
            await UniTask.Delay(TimeSpan.FromMilliseconds(300));
            gameManager.TogglePause();

            return await WaitFor("the menu without a session", () => manager.Session == null &&
                GlobalVariables.Instance.CurrentScene == SceneIndex.Menu);
        }

        private static async UniTaskVoid LogEngineHappiness()
        {
            foreach (int seconds in new[] { 1, 5, 30 })
            {
                await UniTask.Delay(TimeSpan.FromSeconds(seconds == 1 ? 1 : seconds == 5 ? 4 : 25));
                var gameManager = UnityEngine.Object.FindAnyObjectByType<YARG.Gameplay.GameManager>();
                if (gameManager == null || gameManager.EngineManager == null)
                {
                    return;
                }
                foreach (var engine in gameManager.EngineManager.Engines)
                {
                    Log($"t+{seconds}s engine {engine.EngineId}: happiness={engine.Happiness:0.000} " +
                        $"time={engine.BaseEngine.CurrentTime:0.00} sp-revives={engine.BaseEngine.BaseStats.StarPowerRevives}");
                }
            }
        }

        private static async UniTask<bool> PlayThroughMatch(IMultiplayerSession session)
        {
            var manager = MultiplayerManager.Instance;
            if (!await WaitFor("gameplay", () => manager.Session != session ||
                GlobalVariables.Instance.CurrentScene == SceneIndex.Gameplay))
            {
                return false;
            }
            if (manager.Session != session)
            {
                Log("FAIL: session ended before gameplay");
                return false;
            }

            if (!await WaitFor("the song to start", () => manager.Session != session || session.StartTimeLocal.HasValue))
            {
                return false;
            }

            if (_leaveAfter >= 0)
            {
                return await LeaveMidSong(session);
            }

            LogEngineHappiness().Forget();

            if (!await WaitFor("the score screen", () => manager.Session != session ||
                GlobalVariables.Instance.CurrentScene == SceneIndex.Score, MATCH_TIMEOUT))
            {
                return false;
            }
            if (manager.Session != session)
            {
                if (!_expectLeave)
                {
                    Log("FAIL: session ended during the match");
                    return false;
                }
                return await WaitFor("the menu after the other side left",
                    () => GlobalVariables.Instance.CurrentScene == SceneIndex.Menu);
            }

            var stats = GlobalVariables.State.ScoreScreenStats;
            if (stats != null)
            {
                foreach (var card in stats.Value.PlayerScores)
                {
                    Log($"Result: {card.Player.Profile.Name} remote={card.Player.IsRemote} " +
                        $"score={card.Stats.TotalScore} hit={card.Stats.NotesHit}/{card.Stats.TotalNotes} " + DescribeStarPower(card.Stats));
                }
            }

            await UniTask.Delay(TimeSpan.FromSeconds(SCORE_SCREEN_SECONDS));
            GlobalVariables.Instance.LoadScene(SceneIndex.Menu);
            return await WaitFor("the lobby again", () => manager.Session != session ||
                (GlobalVariables.Instance.CurrentScene == SceneIndex.Menu &&
                    (session.State == SessionState.Lobby || session.State == SessionState.Transferring)));
        }

        private static string DescribeStarPower(Core.Engine.BaseStats stats) =>
            $"sp-phrases={stats.StarPowerPhrasesHit}/{stats.TotalStarPowerPhrases} sp-activations={stats.StarPowerActivationCount} " +
            $"sp-bars-filled={stats.TotalStarPowerBarsFilled:0.00} sp-ticks={stats.StarPowerTickAmount}/{stats.TotalStarPowerTicks} " +
            $"sp-revives={stats.StarPowerRevives} " +
            $"sp-time={stats.TimeInStarPower:0.0}s sp-score={stats.StarPowerScore} solo={stats.SoloBonuses} max-combo={stats.MaxCombo}";

        private static async UniTask<bool> RunHost(string songName, int rounds)
        {
            if (!await PrepareLocalPlayer())
            {
                return false;
            }

            var manager = MultiplayerManager.Instance;
            if (!manager.Host())
            {
                Log("FAIL: couldn't host");
                return false;
            }
            var session = (HostSession) manager.Session!;
            Log("Hosting");

            if (!await WaitFor("a client", () => session.Players.Count(p => p.Status != LobbyPlayerStatus.Left) >= 2))
            {
                return false;
            }

            var song = SongContainer.UnfilteredSongs
                .Where(s => s.SubType == EntryType.Ini || s.SubType == EntryType.Sng)
                .FirstOrDefault(s => songName.Length == 0 ||
                    s.Name.Original.IndexOf(songName, StringComparison.OrdinalIgnoreCase) >= 0);
            if (song == null)
            {
                Log($"FAIL: no shareable song matching '{songName}'");
                return false;
            }

            for (int round = 1; round <= rounds; round++)
            {
                Log($"Round {round}: offering {song.Name.Original}");
                if (!await manager.OfferSong(song, 1f))
                {
                    Log("FAIL: offering the song failed");
                    return false;
                }
                PickPart(session, song);

                if (!await WaitFor("everybody to be ready", () => session.CanBeginLoad))
                {
                    foreach (var p in session.Players)
                    {
                        Log($"  {p.Name}: {p.Status} {p.Instrument} {p.Difficulty}");
                    }
                    return false;
                }

                session.BeginLoad();
                if (!await PlayThroughMatch(session))
                {
                    return false;
                }
                if (manager.Session != session)
                {
                    // Only fine when leaving was the test
                    return _leaveAfter >= 0;
                }
            }

            Log("Leaving");
            manager.Leave();
            return true;
        }

        /// <summary>
        /// Plays the song offline with <paramref name="playerCount"/> local bots, for comparing against online results.
        /// </summary>
        private static async UniTask<bool> RunOffline(string songName, int playerCount)
        {
            if (!await WaitFor("the main menu", () => GlobalVariables.Instance.CurrentScene == SceneIndex.Menu))
            {
                return false;
            }
            await UniTask.Delay(TimeSpan.FromSeconds(3));

            foreach (var profile in PlayerContainer.Profiles)
            {
                if (PlayerContainer.Players.Count(p => !p.SittingOut) >= playerCount)
                {
                    break;
                }
                if (!PlayerContainer.IsProfileTaken(profile))
                {
                    PlayerContainer.CreatePlayerFromProfile(profile, true);
                }
            }
            var players = PlayerContainer.Players.Where(p => !p.SittingOut).Take(playerCount).ToList();
            // Players left active by an earlier run would join in without anybody playing them
            foreach (var extra in PlayerContainer.Players.Where(p => !p.SittingOut).Skip(playerCount).ToList())
            {
                extra.SittingOut = true;
            }
            if (players.Count < playerCount)
            {
                Log($"FAIL: only {players.Count} profiles for {playerCount} players");
                return false;
            }

            var song = SongContainer.UnfilteredSongs.FirstOrDefault(s => songName.Length == 0 ||
                s.Name.Original.IndexOf(songName, StringComparison.OrdinalIgnoreCase) >= 0);
            if (song == null)
            {
                Log($"FAIL: no song matching '{songName}'");
                return false;
            }

            foreach (var player in players)
            {
                player.Profile.IsBot = true;
                var profile = player.Profile;
                var instrument = profile.GameMode.PossibleInstrumentsForSong(song).First();
                profile.CurrentInstrument = instrument;
                profile.CurrentDifficulty = EnumExtensions<Difficulty>.Values.Reverse().First(d => song[instrument][d]);
                Log($"Offline player {profile.Name}: {profile.CurrentInstrument} {profile.CurrentDifficulty}");
            }

            GlobalVariables.State = PersistentState.Default;
            GlobalVariables.State.CurrentSong = song;
            GlobalVariables.State.SongSpeed = 1f;
            GlobalVariables.Instance.LoadScene(SceneIndex.Gameplay);

            if (!await WaitFor("the score screen", () => GlobalVariables.Instance.CurrentScene == SceneIndex.Score,
                MATCH_TIMEOUT))
            {
                return false;
            }
            foreach (var card in GlobalVariables.State.ScoreScreenStats!.Value.PlayerScores)
            {
                Log($"Result: {card.Player.Profile.Name} score={card.Stats.TotalScore} " +
                    $"hit={card.Stats.NotesHit}/{card.Stats.TotalNotes} " + DescribeStarPower(card.Stats));
            }
            return true;
        }

        private static async UniTask<bool> RunClient(string address)
        {
            if (!await PrepareLocalPlayer())
            {
                return false;
            }

            var manager = MultiplayerManager.Instance;
            if (!manager.Join(address))
            {
                Log("FAIL: couldn't join");
                return false;
            }
            var session = manager.Session!;
            Log($"Joining {address}");

            if (!await WaitFor("the welcome", () => manager.Session != session || session.LocalPlayerId > 0))
            {
                return false;
            }
            if (manager.Session != session)
            {
                Log("FAIL: rejected or disconnected");
                return false;
            }

            int round = 0;
            while (true)
            {
                // The host offers a song; the client gets it and picks a part
                if (!await WaitFor("the song", () => manager.Session != session ||
                    session.Players.Any(p => p.PlayerId == session.LocalPlayerId && p.Status == LobbyPlayerStatus.HasSong),
                    MATCH_TIMEOUT))
                {
                    return false;
                }
                if (manager.Session != session)
                {
                    // The host left after the last round: that's the end of a good run
                    Log($"Session ended after {round} round(s)");
                    return round > 0;
                }

                var song = MultiplayerSongLibrary.FindByHash(session.CurrentOffer!.Hash);
                if (song == null)
                {
                    Log("FAIL: song marked as had but missing from the library");
                    return false;
                }
                PickPart(session, song);

                if (!await PlayThroughMatch(session))
                {
                    return false;
                }
                round++;
            }
        }
    }
}
