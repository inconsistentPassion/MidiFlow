using NFluidsynth;
using NAudio.Wave;
using System.Collections.Concurrent;

namespace PianoFlow.Audio;

/// <summary>
/// Audio synthesis engine using FluidSynth (via SpaceWizards.NFluidsynth).
/// FluidSynth is the industry standard for SoundFont rendering, supporting
/// 4,000+ voices and high-quality orchestral playback.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private Settings? _settings;
    private Synth? _synth;
    private IWavePlayer? _waveOut;
    private SynthWaveProvider? _waveProvider;
    private readonly object _synthLock = new();
    private readonly ConcurrentQueue<MidiEvent> _eventQueue = new();
    private bool _disposed;
    
    private struct MidiEvent {
        public int Channel;
        public int Command;
        public int Data1;
        public int Data2;
    }

    private string? _soundFontPath;
    private int _sampleRate = 44100;
    private float _volume = 1.0f;

    public bool IsReady => _synth != null && _waveOut != null;
    public string? SoundFontPath => _soundFontPath;

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            if (_waveOut != null)
                _waveOut.Volume = _volume;
        }
    }

    /// <summary>
    /// Load a SoundFont and start the FluidSynth engine.
    /// </summary>
    public void Initialize(string soundFontPath, int sampleRate = 44100)
    {
        Dispose();
        _disposed = false;

        _sampleRate = sampleRate;
        _soundFontPath = soundFontPath;

        lock (_synthLock)
        {
            _settings = new Settings();
            _settings[ConfigurationKeys.SynthSampleRate].DoubleValue = _sampleRate;
            _settings[ConfigurationKeys.SynthPolyphony].IntValue = 4096;
            _settings[ConfigurationKeys.SynthGain].DoubleValue = 0.6;
            
            // Enable multi-threaded rendering in FluidSynth to reduce lag
            _settings[ConfigurationKeys.SynthCpuCores].IntValue = Environment.ProcessorCount > 1 ? 2 : 1;

            _synth = new Synth(_settings);
            _synth.LoadSoundFont(soundFontPath, true);
        }

        _waveProvider = new SynthWaveProvider(this, _sampleRate);

        _waveOut = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 40);
        _waveOut.Init(_waveProvider);
        _waveOut.Volume = _volume;
        _waveOut.Play();
    }

    /// <summary>Send a Note-On event to FluidSynth (Queued).</summary>
    public void NoteOn(int channel, int note, int velocity)
    {
        _eventQueue.Enqueue(new MidiEvent { Channel = channel, Command = 0x90, Data1 = note, Data2 = velocity });
    }

    /// <summary>Send a Note-Off event to FluidSynth (Queued).</summary>
    public void NoteOff(int channel, int note)
    {
        _eventQueue.Enqueue(new MidiEvent { Channel = channel, Command = 0x80, Data1 = note });
    }

    /// <summary>Queue a raw MIDI message to be processed by the audio thread.</summary>
    public void ProcessMidiMessage(int channel, int command, int data1, int data2)
    {
        _eventQueue.Enqueue(new MidiEvent { Channel = channel, Command = command, Data1 = data1, Data2 = data2 });
    }

    /// <summary>Turn off all notes on all channels.</summary>
    public void AllNotesOff()
    {
        lock (_synthLock)
        {
            if (_synth == null) return;
            for (int ch = 0; ch < 16; ch++)
                _synth.AllNotesOff(ch);
        }
    }

    /// <summary>Render high-fidelity samples using FluidSynth inner rendering loop.</summary>
    internal void RenderSamples(float[] left, float[] right, int count)
    {
        lock (_synthLock)
        {
            if (_synth == null) return;

            // 1. Process all queued MIDI events exactly when needed for the next buffer
            // This prevents the UI thread from being blocked by the synth rendering lock.
            while (_eventQueue.TryDequeue(out var ev))
            {
                // Sanitize inputs to prevent FluidSynth from throwing exceptions on invalid MIDI data
                int ch = Math.Clamp(ev.Channel, 0, 15);
                int key = Math.Clamp(ev.Data1, 0, 127);
                int vel = Math.Clamp(ev.Data2, 0, 127);

                int status = ev.Command & 0xF0;
                try 
                {
                    switch (status)
                    {
                        case 0x80: _synth.NoteOff(ch, key); break;
                        case 0x90: 
                            if (vel == 0) _synth.NoteOff(ch, key);
                            else _synth.NoteOn(ch, key, vel); 
                            break;
                        case 0xB0: _synth.CC(ch, key, vel); break;
                        case 0xC0: _synth.ProgramChange(ch, key); break;
                        case 0xE0: 
                            int pbValue = (ev.Data2 << 7) | ev.Data1;
                            _synth.PitchBend(ch, Math.Clamp(pbValue, 0, 16383)); 
                            break;
                    }
                }
                catch (Exception ex)
                {
                    // If a specific MIDI event fails, we skip it rather than crashing the whole audio engine.
                    // This happens occasionally with some SoundFonts or malformed MIDI file transitions.
                    System.Diagnostics.Debug.WriteLine($"FluidSynth MIDI error ({status:X2} ch:{ch} key:{key}): {ex.Message}");
                }
            }

            // 2. Render the audio
            _synth.WriteSampleFloat(count, left, 0, left.Length, 1, right, 0, right.Length, 1);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;

        _waveProvider = null;

        lock (_synthLock)
        {
            _synth?.Dispose();
            _synth = null;
            _settings?.Dispose();
            _settings = null;
        }
    }
}

/// <summary>
/// NAudio IWaveProvider that pulls audio samples from FluidSynth.
/// Includes a soft-limiter to prevent clipping during massive orchestral climaxes.
/// </summary>
internal sealed unsafe class SynthWaveProvider : IWaveProvider
{
    private readonly AudioEngine _engine;
    private readonly WaveFormat _waveFormat;
    private float[] _left;
    private float[] _right;

    public SynthWaveProvider(AudioEngine engine, int sampleRate)
    {
        _engine = engine;
        _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
        _left = new float[sampleRate / 5];
        _right = new float[sampleRate / 5];
    }

    public WaveFormat WaveFormat => _waveFormat;

    public int Read(byte[] buffer, int offset, int count)
    {
        int samplesNeeded = count / 8; // 2 channels, 4 bytes per float
        if (samplesNeeded <= 0) return 0;

        if (_left.Length < samplesNeeded)
        {
            _left = new float[samplesNeeded];
            _right = new float[samplesNeeded];
        }

        _engine.RenderSamples(_left, _right, samplesNeeded);

        fixed (float* pL = _left, pR = _right)
        fixed (byte* pBuf = buffer)
        {
            float* dst = (float*)(pBuf + offset);
            float* srcL = pL;
            float* srcR = pR;
            float* end = srcL + samplesNeeded;

            while (srcL < end)
            {
                float l = *srcL++;
                float r = *srcR++;

                // Soft-knee limiter: extremely important for high-polyphony FluidSynth
                if (l > 0.9f) l = 0.9f + (l - 0.9f) * 0.1f;
                else if (l < -0.9f) l = -0.9f + (l + 0.9f) * 0.1f;

                if (r > 0.9f) r = 0.9f + (r - 0.9f) * 0.1f;
                else if (r < -0.9f) r = -0.9f + (r + 0.9f) * 0.1f;

                if (l > 1.0f) l = 1.0f; else if (l < -1.0f) l = -1.0f;
                if (r > 1.0f) r = 1.0f; else if (r < -1.0f) r = -1.0f;

                *dst++ = l;
                *dst++ = r;
            }
        }

        return count;
    }
}
