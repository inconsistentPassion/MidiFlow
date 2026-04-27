using NAudio.Midi;

namespace PianoFlow.Models;

/// <summary>
/// Wrapper around NAudio MidiIn for live MIDI device input.
/// Fires NoteOn/NoteOff events on the UI thread via dispatcher.
/// </summary>
public class MidiDeviceInput : IDisposable
{
    private MidiIn? _midiIn;
    private int _deviceIndex = -1;

    /// <summary>Fired when a note is pressed. Parameters: note, velocity, channel.</summary>
    public event Action<int, int, int>? NoteOn;

    /// <summary>Fired when a note is released. Parameters: note, channel.</summary>
    public event Action<int, int>? NoteOff;

    /// <summary>Fired for any MIDI channel message. Parameters: status, data1, data2.</summary>
    public event Action<byte, byte, byte>? MessageReceived;

    /// <summary>Currently connected device name, or null.</summary>
    public string? DeviceName { get; private set; }

    /// <summary>Whether a device is currently connected.</summary>
    public bool IsConnected => _midiIn != null;

    /// <summary>Get list of available MIDI input device names.</summary>
    public static List<(int Index, string Name)> GetDevices()
    {
        var devices = new List<(int, string)>();
        for (int i = 0; i < MidiIn.NumberOfDevices; i++)
        {
            var caps = MidiIn.DeviceInfo(i);
            devices.Add((i, caps.ProductName));
        }
        return devices;
    }

    /// <summary>Open a MIDI input device by index.</summary>
    public void Open(int deviceIndex)
    {
        Close();

        if (deviceIndex < 0 || deviceIndex >= MidiIn.NumberOfDevices)
            throw new ArgumentOutOfRangeException(nameof(deviceIndex));

        _midiIn = new MidiIn(deviceIndex);
        _deviceIndex = deviceIndex;
        DeviceName = MidiIn.DeviceInfo(deviceIndex).ProductName;

        _midiIn.MessageReceived += OnMessageReceived;
        _midiIn.ErrorReceived += OnErrorReceived;
        _midiIn.Start();
    }

    /// <summary>Close the current device.</summary>
    public void Close()
    {
        if (_midiIn != null)
        {
            _midiIn.Stop();
            _midiIn.MessageReceived -= OnMessageReceived;
            _midiIn.ErrorReceived -= OnErrorReceived;
            _midiIn.Dispose();
            _midiIn = null;
            _deviceIndex = -1;
            DeviceName = null;
        }
    }

    private void OnMessageReceived(object? sender, MidiInMessageEventArgs e)
    {
        var midiEvent = e.MidiEvent;
        if (midiEvent == null) return;

        if (midiEvent.CommandCode == MidiCommandCode.NoteOn)
        {
            var noteOn = (NoteEvent)midiEvent;
            if (noteOn.Velocity > 0)
            {
                NoteOn?.Invoke(noteOn.NoteNumber, noteOn.Velocity, noteOn.Channel - 1);
            }
            else
            {
                // velocity 0 = note off
                NoteOff?.Invoke(noteOn.NoteNumber, noteOn.Channel - 1);
            }
        }
        else if (midiEvent.CommandCode == MidiCommandCode.NoteOff)
        {
            var noteOff = (NoteEvent)midiEvent;
            NoteOff?.Invoke(noteOff.NoteNumber, noteOff.Channel - 1);
        }

        // Fire generic message event for all channel messages
        if ((int)midiEvent.CommandCode < 0xF0)
        {
            byte status = (byte)(e.RawMessage & 0xFF);
            byte d1 = (byte)((e.RawMessage >> 8) & 0xFF);
            byte d2 = (byte)((e.RawMessage >> 16) & 0xFF);
            MessageReceived?.Invoke(status, d1, d2);
        }
    }

    private void OnErrorReceived(object? sender, MidiInMessageEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"MIDI Error: {e.RawMessage:X8}");
    }

    public void Dispose()
    {
        Close();
    }
}
