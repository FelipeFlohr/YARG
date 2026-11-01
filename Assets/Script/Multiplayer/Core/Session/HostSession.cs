using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YARG.Core;
using YARG.Core.Song;
using YARG.Multiplayer.Protocol;
using YARG.Multiplayer.Transport;

namespace YARG.Multiplayer.Session
{
    /// <summary>
    /// The hosting side of a session: owns the lobby, decides the song and when to start,
    /// relays game messages between clients and sends them songs they don't have.
    /// </summary>
    public sealed class HostSession : IMultiplayerSession, INetEventListener
    {
        private const int HOST_PLAYER_ID = 0;
        private const double START_DELAY_SECONDS = 3;

        // Song chunks are only queued while a peer's send backlog is below this
        private const long MAX_SEND_BACKLOG = 2 * 1024 * 1024;

        private sealed class PlayerInfo
        {
            public int               PlayerId;
            public int               PeerId = -1;
            public string            Name = string.Empty;
            public Instrument        Instrument = Instrument.FiveFretGuitar;
            public Difficulty        Difficulty = Difficulty.Expert;
            public bool              HasPart;
            public LobbyPlayerStatus Status;
            public byte              Progress;

            public bool IsParticipant => Status != LobbyPlayerStatus.Left && Status != LobbyPlayerStatus.SongFailed;

            public LobbyPlayer ToLobbyPlayer() =>
                new(PlayerId, Name, Instrument, Difficulty, Status, Progress, HasPart);
        }

        private sealed class PeerInfo
        {
            public int         PeerId;
            public double      LastHeard;
            public PlayerInfo? Player;
        }

        private sealed class Transfer
        {
            public PlayerInfo Player = null!;
            public Stream     Stream = null!;
            public long       Offset;
        }

        private readonly INetTransport _transport;
        private readonly SessionConfig _config;
        private readonly Func<double>  _clock;

        private readonly SortedDictionary<int, PlayerInfo> _players   = new();
        private readonly Dictionary<int, PeerInfo>         _peers     = new();
        private readonly Dictionary<int, Transfer>         _transfers = new();

        private Func<Stream>? _openPackage;
        private int           _nextPlayerId = HOST_PLAYER_ID + 1;
        private double        _lastPing = double.NegativeInfinity;
        private bool          _lobbyDirty;

        private IReadOnlyList<LobbyPlayer> _lobbyPlayers = Array.Empty<LobbyPlayer>();

        public bool                       IsHost         => true;
        public int                        LocalPlayerId  => HOST_PLAYER_ID;
        public SessionState               State          { get; private set; } = SessionState.Lobby;
        public IReadOnlyList<LobbyPlayer> Players        => _lobbyPlayers;
        public SongOffer?                 CurrentOffer   { get; private set; }
        public double?                    StartTimeLocal { get; private set; }

        public event Action?             LobbyChanged;
        public event Action<NetMessage>? GameMessageReceived;
        public event Action?             BeginLoadRequested;
        public event Action?             ReturnToLobbyRequested;

        // A host session only ends when the host itself leaves
        public event Action<string>? Ended
        {
            add { }
            remove { }
        }

        private PlayerInfo LocalPlayer => _players[HOST_PLAYER_ID];

        /// <summary>
        /// Every player still in the match has the song and picked a part.
        /// </summary>
        public bool CanBeginLoad => State == SessionState.Transferring &&
            _players.Values.Where(p => p.IsParticipant)
                .All(p => p.Status == LobbyPlayerStatus.HasSong && p.HasPart);

        public HostSession(INetTransport transport, SessionConfig config, Func<double> clock)
        {
            _transport = transport;
            _config = config;
            _clock = clock;

            _players[HOST_PLAYER_ID] = new PlayerInfo
            {
                PlayerId = HOST_PLAYER_ID,
                Name = config.LocalProfileName,
            };
            _lobbyDirty = true;
        }

        public void Start(int port)
        {
            _transport.StartHost(port);
            FlushLobby();
        }

        public void Tick()
        {
            if (State == SessionState.Ended)
            {
                return;
            }

            _transport.Poll(this);

            double now = _clock();
            if (now - _lastPing >= _config.PingIntervalSeconds)
            {
                _lastPing = now;
                SendToAllPlayers(new Ping(now));
            }

            foreach (var peer in _peers.Values.ToList())
            {
                // A peer that has not said Hello yet is still handshaking
                double limit = peer.Player == null ? _config.ConnectTimeoutSeconds : _config.TimeoutSeconds;
                if (now - peer.LastHeard > limit)
                {
                    _config.Log($"Peer {peer.PeerId} timed out");
                    RemovePeer(peer.PeerId, true);
                }
            }

            SendChunks();
            FlushLobby();
        }

        public void OfferSong(SongOffer offer, Func<Stream> openPackage)
        {
            if (State != SessionState.Lobby && State != SessionState.Results && State != SessionState.Transferring)
            {
                throw new InvalidOperationException($"Cannot offer a song while {State}");
            }

            CancelTransfers();
            PruneLeftPlayers();

            CurrentOffer = offer;
            _openPackage = openPackage;
            State = SessionState.Transferring;
            foreach (var player in _players.Values)
            {
                player.Status = LobbyPlayerStatus.Idle;
                player.Progress = 0;
                // The new song may not chart the part picked before
                player.HasPart = false;
            }
            // The host offers from its own library
            LocalPlayer.Status = LobbyPlayerStatus.HasSong;

            SendToAllPlayers(offer);
            _lobbyDirty = true;
            FlushLobby();
        }

        public void BeginLoad()
        {
            if (!CanBeginLoad)
            {
                throw new InvalidOperationException("Not every player is ready to load");
            }

            State = SessionState.Loading;
            SendToAllPlayers(new BeginLoad());
            _lobbyDirty = true;
            FlushLobby();
            BeginLoadRequested?.Invoke();
        }

        public void ReturnToLobby()
        {
            if (State != SessionState.Results)
            {
                throw new InvalidOperationException($"Cannot return to the lobby while {State}");
            }

            PruneLeftPlayers();
            foreach (var player in _players.Values)
            {
                player.Status = LobbyPlayerStatus.Idle;
                player.Progress = 0;
            }
            CurrentOffer = null;
            StartTimeLocal = null;
            State = SessionState.Lobby;

            SendToAllPlayers(new ReturnToLobby());
            _lobbyDirty = true;
            FlushLobby();
            ReturnToLobbyRequested?.Invoke();
        }

        public void SetLocalPart(Instrument instrument, Difficulty difficulty)
        {
            SetPart(LocalPlayer, instrument, difficulty);
            FlushLobby();
        }

        public void SendGameMessage(NetMessage message)
        {
            GameMessages.EnsureGameMessage(message);
            SendToAllPlayers(message);

            if (message is Finished)
            {
                MarkFinished(LocalPlayer);
            }
            FlushLobby();
        }

        public void ReportLoaded()
        {
            if (State == SessionState.Loading)
            {
                LocalPlayer.Status = LobbyPlayerStatus.Loaded;
                _lobbyDirty = true;
                TryStart();
            }
            FlushLobby();
        }

        public void Leave()
        {
            if (State == SessionState.Ended)
            {
                return;
            }

            SendToAllPlayers(new Leave("host left"));
            foreach (int peerId in _peers.Keys.ToList())
            {
                _transport.Disconnect(peerId);
            }
            _peers.Clear();
            CancelTransfers();
            State = SessionState.Ended;
        }

        public void Dispose()
        {
            CancelTransfers();
            _transport.Dispose();
            State = SessionState.Ended;
        }

        void INetEventListener.OnPeerConnected(int peerId)
        {
            _peers[peerId] = new PeerInfo
            {
                PeerId = peerId,
                LastHeard = _clock(),
            };
        }

        void INetEventListener.OnPeerDisconnected(int peerId, string reason)
        {
            if (_peers.ContainsKey(peerId))
            {
                _config.Log($"Peer {peerId} disconnected: {reason}");
                RemovePeer(peerId, false);
            }
        }

        void INetEventListener.OnMessage(int peerId, byte[] data)
        {
            if (!_peers.TryGetValue(peerId, out var peer))
            {
                return;
            }
            peer.LastHeard = _clock();

            NetMessage message;
            try
            {
                message = MessageCodec.Decode(data);
            }
            catch (InvalidDataException e)
            {
                _config.Log($"Malformed message from peer {peerId}, dropping it: {e.Message}");
                RemovePeer(peerId, true);
                return;
            }

            if (message is Ping ping)
            {
                _transport.Send(peerId, MessageCodec.Encode(new Pong(ping.SentAt, _clock())));
                return;
            }

            if (peer.Player == null)
            {
                if (message is Hello hello)
                {
                    HandleHello(peer, hello);
                }
                return;
            }

            HandlePlayerMessage(peer, peer.Player, message, data);
        }

        private void HandleHello(PeerInfo peer, Hello hello)
        {
            RejectCode? rejection = null;
            if (hello.ProtocolVersion != ProtocolInfo.Version || hello.GameVersion != _config.GameVersion)
            {
                rejection = RejectCode.VersionMismatch;
            }
            // Only a song that's loading or playing locks the session; while a song is being
            // offered the newcomer just gets the offer too
            else if (State == SessionState.Loading || State == SessionState.Playing)
            {
                rejection = RejectCode.SessionInProgress;
            }
            else if (_players.Values.Count(p => p.Status != LobbyPlayerStatus.Left) >= ProtocolInfo.MaxMachines)
            {
                rejection = RejectCode.SessionFull;
            }

            if (rejection.HasValue)
            {
                _config.Log($"Rejecting {hello.ProfileName} ({hello.GameVersion}): {rejection.Value}");
                _transport.Send(peer.PeerId, MessageCodec.Encode(new Reject(rejection.Value, _config.GameVersion)));
                RemovePeer(peer.PeerId, true);
                return;
            }

            var player = new PlayerInfo
            {
                PlayerId = _nextPlayerId++,
                PeerId = peer.PeerId,
                Name = hello.ProfileName,
                Status = LobbyPlayerStatus.Idle,
            };
            _players[player.PlayerId] = player;
            peer.Player = player;

            _transport.Send(peer.PeerId, MessageCodec.Encode(new Welcome(player.PlayerId)));
            _transport.StartKeepAlive(peer.PeerId);
            if (State == SessionState.Transferring && CurrentOffer != null)
            {
                _transport.Send(peer.PeerId, MessageCodec.Encode(CurrentOffer));
            }
            _lobbyDirty = true;
        }

        private void HandlePlayerMessage(PeerInfo peer, PlayerInfo player, NetMessage message, byte[] data)
        {
            bool offerMatches(HashWrapper hash) =>
                State == SessionState.Transferring && CurrentOffer != null && CurrentOffer.Hash.Equals(hash);

            switch (message)
            {
                case SetPart m:
                    SetPart(player, m.Instrument, m.Difficulty);
                    break;
                case Pong _:
                    break;
                case HaveSong m when offerMatches(m.Hash):
                    CancelTransfer(peer.PeerId);
                    SetStatus(player, LobbyPlayerStatus.HasSong);
                    break;
                case NeedSong m when offerMatches(m.Hash):
                    StartTransfer(peer.PeerId, player);
                    break;
                case SongReady m when offerMatches(m.Hash):
                    CancelTransfer(peer.PeerId);
                    SetStatus(player, LobbyPlayerStatus.HasSong);
                    break;
                case SongFailed m when offerMatches(m.Hash):
                    _config.Log($"{player.Name} could not get the song: {m.Reason}");
                    CancelTransfer(peer.PeerId);
                    SetStatus(player, LobbyPlayerStatus.SongFailed);
                    break;
                case Loaded _ when State == SessionState.Loading && player.IsParticipant:
                    SetStatus(player, LobbyPlayerStatus.Loaded);
                    TryStart();
                    break;
                case Leave m:
                    _config.Log($"{player.Name} left: {m.Reason}");
                    RemovePeer(peer.PeerId, true);
                    break;
                default:
                    if (GameMessages.TryGetPlayerId(message, out int playerId))
                    {
                        if (playerId != player.PlayerId)
                        {
                            _config.Log($"Dropping {message.Type} from peer {peer.PeerId} claiming player {playerId}");
                            break;
                        }

                        foreach (var other in _peers.Values)
                        {
                            if (other.Player != null && other.PeerId != peer.PeerId)
                            {
                                _transport.Send(other.PeerId, data);
                            }
                        }
                        GameMessageReceived?.Invoke(message);

                        if (message is Finished)
                        {
                            MarkFinished(player);
                        }
                    }
                    else
                    {
                        _config.Log($"Ignoring unexpected {message.Type} from peer {peer.PeerId}");
                    }
                    break;
            }
        }

        private void SetPart(PlayerInfo player, Instrument instrument, Difficulty difficulty)
        {
            player.Instrument = instrument;
            player.Difficulty = difficulty;
            player.HasPart = true;
            _lobbyDirty = true;
        }

        private void SetStatus(PlayerInfo player, LobbyPlayerStatus status)
        {
            if (player.Status != status)
            {
                player.Status = status;
                _lobbyDirty = true;
            }
        }

        private void MarkFinished(PlayerInfo player)
        {
            if (State != SessionState.Playing || !player.IsParticipant)
            {
                return;
            }

            SetStatus(player, LobbyPlayerStatus.Finished);
            if (_players.Values.Where(p => p.IsParticipant).All(p => p.Status == LobbyPlayerStatus.Finished))
            {
                State = SessionState.Results;
                _lobbyDirty = true;
            }
        }

        private void TryStart()
        {
            if (State != SessionState.Loading ||
                !_players.Values.Where(p => p.IsParticipant).All(p => p.Status == LobbyPlayerStatus.Loaded))
            {
                return;
            }

            double hostTime = _clock() + START_DELAY_SECONDS;
            StartTimeLocal = hostTime;
            State = SessionState.Playing;
            foreach (var player in _players.Values.Where(p => p.IsParticipant))
            {
                player.Status = LobbyPlayerStatus.Playing;
            }

            SendToAllPlayers(new StartAt(hostTime));
            _lobbyDirty = true;
        }

        private void RemovePeer(int peerId, bool disconnect)
        {
            if (!_peers.Remove(peerId, out var peer))
            {
                return;
            }

            if (disconnect)
            {
                _transport.Disconnect(peerId);
            }

            CancelTransfer(peerId);
            if (peer.Player != null)
            {
                SetStatus(peer.Player, LobbyPlayerStatus.Left);

                // The ones who left may have been the last ones being waited on
                TryStart();
                if (State == SessionState.Playing &&
                    _players.Values.Where(p => p.IsParticipant).All(p => p.Status == LobbyPlayerStatus.Finished))
                {
                    State = SessionState.Results;
                }
            }
        }

        private void PruneLeftPlayers()
        {
            foreach (var player in _players.Values.Where(p => p.Status == LobbyPlayerStatus.Left).ToList())
            {
                _players.Remove(player.PlayerId);
            }
        }

        private void StartTransfer(int peerId, PlayerInfo player)
        {
            CancelTransfer(peerId);

            Stream stream;
            try
            {
                stream = _openPackage!();
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                _config.Log($"Could not open the song package: {e.Message}");
                SetStatus(player, LobbyPlayerStatus.SongFailed);
                return;
            }

            _transfers[peerId] = new Transfer
            {
                Player = player,
                Stream = stream,
            };
            player.Progress = 0;
            player.Status = LobbyPlayerStatus.Downloading;
            _lobbyDirty = true;
        }

        private void SendChunks()
        {
            if (CurrentOffer == null)
            {
                return;
            }

            foreach (var (peerId, transfer) in _transfers.ToList())
            {
                for (int i = 0; i < _config.ChunksPerTick && transfer.Offset < CurrentOffer.TotalBytes; i++)
                {
                    if (_transport.PendingSendBytes(peerId) >= MAX_SEND_BACKLOG)
                    {
                        break;
                    }

                    int size = (int) Math.Min(ProtocolInfo.MaxChunkSize, CurrentOffer.TotalBytes - transfer.Offset);
                    var data = new byte[size];
                    int read = ReadFully(transfer.Stream, data);
                    if (read < size)
                    {
                        _config.Log($"Song package ended at {transfer.Offset + read} of {CurrentOffer.TotalBytes} bytes");
                        CancelTransfer(peerId);
                        SetStatus(transfer.Player, LobbyPlayerStatus.SongFailed);
                        break;
                    }

                    _transport.Send(peerId, MessageCodec.Encode(new SongChunk(CurrentOffer.Hash, transfer.Offset, data)));
                    transfer.Offset += size;

                    byte progress = (byte) (transfer.Offset * 100 / CurrentOffer.TotalBytes);
                    if (progress != transfer.Player.Progress)
                    {
                        transfer.Player.Progress = progress;
                        _lobbyDirty = true;
                    }
                }

                if (transfer.Offset >= CurrentOffer.TotalBytes)
                {
                    // Sent everything; the client confirms with SongReady or SongFailed
                    transfer.Stream.Dispose();
                    _transfers.Remove(peerId);
                }
            }
        }

        private static int ReadFully(Stream stream, byte[] buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = stream.Read(buffer, total, buffer.Length - total);
                if (read == 0)
                {
                    break;
                }
                total += read;
            }
            return total;
        }

        private void CancelTransfer(int peerId)
        {
            if (_transfers.Remove(peerId, out var transfer))
            {
                transfer.Stream.Dispose();
            }
        }

        private void CancelTransfers()
        {
            foreach (int peerId in _transfers.Keys.ToList())
            {
                CancelTransfer(peerId);
            }
        }

        private void SendToAllPlayers(NetMessage message)
        {
            byte[]? data = null;
            foreach (var peer in _peers.Values)
            {
                if (peer.Player != null)
                {
                    data ??= MessageCodec.Encode(message);
                    _transport.Send(peer.PeerId, data);
                }
            }
        }

        private void FlushLobby()
        {
            if (!_lobbyDirty)
            {
                return;
            }
            _lobbyDirty = false;

            _lobbyPlayers = _players.Values.Select(p => p.ToLobbyPlayer()).ToArray();
            SendToAllPlayers(new LobbyState(_lobbyPlayers.ToArray(), CurrentOffer, State));
            LobbyChanged?.Invoke();
        }
    }
}
