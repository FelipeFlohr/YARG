using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using YARG.Core.Input;
using YARG.Core.Logging;
using YARG.Core.Replays;
using YARG.Gameplay.Player;
using YARG.Multiplayer.Protocol;
using YARG.Multiplayer.Session;
using YARG.Multiplayer.Sync;
using YARG.Player;
using YARG.Settings;

namespace YARG.Multiplayer
{
    /// <summary>
    /// The network side of one online song: sends the local player's inputs and stats,
    /// and collects the other players' setups, inputs and results.
    /// </summary>
    public static class MultiplayerGameplay
    {
        private const double CHECKPOINT_INTERVAL = 1.0;

        private static IMultiplayerSession _session;
        private static InputBatcher        _batcher;
        private static double              _nextCheckpoint;

        // Once the remote players are created, inputs for anybody else are dropped
        private static bool _remotePlayersCreated;

        private static readonly Dictionary<int, ReplayFrame>       _setups      = new();
        private static readonly Dictionary<int, ReplayFrame>       _checkpoints = new();
        private static readonly Dictionary<int, ReplayFrame>       _finished    = new();
        private static readonly Dictionary<int, RemoteInputBuffer> _buffers     = new();

        private static double Now => MultiplayerManager.Instance.Now;

        /// <summary>
        /// Whether an online song is being loaded or played. A session can be active without one,
        /// e.g. while in the lobby or when playing a song offline after the score screen.
        /// </summary>
        public static bool IsInMatch => _session != null;

        /// <summary>
        /// Starts listening for the song's game messages. Called when loading starts, before the
        /// gameplay scene exists, so setups and inputs that arrive early are kept.
        /// </summary>
        public static void BeginMatch(IMultiplayerSession session)
        {
            EndMatch();

            _session = session;
            _session.GameMessageReceived += OnGameMessage;
            _batcher = new InputBatcher(session.LocalPlayerId);
            _nextCheckpoint = double.NegativeInfinity;
        }

        public static void EndMatch()
        {
            if (_session != null)
            {
                _session.GameMessageReceived -= OnGameMessage;
            }

            _session = null;
            _batcher = null;
            _remotePlayersCreated = false;
            _setups.Clear();
            _checkpoints.Clear();
            _finished.Clear();
            _buffers.Clear();
        }

        public static void OnLocalInput(BasePlayer player, GameInput input)
        {
            _batcher?.Add(input);
        }

        public static void OnLocalEngineTime(BasePlayer player, double time)
        {
            if (_session == null)
            {
                return;
            }

            _batcher.SetWatermark(time);
            var batch = _batcher.TryFlush(Now);
            if (batch != null)
            {
                _session.SendGameMessage(batch);
            }

            if (Now >= _nextCheckpoint)
            {
                _nextCheckpoint = Now + CHECKPOINT_INTERVAL;
                _session.SendGameMessage(new StatsCheckpoint(_session.LocalPlayerId, EncodeFrame(player)));
            }
        }

        public static void SendSetup(BasePlayer localPlayer)
        {
            _session?.SendGameMessage(new PlayerSetup(_session.LocalPlayerId, EncodeFrame(localPlayer),
                localPlayer.Player.Profile.IsBot));
        }

        /// <summary>
        /// Waits until every other player taking part sent its setup, and creates their players.
        /// Whoever is not here by the timeout plays without being shown.
        /// </summary>
        public static async UniTask<List<YargPlayer>> AwaitRemotePlayers(double timeoutSeconds)
        {
            double deadline = Now + timeoutSeconds;
            await UniTask.WaitUntil(() => _session == null || Now >= deadline ||
                GetRemoteParticipants().All(id => _setups.ContainsKey(id)));

            var players = new List<YargPlayer>();
            if (_session == null)
            {
                return players;
            }

            var participants = GetRemoteParticipants().ToHashSet();
            foreach (var (playerId, setup) in _setups.OrderBy(pair => pair.Key))
            {
                if (participants.Contains(playerId))
                {
                    players.Add(new YargPlayer(setup, playerId, GetBuffer(playerId)));
                }
            }

            foreach (int missing in participants.Where(id => !_setups.ContainsKey(id)))
            {
                YargLogger.LogFormatWarning("Player {0} didn't load in time and won't be shown", missing);
            }

            _remotePlayersCreated = true;
            foreach (int playerId in _buffers.Keys.ToList())
            {
                if (players.All(player => player.RemotePlayerId != playerId))
                {
                    _buffers.Remove(playerId);
                }
            }

            return players;
        }

        /// <summary>
        /// Reports this machine as loaded and waits for the host's start time.
        /// Returns false when the session ended or nobody started the song within the timeout.
        /// </summary>
        public static async UniTask<bool> WaitForStart(double timeoutSeconds)
        {
            var session = _session;
            if (session == null)
            {
                return false;
            }

            session.ReportLoaded();
            double deadline = Now + timeoutSeconds;
            await UniTask.WaitUntil(() => _session != session || IsStarted(session) || Now >= deadline);
            return _session == session && IsStarted(session);
        }

        private static bool IsStarted(IMultiplayerSession session)
        {
            return session.StartTimeLocal.HasValue && Now >= session.StartTimeLocal.Value;
        }

        public static void SendFinished(BasePlayer localPlayer)
        {
            _session?.SendGameMessage(new Finished(_session.LocalPlayerId, EncodeFrame(localPlayer)));
        }

        /// <summary>
        /// Waits for the other players' final stats. Anybody who left or didn't finish in time
        /// gets their last checkpoint; players with neither are missing from the result.
        /// </summary>
        public static async UniTask<IReadOnlyDictionary<int, ReplayFrame>> AwaitFinishedFrames(double timeoutSeconds)
        {
            var session = _session;
            var frames = new Dictionary<int, ReplayFrame>();
            if (session == null)
            {
                return frames;
            }

            double deadline = Now + timeoutSeconds;
            await UniTask.WaitUntil(() => _session != session || Now >= deadline ||
                _buffers.Keys.All(id => _finished.ContainsKey(id) || HasLeft(id)));

            foreach (int playerId in _buffers.Keys)
            {
                if (_finished.TryGetValue(playerId, out var frame) || _checkpoints.TryGetValue(playerId, out frame))
                {
                    frames[playerId] = frame;
                }
            }
            return frames;
        }

        private static void OnGameMessage(NetMessage message)
        {
            try
            {
                switch (message)
                {
                    case PlayerSetup m:
                        var setup = ReplayFrameCodec.Decode(m.Frame);
                        setup.Profile.IsBot = m.IsBot;
                        _setups[m.PlayerId] = setup;
                        break;
                    case InputBatch m:
                        GetBuffer(m.PlayerId)?.AddBatch(m);
                        break;
                    case StatsCheckpoint m:
                        _checkpoints[m.PlayerId] = ReplayFrameCodec.Decode(m.Frame);
                        break;
                    case Finished m:
                        _finished[m.PlayerId] = ReplayFrameCodec.Decode(m.Frame);
                        break;
                }
            }
            catch (InvalidDataException e)
            {
                YargLogger.LogException(e, "Received an invalid multiplayer player frame");
            }
        }

        private static RemoteInputBuffer GetBuffer(int playerId)
        {
            if (!_buffers.TryGetValue(playerId, out var buffer) && !_remotePlayersCreated)
            {
                double delay = SettingsManager.Settings.MultiplayerRemoteDelayMs.Value / 1000.0;
                buffer = new RemoteInputBuffer(delay);
                _buffers.Add(playerId, buffer);
            }
            return buffer;
        }

        private static IEnumerable<int> GetRemoteParticipants()
        {
            return _session.Players
                .Where(player => player.PlayerId != _session.LocalPlayerId &&
                    player.Status != LobbyPlayerStatus.Left &&
                    player.Status != LobbyPlayerStatus.SongFailed)
                .Select(player => player.PlayerId);
        }

        /// <summary>
        /// Whether a remote player left the session (or was never in it).
        /// </summary>
        public static bool HasLeft(int playerId)
        {
            return _session != null && _session.Players.All(player =>
                player.PlayerId != playerId || player.Status == LobbyPlayerStatus.Left);
        }

        private static byte[] EncodeFrame(BasePlayer player)
        {
            // Stats only: receivers already got the inputs as they were played
            var (frame, _) = player.ConstructReplayData();
            return ReplayFrameCodec.Encode(new ReplayFrame(frame.Profile, frame.EngineParameters, frame.Stats,
                Array.Empty<GameInput>()));
        }
    }
}
