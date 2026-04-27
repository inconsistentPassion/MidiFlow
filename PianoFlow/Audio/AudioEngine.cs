using MeltySynth;
using NAudio.Wave;

namespace PianoFlow.Audio;

/// <summary>
/// Audio synthesis engine using MeltySynth (SoundFont-based).
/// Thread-safe: uses lock since MeltySynth's Synthesizer is not thread-safe,
/// and NAudio calls Read() from a background thread.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private Synthesizer? _synth;
    private WaveOutEvent? _waveOut;
    private SynthWaveProvider? _waveProvider;
    private readonly object _synthLock = new();
    private bool _disposed;

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
    /// Load a SoundFont and start the audio output pipeline.
    /// </summary>
    public void Initialize(string soundFontPath, int sampleRate = 44100)
    {
        Dispose();
        _disposed = false;

        _sampleRate = sampleRate;
        _soundFontPath = soundFontPath;

        lock (_synthLock)
        {
            _synth = new Synthesizer(soundFontPath, _sampleRate);
            _synth.MasterVolume = 2.0f; // Increase gain for fuller chords
        }

        _waveProvider = new SynthWaveProvider(this, _sampleRate);

        _waveOut = new WaveOutEvent
        {
            DesiredLatency = 40,   // 40ms latency - more responsive
            NumberOfBuffers = 3,
            Volume = _volume
        };
        _waveOut.Init(_waveProvider);
        _waveOut.Play();
    }

    /// <summary>Send a Note-On event to the synthesizer (thread-safe).</summary>
    public void NoteOn(int channel, int note, int velocity)
    {
        lock (_synthLock)
        {
            _synth?.NoteOn(channel, note, velocity);
        }
    }

    /// <summary>Send a Note-Off event to the synthesizer (thread-safe).</summary>
    public void NoteOff(int channel, int note)
    {
        lock (_synthLock)
        {
            _synth?.NoteOff(channel, note);
        }
    }

    /// <summary>Process a raw MIDI message (thread-safe).</summary>
    public void ProcessMidiMessage(int channel, int command, int data1, int data2)
    {
        lock (_synthLock)
        {
            _synth?.ProcessMidiMessage(channel, command, data1, data2);
        }
    }

    /// <summary>Turn off all notes on all channels (thread-safe).</summary>
    public void AllNotesOff()
    {
        lock (_synthLock)
        {
            if (_synth == null) return;
            for (int ch = 0; ch < 16; ch++)
                _synth.ProcessMidiMessage(ch, 0xB0, 123, 0); // CC 123 = All Notes Off
        }
    }

    /// <summary>Render audio samples into a buffer (called by SynthWaveProvider on audio thread).</summary>
    internal void RenderSamples(float[] left, float[] right, int count)
    {
        lock (_synthLock)
        {
            if (_synth == null) return;
            _synth.Render(left.AsSpan(0, count), right.AsSpan(0, count));
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
            _synth = null;
        }
    }
}

/// <summary>
/// NAudio IWaveProvider that pulls audio samples from a MeltySynth Synthesizer.
/// Uses unsafe pointer operations for fast interleaving.
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
        // Initial buffers (200ms)
        _left = new float[sampleRate / 5];
        _right = new float[sampleRate / 5];
    }

    public WaveFormat WaveFormat => _waveFormat;

    public int Read(byte[] buffer, int offset, int count)
    {
        int samplesNeeded = count / 8; // 2 channels, 4 bytes per float
        if (samplesNeeded <= 0) return 0;

        // Resize buffers if they are too small for this request
        if (_left.Length < samplesNeeded)
        {
            _left = new float[samplesNeeded];
            _right = new float[samplesNeeded];
        }

        // Render samples from the synth
        _engine.RenderSamples(_left, _right, samplesNeeded);

        // Interleave into the output buffer
        fixed (float* pL = _left, pR = _right)
        fixed (byte* pBuf = buffer)
        {
            float* dst = (float*)(pBuf + offset);
            float* srcL = pL;
            float* srcR = pR;
            float* end = srcL + samplesNeeded;

            while (srcL < end)
            {
                *dst++ = *srcL++;
                *dst++ = *srcR++;
            }
        }

        return count;
    }
}
