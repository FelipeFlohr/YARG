using System;
using System.Globalization;
using System.Linq;
using Cysharp.Threading.Tasks;
using UnityEngine;
using YARG.Core;
using YARG.Core.Extensions;
using YARG.Core.Logging;
using YARG.Core.Audio;
using YARG.Core.Game;
using YARG.Core.Song;
using YARG.Menu.DifficultySelect;
using YARG.Menu.ScoreScreen;
using YARG.Multiplayer.AutoTest;
using YARG.Multiplayer.Protocol;
using YARG.Multiplayer.Session;
using YARG.Player;
using YARG.Song;

namespace YARG.Multiplayer
{
    /// <summary>
    /// Plays online matches without anybody at the keyboard, for testing two instances on one machine:
    /// <c>-mp-autotest host [-mp-song name] [-mp-rounds n] [-mp-players n]</c> or <c>-mp-autotest join address</c>
    /// (<c>-mp-players n</c>, default 2, is how many players the host waits for before starting the round).
    /// By default the local profile is turned into a bot; extra arguments choose the part and the player instead:
    /// <c>-mp-profile name</c> (find or create the profile), <c>-mp-instrument Instrument</c>, <c>-mp-difficulty Difficulty</c>,
    /// <c>-mp-bot 0|1</c> (saved in the profile) and <c>-mp-miss from-to</c> (a human five-fret guitarist driven by a script
    /// that misses on purpose between those song seconds). Stem mute changes and the band result are logged too.
    /// Every step is logged with an "[AutoTest]" prefix and the
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

        private static string _profileName;
        private static Instrument? _instrument;
        private static Difficulty? _difficulty;
        private static bool? _bot;
        private static double? _missFrom;
        private static double _missTo;

        private static bool UsesExtendedArgs =>
            _profileName != null || _instrument.HasValue || _difficulty.HasValue || _bot.HasValue || _missFrom.HasValue;

        /// <returns>False if an argument is invalid.</returns>
        private static bool ParseExtendedArgs(string[] args)
        {
            _profileName = GetArg(args, "-mp-profile", null);
            _instrument = null;
            _difficulty = null;
            _bot = null;
            _missFrom = null;

            string instrument = GetArg(args, "-mp-instrument", null);
            if (instrument != null)
            {
                if (!Enum.TryParse<Instrument>(instrument, true, out var parsed))
                {
                    Log($"FAIL: unknown instrument '{instrument}'");
                    return false;
                }
                _instrument = parsed;
            }

            string difficulty = GetArg(args, "-mp-difficulty", null);
            if (difficulty != null)
            {
                if (!Enum.TryParse<Difficulty>(difficulty, true, out var parsed))
                {
                    Log($"FAIL: unknown difficulty '{difficulty}'");
                    return false;
                }
                _difficulty = parsed;
            }

            string bot = GetArg(args, "-mp-bot", null);
            if (bot != null)
            {
                _bot = bot == "1";
            }

            string miss = GetArg(args, "-mp-miss", null);
            if (miss != null)
            {
                var parts = miss.Split('-');
                if (parts.Length != 2 ||
                    !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double from) ||
                    !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double to))
                {
                    Log($"FAIL: -mp-miss expects <from>-<to> in seconds, got '{miss}'");
                    return false;
                }
                _missFrom = from;
                _missTo = to;
            }
            return true;
        }

        private static async UniTaskVoid Run(string role, string address, string[] args)
        {
            bool passed = false;
            if (!ParseExtendedArgs(args))
            {
                Log("FAIL");
                await UniTask.Delay(TimeSpan.FromSeconds(2));
                Application.Quit(1);
                return;
            }
            _leaveAfter = double.Parse(GetArg(args, "-mp-leave-after", "-1"), System.Globalization.CultureInfo.InvariantCulture);
            _expectLeave = Array.IndexOf(args, "-mp-expect-leave") >= 0;
            try
            {
                passed = role switch
                {
                    "host"    => await RunHost(GetArg(args, "-mp-song", ""), int.Parse(GetArg(args, "-mp-rounds", "1")),
                        int.Parse(GetArg(args, "-mp-players", "2"))),
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

            YargPlayer player;
            if (_profileName != null)
            {
                var profile = PlayerContainer.Profiles.FirstOrDefault(p => p.Name == _profileName);
                if (profile == null)
                {
                    var instrument = _instrument ?? Instrument.FiveFretGuitar;
                    profile = new YargProfile
                    {
                        Name = _profileName,
                        GameMode = instrument.ToNativeGameMode(),
                        CurrentInstrument = instrument,
                    };
                    PlayerContainer.AddProfile(profile);
                    PlayerContainer.SaveProfiles();
                    Log($"Created profile {_profileName}");
                }

                foreach (var extra in PlayerContainer.Players.Where(p => !p.SittingOut && p.Profile != profile).ToList())
                {
                    extra.SittingOut = true;
                }
                player = PlayerContainer.Players.FirstOrDefault(p => p.Profile == profile);
                if (player == null)
                {
                    player = PlayerContainer.CreatePlayerFromProfile(profile, true);
                }
                if (player == null)
                {
                    Log($"FAIL: couldn't create a player for profile {_profileName}");
                    return false;
                }
                player.SittingOut = false;
            }
            else
            {
                foreach (var extra in PlayerContainer.Players.Where(p => !p.SittingOut).Skip(1).ToList())
                {
                    extra.SittingOut = true;
                }

                player = PlayerContainer.Players.FirstOrDefault(p => !p.SittingOut);
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
            }

            if (_instrument.HasValue)
            {
                player.Profile.GameMode = _instrument.Value.ToNativeGameMode();
                player.Profile.CurrentInstrument = _instrument.Value;
            }
            if (_difficulty.HasValue)
            {
                player.Profile.CurrentDifficulty = _difficulty.Value;
            }

            if (_missFrom.HasValue)
            {
                if (player.Profile.GameMode != GameMode.FiveFretGuitar)
                {
                    Log($"FAIL: -mp-miss needs a five-fret guitar profile, got {player.Profile.GameMode}");
                    return false;
                }
                player.Profile.IsBot = false;
            }
            else if (_bot.HasValue)
            {
                player.Profile.IsBot = _bot.Value;
            }
            else if (_profileName == null)
            {
                // Without a profile argument the local profile always plays as a bot
                player.Profile.IsBot = true;
            }

            if (UsesExtendedArgs)
            {
                PlayerContainer.SaveProfiles();
            }

            Log($"Playing as {player.Profile.Name} bot={(player.Profile.IsBot ? 1 : 0)} " +
                $"instrument={player.Profile.CurrentInstrument} difficulty={player.Profile.CurrentDifficulty}");
            return true;
        }

        /// <returns>False only when the requested instrument isn't in the song.</returns>
        private static bool PickPart(IMultiplayerSession session, SongEntry song)
        {
            var profile = MultiplayerManager.Instance.LocalPlayer!.Profile;
            if (_instrument.HasValue)
            {
                var wanted = _instrument.Value;
                if (!song.HasInstrument(wanted))
                {
                    var available = EnumExtensions<Instrument>.Values.Where(song.HasInstrument);
                    Log($"FAIL: {wanted} not in song; available: {string.Join(", ", available)}");
                    return false;
                }

                var difficulties = _difficulty.HasValue
                    ? new[] { _difficulty.Value }
                    : EnumExtensions<Difficulty>.Values.Reverse().ToArray();
                foreach (var difficulty in difficulties)
                {
                    if (PartDifficulty.HasPlayableDifficulty(song, wanted, difficulty))
                    {
                        profile.CurrentInstrument = wanted;
                        profile.CurrentDifficulty = difficulty;
                        session.SetLocalPart(wanted, difficulty);
                        Log($"Picked {wanted} {difficulty}");
                        return true;
                    }
                }
                Log($"FAIL: {wanted} has no requested difficulty chart; difficulties: " +
                    string.Join(", ", EnumExtensions<Difficulty>.Values.Where(d => PartDifficulty.HasPlayableDifficulty(song, wanted, d))));
                return false;
            }

            foreach (var instrument in profile.GameMode.PossibleInstrumentsForSong(song))
            {
                if (!PartDifficulty.HasPlayablePart(song, instrument))
                {
                    continue;
                }

                foreach (var difficulty in EnumExtensions<Difficulty>.Values.Reverse())
                {
                    if (PartDifficulty.HasPlayableDifficulty(song, instrument, difficulty))
                    {
                        profile.CurrentInstrument = instrument;
                        profile.CurrentDifficulty = difficulty;
                        session.SetLocalPart(instrument, difficulty);
                        Log($"Picked {instrument} {difficulty}");
                        return true;
                    }
                }
            }
            Log("No part available for the song");
            return true;
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

        private static YARG.Gameplay.GameManager _stemWatched;

        private static void OnStemFraction(SongStem stem, double fraction)
        {
            double song = _stemWatched != null ? _stemWatched.SongTime : 0;
            Log(string.Format(CultureInfo.InvariantCulture, "Stem {0} fraction={1:0.00} song={2:0.00}", stem, fraction, song));
        }

        private static void StopWatchingStems()
        {
            if (_stemWatched != null)
            {
                _stemWatched.StemAudibleFractionChanged -= OnStemFraction;
                _stemWatched = null;
            }
        }

        private static async UniTask<bool> PlayThroughMatch(IMultiplayerSession session)
        {
            try
            {
                return await PlayThroughMatchCore(session);
            }
            finally
            {
                StopWatchingStems();
            }
        }

        private static async UniTask<bool> ScriptLocalMisses(IMultiplayerSession session)
        {
            var manager = MultiplayerManager.Instance;
            YARG.Gameplay.Player.FiveFretGuitarPlayer guitar = null;
            if (!await WaitFor("the local guitar player", () =>
            {
                if (manager.Session != session)
                {
                    return true;
                }
                var gm = UnityEngine.Object.FindAnyObjectByType<YARG.Gameplay.GameManager>();
                guitar = gm == null ? null : gm.Players.OfType<YARG.Gameplay.Player.FiveFretGuitarPlayer>()
                    .FirstOrDefault(p => !p.Player.IsRemote && p.ChartNotes != null);
                return guitar != null;
            }))
            {
                return false;
            }
            if (guitar == null)
            {
                Log("FAIL: session ended before the guitar player was ready");
                return false;
            }

            double notBefore = guitar.BaseEngine.CurrentTime + 0.5;
            var inputs = ScriptedFiveFretInputs.Generate(guitar.ChartNotes, _missFrom.Value, _missTo, notBefore);
            guitar.SetScriptedInputs(inputs);
            Log(string.Format(CultureInfo.InvariantCulture, "Scripted {0} inputs, missing from {1:0.00} to {2:0.00}",
                inputs.Count, _missFrom.Value, _missTo));
            return true;
        }

        private static async UniTask<bool> PlayThroughMatchCore(IMultiplayerSession session)
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

            StopWatchingStems();
            var watched = UnityEngine.Object.FindAnyObjectByType<YARG.Gameplay.GameManager>();
            if (watched != null)
            {
                _stemWatched = watched;
                watched.StemAudibleFractionChanged += OnStemFraction;
            }

            if (_missFrom.HasValue && !await ScriptLocalMisses(session))
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
                Log($"Band: score={stats.Value.BandScore} stars={stats.Value.BandStars} " +
                    $"players={stats.Value.PlayerScores.Length}");
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

        private static async UniTask<bool> RunHost(string songName, int rounds, int players)
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

            if (!await WaitFor("the clients", () => session.Players.Count(p => p.Status != LobbyPlayerStatus.Left) >= players))
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
                if (!PickPart(session, song))
                {
                    return false;
                }

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

            // A fresh data folder has no profiles at all
            while (PlayerContainer.Profiles.Count < playerCount)
            {
                var guitar = Instrument.FiveFretGuitar;
                PlayerContainer.AddProfile(new YargProfile
                {
                    Name = $"AutoTest {PlayerContainer.Profiles.Count + 1}",
                    GameMode = guitar.ToNativeGameMode(),
                    CurrentInstrument = guitar,
                });
                Log($"Created profile {PlayerContainer.Profiles[PlayerContainer.Profiles.Count - 1].Name}");
            }

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
                var candidates = profile.GameMode.PossibleInstrumentsForSong(song)
                    .Where(i => PartDifficulty.HasPlayablePart(song, i)).ToList();
                if (candidates.Count == 0)
                {
                    Log($"FAIL: {profile.Name} has no playable part in the song");
                    return false;
                }
                var instrument = candidates[0];
                profile.CurrentInstrument = instrument;
                profile.CurrentDifficulty = EnumExtensions<Difficulty>.Values.Reverse()
                    .First(d => PartDifficulty.HasPlayableDifficulty(song, instrument, d));
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
                if (!PickPart(session, song))
                {
                    return false;
                }

                if (!await PlayThroughMatch(session))
                {
                    return false;
                }
                round++;
            }
        }
    }
}
