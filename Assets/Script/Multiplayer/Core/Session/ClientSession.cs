using System;
using System.Collections.Generic;
using System.IO;
using YARG.Core;
using YARG.Multiplayer.Protocol;
using YARG.Multiplayer.Sync;
using YARG.Multiplayer.Transport;

namespace YARG.Multiplayer.Session
{
    /// <summary>
    /// The joining side of a session. Mirrors the lobby and state the host sends, keeps the clock
    /// in sync for the start time, and surfaces song offers and chunks to the song-sharing layer.
    /// </summary>
    public sealed class ClientSession : IMultiplayerSession, INetEventListener
    {
        private const int HOST_PEER_ID = 0;

        private readonly INetTransport _transport;
        private readonly SessionConfig _config;
        private readonly Func<double>  _clock;
        private readonly ClockSync     _clockSync = new();

        private bool   _connected;
        private bool   _welcomed;
        private double _lastHeard;
        private double _lastPing = double.NegativeInfinity;

        public bool                       IsHost         => false;
        public int                        LocalPlayerId  { get; private set; } = -1;
        public SessionState               State          { get; private set; } = SessionState.Lobby;
        public IReadOnlyList<LobbyPlayer> Players        { get; private set; } = Array.Empty<LobbyPlayer>();
        public SongOffer?                 CurrentOffer   { get; private set; }
        public double?                    StartTimeLocal { get; private set; }

        /// <summary>
        /// Why the host refused this client, if it did.
        /// </summary>
        public Reject? RejectReason { get; private set; }

        public event Action?             LobbyChanged;
        public event Action<NetMessage>? GameMessageReceived;
        public event Action<string>?     Ended;
        public event Action?             BeginLoadRequested;
        public event Action?             ReturnToLobbyRequested;
        public event Action<SongOffer>?  SongOffered;
        public event Action<SongChunk>?  SongChunkReceived;

        public ClientSession(INetTransport transport, SessionConfig config, Func<double> clock)
        {
            _transport = transport;
            _config = config;
            _clock = clock;
        }

        public void Connect(string host, int port)
        {
            _lastHeard = _clock();
            _transport.Connect(host, port);
        }

        public void Tick()
        {
            if (State == SessionState.Ended)
            {
                return;
            }

            _transport.Poll(this);
            if (State == SessionState.Ended)
            {
                return;
            }

            double now = _clock();
            if (_connected && now - _lastPing >= _config.PingIntervalSeconds)
            {
                _lastPing = now;
                Send(new Ping(now));
            }

            double limit = _welcomed ? _config.TimeoutSeconds : _config.ConnectTimeoutSeconds;
            if (now - _lastHeard > limit)
            {
                _transport.Disconnect(HOST_PEER_ID);
                End("connection to the host timed out");
            }
        }

        public void RespondToOffer(bool haveSong)
        {
            var offer = CurrentOfferOrThrow();
            Send(haveSong ? new HaveSong(offer.Hash) : (NetMessage) new NeedSong(offer.Hash));
        }

        public void ReportSongReady()
        {
            Send(new SongReady(CurrentOfferOrThrow().Hash));
        }

        public void ReportSongFailed(string reason)
        {
            Send(new SongFailed(CurrentOfferOrThrow().Hash, reason));
        }

        public void SetLocalPart(Instrument instrument, Difficulty difficulty)
        {
            Send(new SetPart(instrument, difficulty));
        }

        public void SendGameMessage(NetMessage message)
        {
            GameMessages.EnsureGameMessage(message);
            Send(message);
        }

        public void ReportLoaded()
        {
            Send(new Loaded());
        }

        public void Leave()
        {
            if (State == SessionState.Ended)
            {
                return;
            }

            Send(new Leave("left"));
            _transport.Disconnect(HOST_PEER_ID);
            State = SessionState.Ended;
        }

        public void Dispose()
        {
            _transport.Dispose();
            State = SessionState.Ended;
        }

        void INetEventListener.OnPeerConnected(int peerId)
        {
            _connected = true;
            _lastHeard = _clock();
            Send(new Hello(ProtocolInfo.Version, _config.GameVersion, _config.LocalProfileName));
        }

        void INetEventListener.OnPeerDisconnected(int peerId, string reason)
        {
            End($"disconnected from the host: {reason}");
        }

        void INetEventListener.OnMessage(int peerId, byte[] data)
        {
            if (State == SessionState.Ended)
            {
                return;
            }

            double now = _clock();
            _lastHeard = now;

            NetMessage message;
            try
            {
                message = MessageCodec.Decode(data);
            }
            catch (InvalidDataException e)
            {
                _config.Log($"Malformed message from the host: {e.Message}");
                _transport.Disconnect(HOST_PEER_ID);
                End("received a malformed message from the host");
                return;
            }

            switch (message)
            {
                case Welcome m:
                    LocalPlayerId = m.PlayerId;
                    _welcomed = true;
                    break;
                case Reject m:
                    RejectReason = m;
                    _transport.Disconnect(HOST_PEER_ID);
                    End($"rejected by the host: {m.Code} (host version {m.HostGameVersion})");
                    break;
                case LobbyState m:
                    Players = m.Players;
                    CurrentOffer = m.Offer;
                    State = m.State;
                    LobbyChanged?.Invoke();
                    break;
                case SongOffer m:
                    CurrentOffer = m;
                    SongOffered?.Invoke(m);
                    break;
                case SongChunk m:
                    if (CurrentOffer != null && CurrentOffer.Hash.Equals(m.Hash))
                    {
                        SongChunkReceived?.Invoke(m);
                    }
                    break;
                case BeginLoad _:
                    State = SessionState.Loading;
                    BeginLoadRequested?.Invoke();
                    break;
                case StartAt m:
                    if (!_clockSync.HasEstimate)
                    {
                        _config.Log("Start time received before any clock sample; assuming synced clocks");
                    }
                    StartTimeLocal = _clockSync.HasEstimate ? _clockSync.HostToLocal(m.HostTime) : m.HostTime;
                    State = SessionState.Playing;
                    break;
                case ReturnToLobby _:
                    State = SessionState.Lobby;
                    StartTimeLocal = null;
                    ReturnToLobbyRequested?.Invoke();
                    break;
                case Ping m:
                    Send(new Pong(m.SentAt, now));
                    break;
                case Pong m:
                    _clockSync.AddSample(m.PingSentAt, m.ReplySentAt, now);
                    break;
                case Leave m:
                    End($"the host left: {m.Reason}");
                    break;
                default:
                    if (GameMessages.TryGetPlayerId(message, out _))
                    {
                        GameMessageReceived?.Invoke(message);
                    }
                    else
                    {
                        _config.Log($"Ignoring unexpected {message.Type} from the host");
                    }
                    break;
            }
        }

        private SongOffer CurrentOfferOrThrow() =>
            CurrentOffer ?? throw new InvalidOperationException("No song has been offered");

        private void Send(NetMessage message)
        {
            _transport.Send(HOST_PEER_ID, MessageCodec.Encode(message));
        }

        private void End(string reason)
        {
            if (State == SessionState.Ended)
            {
                return;
            }

            _config.Log($"Session ended: {reason}");
            State = SessionState.Ended;
            Ended?.Invoke(reason);
        }
    }
}
