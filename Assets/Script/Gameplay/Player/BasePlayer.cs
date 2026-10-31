using System.Collections.Generic;
using PlasticBand.Haptics;
using UnityEngine;
using UnityEngine.InputSystem;
using YARG.Core;
using YARG.Core.Audio;
using YARG.Core.Chart;
using YARG.Core.Engine;
using YARG.Core.Game;
using YARG.Core.Input;
using YARG.Core.Logging;
using YARG.Core.Replays;
using YARG.Gameplay.HUD;
using YARG.Helpers.Extensions;
using YARG.Helpers.UI;
using YARG.Input;
using YARG.Multiplayer;
using YARG.Player;
using YARG.Settings;

namespace YARG.Gameplay.Player
{
    public abstract class BasePlayer : GameplayBehaviour
    {
        public int HighwayIndex { get; private set; }

        /// <summary>Id under which this player is counted in the stem mute tracker.</summary>
        public int StemParticipantId { get; set; }

        public YargPlayer Player { get; private set; }

        public float NoteSpeed
        {
            get
            {
                float noteSpeed = Player.Profile.NoteSpeed * _noteSpeedDifficultyScale;

                // If we're in a replay, don't change the note speed (it should be like a video
                // slowing down/speeding up). The actual song speed should be taken into account though,
                // which is saved in the engine parameter override.
                if (Player.IsReplay)
                {
                    return noteSpeed / (float) Player.EngineParameterOverride.SongSpeed;
                }

                if (GameManager.IsPractice && GameManager.SongSpeed < 1)
                {
                    return noteSpeed;
                }

                return noteSpeed / GameManager.SongSpeed;
            }
        }

        /// <summary>
        /// The player's input calibration, in seconds.
        /// </summary>
        /// <remarks>
        /// Be aware that this value is negated!
        /// Positive calibration settings will result in a negative number here.
        /// </remarks>
        public double InputCalibration => -Player.Profile.InputCalibrationSeconds;

        public abstract BaseEngine BaseEngine { get; }

        public BaseStats BaseStats => BaseEngine.BaseStats;
        public BaseEngineParameters BaseParameters => BaseEngine.BaseParameters;

        /// <summary>
        /// <p> Star thresholds, from 1 to 5 stars, then gold stars. </p>
        /// <p> These values represent multiples of the score if you were to FC, hold all sustains fully, hit no dynamics, and use no star power. </p>
        /// <p> Multiplying these by the max multiplier of the instrument will also roughly give you the average multiplier needed for that star. </p>
        /// </summary>
        protected abstract float[] StarMultiplierThresholds { get; set; }

        /// <summary>
        /// Multiples of the maximum points it is possible to get from a solo, which is then added to each star point threshold (1 to 5, then gold stars).
        /// <seealso cref="StarMultiplierThresholds"/>
        /// </summary>
        protected readonly float[] SoloBonusStarMultiplierThresholds = {
            0.05f, 0.1f, 0.2f, 0.35f, 0.65f, 0.95f
        };

        public abstract bool ShouldUpdateInputsOnResume { get; }

        public HitWindowSettings HitWindow { get; protected set; }

        public float Stars => BaseStats.Stars;

        public int Score => BaseStats.TotalScore;
        public int BandBonusScore => BaseStats.BandBonusScore;
        public int Combo => BaseStats.Combo;
        public int NotesHit => BaseStats.NotesHit;

        public int TotalNotes { get; protected set; }

        public bool IsFc { get; protected set; }

        public int? LastHighScore { get; private set; }

        public IReadOnlyList<GameInput> ReplayInputs => _replayInputs.AsReadOnly();

        private Dictionary<int, GameInput> LastInputs { get; } = new();
        private Dictionary<int, GameInput> InputsToSendOnResume { get; } = new();

        protected SongChart Chart { get; private set; }

        protected SyncTrack SyncTrack { get; private set; }

        protected bool IsInitialized { get; private set; }

        protected List<ISantrollerHaptics> SantrollerHaptics { get; private set; } = new();

        protected BaseInputViewer InputViewer { get; private set; }

        protected int  LastCombo;
        protected bool IsStemMuted;

        private List<GameInput> _replayInputs;

        private int _replayInputIndex;

        /// <summary>
        /// How far behind the local song time a remote player's engine currently is.
        /// Its track is drawn this much in the past so notes and hits line up.
        /// </summary>
        public double RemoteLagSeconds { get; private set; }

        private double _remoteEngineTime = double.NegativeInfinity;

        private float _noteSpeedDifficultyScale;

        public EngineManager.EngineContainer EngineContainer;

        protected bool PlayerHasFailed;

        public bool IsActive => Player.IsActive;
        public bool IsBot    => Player.Profile.IsBot;

        protected override void GameplayAwake()
        {
            _replayInputs = new List<GameInput>();

            // TODO: Couldn't there be more than one input viewer?
            //  We were using FindObjectOfType<BaseInputViewer> before anyway, so we're no worse off in that respect
            InputViewer = FindFirstObjectByType<BaseInputViewer>();

            IsFc = true;
        }

        private void Update()
        {
            //Ensure hud elements get repositioned on screen size change
            if (ScreenSizeDetector.HasScreenSizeChanged)
            {
                UpdateVisuals(GetPlayerVisualTime(GameManager.VisualTime));
            }
        }

        /// <summary>
        /// The visual time this player's track is drawn at. Track elements must use this instead of
        /// <see cref="GameManager.VisualTime"/>, so a remote track's notes line up with its lagging engine.
        /// </summary>
        public double PlayerVisualTime => GetPlayerVisualTime(GameManager.VisualTime);

        private double GetPlayerVisualTime(double visualTime)
        {
            // The vocal track is shared by every singer, so remote singers stay on local time
            if (Player.IsRemote && Player.Profile.GameMode != GameMode.Vocals)
            {
                return visualTime - RemoteLagSeconds;
            }
            return visualTime;
        }

        protected void Start()
        {
            if (Player.Bindings is not null)
            {
                SantrollerHaptics = Player.Bindings.GetDevicesByType<ISantrollerHaptics>();
            }

            if (!Player.IsReplay)
            {
                SubscribeToInputEvents();
            }
        }

        protected void Initialize(int index, YargPlayer player, SongChart chart, int? lastHighScore)
        {
            if (IsInitialized)
            {
                return;
            }

            HighwayIndex = index;
            Player = player;

            if (!Player.IsReplay)
            {
                Player.MenuInput += OnMenuInput;
            }

            Chart = chart;
            SyncTrack = chart.SyncTrack;

            LastHighScore = lastHighScore;

            _noteSpeedDifficultyScale = Player.Profile.CurrentDifficulty.NoteSpeedScale();

            if (Player.IsReplay && GameManager.ReplayInfo != null)
            {
                _replayInputs = new List<GameInput>(GameManager.ReplayData.Frames[player.ReplayIndex].Inputs);
                YargLogger.LogFormatDebug("Initialized replay inputs with {0} inputs", _replayInputs.Count);
            }

            if (InputViewer != null)
            {
                InputViewer.SetColors(player.ColorProfile);
                InputViewer.ResetButtons();
            }

            IsInitialized = true;
        }

        public virtual void GameplayUpdate()
        {
            if (!GameManager.Started || GameManager.Paused)
            {
                return;
            }

            if (!GameManager.Rewinding)
            {
                UpdateInputs(GameManager.InputTime);
            }

            UpdateVisuals(GetPlayerVisualTime(GameManager.VisualTime));
        }

        protected abstract void UpdateVisuals(double visualTime);
        protected abstract void ResetVisuals();
        public abstract void Rewind(double visualTime);
        public abstract void PostRewind(double visualTime);

        protected virtual void ResetDifficulty(double time)
        {
            _noteSpeedDifficultyScale = Player.Profile.CurrentDifficulty.NoteSpeedScale();
        }

        public virtual void ResetPracticeSection()
        {
            LastCombo = 0;

            IsFc = true;

            ResetVisuals();
        }

        public abstract void SetPracticeSection(uint start, uint end);

        // TODO Make this more generic
        public abstract void SetStemMuteState(bool muted);

        public virtual void SetStarPowerFX(bool active)
        {
            GameManager.ChangeStemReverbState(SongStem.Song, active);
        }

        public virtual void SetReplayTime(double time)
        {
            IsFc = true;

            _replayInputIndex = BaseEngine.ProcessUpToTime(time, ReplayInputs);

            SetStemMuteState(false);

            ResetVisuals();
            UpdateVisuals(time);
        }

        protected override void GameplayDestroy()
        {
            if (!Player.IsReplay)
            {
                Player.MenuInput -= OnMenuInput;
                UnsubscribeFromInputEvents();
            }

            FinishDestruction();
        }

        protected virtual void FinishDestruction()
        {
        }

        protected virtual void UpdateInputs(double time)
        {
            if (!IsActive)
            {
                return;
            }

            if (Player.IsRemote)
            {
                UpdateRemoteInputs(time);
                return;
            }

            // Apply input offset
            // Video offset is already accounted for
            time += InputCalibration;

            if (Player.IsReplay && GameManager.ReplayInfo != null)
            {
                while (_replayInputIndex < ReplayInputs.Count)
                {
                    var input = ReplayInputs[_replayInputIndex];

                    // Current input does not meet the time requirement
                    if (time < input.Time)
                    {
                        break;
                    }

                    BaseEngine.QueueInput(ref input);
                    OnInputQueued(input);

                    _replayInputIndex++;
                }
            }

            BaseEngine.Update(time);

            if (MultiplayerGameplay.IsInMatch && !Player.IsReplay)
            {
                MultiplayerGameplay.OnLocalEngineTime(this, time);
            }
        }

        private void UpdateRemoteInputs(double time)
        {
            // Remote input times already include the sender's calibration, so none is applied here.
            // The engine only runs up to what the sender has confirmed, so late packets freeze
            // the track instead of turning into misses.
            var inputs = Player.RemoteInputs;
            double target = inputs.GetTargetTime(time);
            if (double.IsNegativeInfinity(target))
            {
                RemoteLagSeconds = inputs.DelaySeconds;
                return;
            }

            while (inputs.TryDequeue(target, out var input))
            {
                BaseEngine.QueueInput(ref input);
                OnInputQueued(input);
                _replayInputs.Add(input);
            }

            if (target > _remoteEngineTime)
            {
                _remoteEngineTime = target;
                BaseEngine.Update(target);
            }
            RemoteLagSeconds = time - _remoteEngineTime;
        }

        private void SubscribeToInputEvents()
        {
            Player.Bindings.SubscribeToGameplayInputs(Player.Profile.GameMode, OnGameInput);

            Player.Bindings.DeviceAdded += OnDeviceAdded;
            Player.Bindings.DeviceRemoved += OnDeviceRemoved;
        }

        private void UnsubscribeFromInputEvents()
        {
            Player.Bindings.UnsubscribeFromGameplayInputs(Player.Profile.GameMode, OnGameInput);

            Player.Bindings.DeviceAdded -= OnDeviceAdded;
            Player.Bindings.DeviceRemoved -= OnDeviceRemoved;
        }

        private void OnDeviceAdded(InputDevice device)
        {
            if (device is ISantrollerHaptics haptics)
            {
                SantrollerHaptics.Add(haptics);
            }
        }

        private void OnDeviceRemoved(InputDevice device)
        {
            if (device is ISantrollerHaptics haptics)
            {
                SantrollerHaptics.Remove(haptics);
            }

            if (GameManager != null && !GameManager.Paused && SettingsManager.Settings.PauseOnDeviceDisconnect.Value)
            {
                GameManager.SetPaused(true);
            }
        }

        public void SendInputsOnResume()
        {
            foreach (var originalInput in InputsToSendOnResume.Values)
            {
                var input = new GameInput(InputManager.CurrentInputTime, originalInput.Action, originalInput.Integer);
                OnGameInput(ref input);
            }

            InputsToSendOnResume.Clear();
        }

        protected virtual bool IsMenuOpen => false;

        protected virtual void OnMenuInput(YargPlayer player, ref GameInput input)
        {
        }

        protected void OnGameInput(ref GameInput input)
        {
            // Ignore completely if the song hasn't started yet or player failed
            if (!GameManager.Started || PlayerHasFailed || !IsActive)
            {
                YargLogger.LogFormatDebug("Ignoring input: Started: {0} Failed: {1} IsActive: {2}", GameManager.Started, PlayerHasFailed, IsActive);
                return;
            }

            if (IsMenuOpen && !SettingsManager.Settings.PauseOnMenuOpen.Value)
            {
                return;
            }

            // Ignore while paused
            if (GameManager.Paused || GameManager.Rewinding)
            {
                if (!ShouldUpdateInputsOnResume)
                {
                    return;
                }

                if (LastInputs.TryGetValue(input.Action, out var lastInput))
                {
                    if (lastInput.Button != input.Button)
                    {
                        InputsToSendOnResume[input.Action] = input;
                    }
                    else
                    {
                        InputsToSendOnResume.Remove(input.Action);
                    }
                }

                return;
            }

            LastInputs[input.Action] = input;

            double adjustedTime = GameManager.GetInputTime(input.Time);
            // Apply input offset
            adjustedTime += InputCalibration;
            input = new(adjustedTime, input.Action, input.Integer);

            // Allow the input to be explicitly ignored before processing it
            if (InterceptInput(ref input)) return;

            BaseEngine.QueueInput(ref input);
            OnInputQueued(input);
            _replayInputs.Add(input);

            if (MultiplayerGameplay.IsInMatch)
            {
                MultiplayerGameplay.OnLocalInput(this, input);
            }
        }

        protected virtual void OnStarPowerPhraseHit()
        {
            if (!GameManager.Paused && !GameManager.IsSeekingReplay)
            {
                GlobalAudioHandler.PlaySoundEffect(SfxSample.StarPowerAward);
            }
        }

        protected virtual void OnStarPowerReady()
        {
            if (!GameManager.Paused && !GameManager.IsSeekingReplay)
            {
                GlobalAudioHandler.PlaySoundEffect(SfxSample.StarPowerReady);
            }
        }

        protected virtual void OnStarPowerPhraseMissed()
        {

        }

        protected virtual void OnStarPowerStatus(bool active)
        {
            var deploySample = SfxSample.StarPowerDeploy;
            if (SettingsManager.Settings.UseCrowdCheering.Value &&
                !GlobalVariables.State.CrowdSfxVenueOverride)
            {
                deploySample = SfxSample.StarPowerDeployCrowd;
            }

            if (!GameManager.Paused)
            {
                GlobalAudioHandler.PlaySoundEffect(active
                    ? deploySample
                    : SfxSample.StarPowerRelease);

                SetStarPowerFX(active);
            }

            GameManager.ChangeStarPowerStatus(active);

            foreach (var haptics in SantrollerHaptics)
            {
                haptics.SetStarPowerActive(active);
            }
        }

        protected abstract bool InterceptInput(ref GameInput input);

        protected virtual void OnInputQueued(GameInput input)
        {
            if (InputViewer != null)
            {
                InputViewer.OnInput(input);
            }
        }

        protected void OnComboIncrement(int amount)
        {
            GameManager.AddBandCombo(amount);
        }

        protected void OnComboReset()
        {
            GameManager.ResetBandCombo();
        }

        public abstract (ReplayFrame Frame, ReplayStats Stats) ConstructReplayData();
    }
}
