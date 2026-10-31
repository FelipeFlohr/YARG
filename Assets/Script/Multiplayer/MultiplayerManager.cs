using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using Cysharp.Threading.Tasks;
using UnityEngine;
using YARG.Core.Logging;
using YARG.Core.Song;
using YARG.Localization;
using YARG.Menu.Persistent;
using YARG.Multiplayer.Protocol;
using YARG.Multiplayer.Session;
using YARG.Multiplayer.SongShare;
using YARG.Multiplayer.Transport;
using YARG.Player;
using YARG.Settings;

namespace YARG.Multiplayer
{
    /// <summary>
    /// Owns the online session for the whole game: ticks it, downloads offered songs on clients,
    /// and moves everyone into gameplay when the host starts.
    /// </summary>
    public sealed class MultiplayerManager : MonoBehaviour
    {
        private const int MAX_DOWNLOAD_ATTEMPTS = 2;

        private static MultiplayerManager _instance;

        public static MultiplayerManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    var gameObject = new GameObject("Multiplayer Manager");
                    DontDestroyOnLoad(gameObject);
                    _instance = gameObject.AddComponent<MultiplayerManager>();

                    // Leftovers from a crash
                    MultiplayerSongLibrary.CleanupTemp();
                }
                return _instance;
            }
        }

        public static bool IsActive  => _instance != null && _instance.Session != null;
        public static bool IsHosting => IsActive && _instance.Session.IsHost;

        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private SongPackage  _package;
        private SongReceiver _receiver;
        private SongEntry    _offeredSong;
        private int          _downloadAttempts;
        private SceneIndex   _lastScene;

#nullable enable
        public IMultiplayerSession? Session { get; private set; }

        /// <summary>
        /// Monotonic clock shared by everything multiplayer, in seconds.
        /// </summary>
        public double Now => _clock.Elapsed.TotalSeconds;

        /// <summary>
        /// The one player on this machine taking part in the session.
        /// </summary>
        public YargPlayer? LocalPlayer => PlayerContainer.Players.FirstOrDefault(player => !player.SittingOut);

        /// <summary>
        /// Raised when a session starts or stops, for the menus.
        /// </summary>
        public event Action? SessionChanged;
#nullable disable

        public bool Host(int port = ProtocolInfo.DefaultPort)
        {
            EnsureNoSession();

            var session = new HostSession(new TcpTransport(), CreateConfig(), () => Now);
            try
            {
                session.Start(port);
            }
            catch (SocketException e)
            {
                YargLogger.LogException(e, "Failed to start hosting");
                ToastManager.ToastError(Localize.KeyFormat("Menu.Multiplayer.HostFailed", e.Message));
                session.Dispose();
                return false;
            }

            Subscribe(session);
            Session = session;
            YargLogger.LogFormatInfo("Hosting a multiplayer session on port {0}", port);
            SessionChanged?.Invoke();
            return true;
        }

        public bool Join(string address)
        {
            EnsureNoSession();

            if (!TryParseAddress(address, out string host, out int port))
            {
                ToastManager.ToastError(Localize.Key("Menu.Multiplayer.InvalidAddress"));
                return false;
            }

            var session = new ClientSession(new TcpTransport(), CreateConfig(), () => Now);
            Subscribe(session);
            session.SongOffered += OnSongOffered;
            session.SongChunkReceived += OnSongChunkReceived;
            Session = session;

            YargLogger.LogFormatInfo("Joining multiplayer session at {0}:{1}", host, port);
            session.Connect(host, port);
            SessionChanged?.Invoke();
            return true;
        }

        public void Leave()
        {
            Session?.Leave();
            EndSession();
        }

        /// <summary>
        /// Host only: offers the song picked in the difficulty select to everybody.
        /// </summary>
        public bool OfferSong(SongEntry song, float songSpeed)
        {
            if (Session is not HostSession host)
            {
                throw new InvalidOperationException("Only the host can offer songs");
            }

            SongPackage package;
            try
            {
                package = MultiplayerSongLibrary.CreatePackage(song);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                YargLogger.LogException(e, "Failed to package song for multiplayer");
                ToastManager.ToastError(Localize.KeyFormat("Menu.Multiplayer.PackageFailed", e.Message));
                return false;
            }

            if (package == null)
            {
                ToastManager.ToastError(Localize.Key("Menu.Multiplayer.UnsupportedSongType"));
                return false;
            }

            _package?.Dispose();
            _package = package;
            _offeredSong = song;

            var offer = new SongOffer(song.Hash, song.Name.Original, song.Artist.Original, package.Kind,
                package.Length, songSpeed);
            host.OfferSong(offer, package.Open);
            return true;
        }

        private void Awake()
        {
            _lastScene = GlobalVariables.Instance.CurrentScene;
        }

        private void Update()
        {
            var scene = GlobalVariables.Instance.CurrentScene;
            if (scene != _lastScene)
            {
                _lastScene = scene;
                if (scene == SceneIndex.Menu && Session != null)
                {
                    OnReturnedToMenu();
                }
            }

            if (Session != null)
            {
                try
                {
                    Session.Tick();
                }
                catch (Exception e)
                {
                    YargLogger.LogException(e, "Error while updating the multiplayer session");
                }
            }

            MultiplayerMenuFlow.Update();
        }

        private void OnReturnedToMenu()
        {
            // Back from the score screen: the host takes everybody back to the lobby
            if (Session is HostSession host && host.State == SessionState.Results)
            {
                host.ReturnToLobby();
            }
            MultiplayerMenuFlow.RequestOpenLobby();
        }

        private void OnApplicationQuit()
        {
            Leave();
        }

        private SessionConfig CreateConfig()
        {
            var player = LocalPlayer ?? throw new InvalidOperationException("No active local player");
            return new SessionConfig(GlobalVariables.Instance.CurrentVersion, player.Profile.Name,
                log: message => YargLogger.LogFormatInfo("[Multiplayer] {0}", message));
        }

        private void EnsureNoSession()
        {
            if (Session != null)
            {
                throw new InvalidOperationException("Already in a multiplayer session");
            }
        }

        private void Subscribe(IMultiplayerSession session)
        {
            session.Ended += OnSessionEnded;
            session.BeginLoadRequested += OnBeginLoadRequested;
            session.LobbyChanged += MultiplayerMenuFlow.OnLobbyChanged;
        }

        private void EndSession()
        {
            if (Session == null)
            {
                return;
            }

            MultiplayerGameplay.EndMatch();
            Session.Dispose();
            Session = null;

            _receiver?.Dispose();
            _receiver = null;
            _package?.Dispose();
            _package = null;
            _offeredSong = null;

            if (!SettingsManager.Settings.SaveMultiplayerSongs.Value)
            {
                MultiplayerSongLibrary.CleanupTemp();
            }

            SessionChanged?.Invoke();
        }

        private void OnSessionEnded(string reason)
        {
            ToastManager.ToastError(Localize.KeyFormat("Menu.Multiplayer.SessionEnded", reason));
            EndSession();

            if (GlobalVariables.Instance.CurrentScene == SceneIndex.Gameplay)
            {
                GlobalVariables.Instance.LoadScene(SceneIndex.Menu);
            }
        }

        private void OnBeginLoadRequested()
        {
            var session = Session;
            var offer = session.CurrentOffer;
            var self = session.Players.FirstOrDefault(p => p.PlayerId == session.LocalPlayerId);
            if (offer == null || self == null || self.Status == LobbyPlayerStatus.SongFailed)
            {
                ToastManager.ToastWarning(Localize.Key("Menu.Multiplayer.SittingOutSong"));
                return;
            }

            var song = session.IsHost ? _offeredSong : MultiplayerSongLibrary.FindByHash(offer.Hash);
            if (song == null)
            {
                YargLogger.LogError("The song to play is missing from the library");
                ToastManager.ToastError(Localize.Key("Menu.Multiplayer.SongMissing"));
                Leave();
                return;
            }

            MultiplayerGameplay.BeginMatch(session);

            GlobalVariables.State = PersistentState.Default;
            GlobalVariables.State.CurrentSong = song;
            GlobalVariables.State.SongSpeed = offer.SongSpeed;
            GlobalVariables.State.PlayingWithReplay = false;
            GlobalVariables.Instance.LoadScene(SceneIndex.Gameplay);
        }

        private void OnSongOffered(SongOffer offer)
        {
            var client = (ClientSession) Session;

            _receiver?.Dispose();
            _receiver = null;
            _downloadAttempts = 0;

            if (MultiplayerSongLibrary.FindByHash(offer.Hash) != null)
            {
                client.RespondToOffer(true);
            }
            else if (offer.TotalBytes > ProtocolInfo.MaxSongBytes)
            {
                client.ReportSongFailed("song too large");
            }
            else
            {
                StartDownload(client, offer);
            }
        }

        private void StartDownload(ClientSession client, SongOffer offer)
        {
            _downloadAttempts++;
            try
            {
                _receiver = new SongReceiver(offer, MultiplayerSongLibrary.StagingRoot);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                YargLogger.LogException(e, "Failed to prepare the song download");
                client.ReportSongFailed(e.Message);
                return;
            }
            client.RespondToOffer(false);
        }

        private void OnSongChunkReceived(SongChunk chunk)
        {
            var client = (ClientSession) Session;
            var receiver = _receiver;
            if (receiver == null)
            {
                return;
            }

            try
            {
                receiver.Write(chunk);
            }
            catch (Exception e) when (e is InvalidDataException || e is IOException)
            {
                YargLogger.LogException(e, "Failed to write song chunk");
                receiver.Dispose();
                _receiver = null;
                client.ReportSongFailed(e.Message);
                return;
            }

            if (receiver.IsComplete)
            {
                _receiver = null;
                InstallAsync(client, receiver, client.CurrentOffer).Forget();
            }
        }

        private async UniTaskVoid InstallAsync(ClientSession client, SongReceiver receiver, SongOffer offer)
        {
            SongEntry entry = null;
            try
            {
                entry = await MultiplayerSongLibrary.InstallAndRegister(receiver, offer);
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException ||
                e is UnauthorizedAccessException)
            {
                YargLogger.LogException(e, "Failed to install multiplayer song");
            }
            finally
            {
                receiver.Dispose();
            }

            // The session or the offer changed while installing
            if (Session != client || client.CurrentOffer == null || !client.CurrentOffer.Hash.Equals(offer.Hash))
            {
                return;
            }

            if (entry != null)
            {
                client.ReportSongReady();
            }
            else if (_downloadAttempts < MAX_DOWNLOAD_ATTEMPTS)
            {
                YargLogger.LogWarning("Downloaded song didn't match the offer, downloading again");
                StartDownload(client, offer);
            }
            else
            {
                client.ReportSongFailed("downloaded song doesn't match the offer");
            }
        }

        private static bool TryParseAddress(string address, out string host, out int port)
        {
            host = address.Trim();
            port = ProtocolInfo.DefaultPort;

            // [IPv6]:port
            if (host.StartsWith("["))
            {
                int end = host.IndexOf(']');
                if (end < 0)
                {
                    return false;
                }

                string rest = host.Substring(end + 1);
                host = host.Substring(1, end - 1);
                return rest.Length == 0 || (rest.StartsWith(":") && int.TryParse(rest.Substring(1), out port));
            }

            // host:port (a bare IPv6 address has several colons and no port)
            int colon = host.IndexOf(':');
            if (colon >= 0 && colon == host.LastIndexOf(':'))
            {
                if (!int.TryParse(host.Substring(colon + 1), out port))
                {
                    return false;
                }
                host = host.Substring(0, colon);
            }

            return host.Length > 0 && port > 0 && port <= ushort.MaxValue;
        }
    }
}
