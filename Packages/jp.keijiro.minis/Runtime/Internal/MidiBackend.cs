using System;
using System.Collections.Generic;
using Minis.Native;
using UnityEngine;
using UnityEngine.InputSystem;

using static Minis.Native.RtMidi;

namespace Minis
{
    /// <summary>
    /// Manages RtMidi and the devices it reads.
    /// </summary>
    internal sealed class MidiBackend : CustomInputBackend<MidiChannel>
    {
        private RtMidiInHandle _rtMidi;

        // Track port count separately, for better handling of error scenarios
        private uint _lastPortCount = 0;
        private List<MidiPort> _ports = new List<MidiPort>();

        public MidiBackend()
        {
            _rtMidi = rtmidi_in_create_default();
            if (_rtMidi == null || _rtMidi.IsInvalid)
                throw new Exception("Failed to create RtMidi handle!");
            if (!_rtMidi.Ok)
                throw new Exception($"Failed to create RtMidi handle: {_rtMidi.ErrorMessage}");
        }

        protected override void OnDispose()
        {
            foreach (var port in _ports)
                port?.Dispose();
            _ports.Clear();

            _rtMidi?.Dispose();
            _rtMidi = null;
        }

        protected override void OnUpdate()
        {
            // Check for port connections/disconnections
            uint portCount = rtmidi_get_port_count(_rtMidi);
            if (!_rtMidi.Ok)
            {
                Debug.LogError($"Failed to get RtMidi port count: {_rtMidi.ErrorMessage}");
            }
            else if (portCount != _lastPortCount)
            {
                // Update port count first so we don't repeatedly attempt to open ports that failed
                _lastPortCount = portCount;

                // Completely refresh all MIDI devices
                // Not ideal, but no sane way to track which devices have been added/removed
                foreach (var port in _ports)
                    port.Dispose();
                _ports.Clear();

                for (uint port = 0; port < portCount; port++)
                {
                    // Attempt port open 3 times
                    for (int i = 0; i < 3; i++)
                    {
                        try
                        {
                            _ports.Add(new MidiPort(this, port));
                            break;
                        }
                        catch (Exception ex)
                        {
                            Debug.LogException(ex);
                        }
                    }
                }
            }
        }

        protected override MidiChannel OnDeviceAdded(InputDevice device, IDisposable context)
        {
            var channel = (MidiChannel)context;
            channel.OnAdded(device);
            return channel;
        }

        protected override void OnDeviceRemoved(MidiChannel channel)
        {
            channel.OnRemoved();
        }
    }
}