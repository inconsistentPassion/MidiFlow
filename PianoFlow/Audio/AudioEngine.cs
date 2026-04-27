using MeltySynth;
using NAudio.Wave;

namespace PianoFlow.Audio;

/// <summary>
/// Audio synthesis engine using MeltySynth (SoundFont-based).
/// Renders MIDI notes to audio via a software synthesizer and outputs through NAudio.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private Synthesizer? _synth;
    private WaveOutEvent? _waveOut;
    private SynthWaveProvider? _waveProvider;
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
    /// Call this once at startup or when changing sound fonts.
    /// </summary>
    public void Initialize(string soundFontPath, int sampleRate = 44100)
    {
        Dispose();

        _sampleRate = sampleRate;
        _soundFontPath = soundFontPath;

        _synth = new Synthesizer(soundFontPath, _sampleRate);

        _waveProvider = new SynthWaveProvider(_synth, _sampleRate);

        _waveOut = new WaveOutEvent
        {
            DesiredLatency = 50,  // ~50ms latency - good balance
            NumberOfBuffers = 3,
            Volume = _volume
        };
        _waveOut.Init(_waveProvider);
        _waveOut.Play();
    }

    /// <summary>Send a Note-On event to the synthesizer.</summary>
    public void NoteOn(int channel, int note, int velocity)
    {
        _synth?.NoteOn(channel, note, velocity);
    }

    /// <summary>Send a Note-Off event to the synthesizer.</summary>
    public void NoteOff(int channel, int note)
    {
        _synth?.NoteOff(channel, note);
    }

    /// <summary>Turn off all notes on all channels (CC 123).</summary>
    public void AllNotesOff()
    {
        if (_synth == null) return;
        for (int ch = 0; ch < 16; ch++)
            _synth.ProcessMidiMessage(ch, 0xB0, 123, 0);
    }

    /// <summary>Send a program change (instrument) on a channel.</summary>
    public void ProgramChange(int channel, int program)
    {
        _synth?.ProcessMidiMessage(channel, 0xC0, program, 0);
    }

    /// <summary>Send a control change (e.g. sustain pedal, volume).</summary>
    public void ControlChange(int channel, int controller, int value)
    {
        _synth?.ProcessMidiMessage(channel, 0xB0, controller, value);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;

        _waveProvider = null;
        _synth = null;
    }
}

/// <summary>
/// NAudio IWaveProvider that pulls audio samples from a MeltySynth Synthesizer.
/// MeltySynth renders to separate L/R buffers; we interleave for NAudio's IEEE float format.
/// </summary>
internal sealed class SynthWaveProvider : IWaveProvider
{
    private readonly Synthesizer _synth;
    private readonly WaveFormat _waveFormat;
    private readonly int _bufferSamples;
    private readonly float[] _left;
    private readonly float[] _right;
    private const float Gain = 2.0f;  // boost MeltySynth output

    public SynthWaveProvider(Synthesizer synth, int sampleRate)
    {
        _synth = synth;
        _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
        _bufferSamples = sampleRate / 60;  // ~16ms chunks
        _left = new float[_bufferSamples];
        _right = new float[_bufferSamples];
    }

    public WaveFormat WaveFormat => _waveFormat;

    public int Read(byte[] buffer, int offset, int count)
    {
        int samplesNeeded = count / (sizeof(float) * 2);  // stereo float
        int samplesToRender = Math.Min(samplesNeeded, _bufferSamples);

        // Render to separate L/R buffers
        _synth.Render(_left, _right);

        // Interleave into output buffer with gain boost
        int writePos = offset;
        for (int i = 0; i < samplesToRender; i++)
        {
            float l = Math.Clamp(_left[i] * Gain, -1f, 1f);
            float r = Math.Clamp(_right[i] * Gain, -1f, 1f);
            Buffer.BlockCopy(BitConverter.GetBytes(l), 0, buffer, writePos, sizeof(float));
            writePos += sizeof(float);
            Buffer.BlockCopy(BitConverter.GetBytes(r), 0, buffer, writePos, sizeof(float));
            writePos += sizeof(float);
        }

        // Zero-fill if we rendered fewer samples than requested
        int bytesWritten = writePos - offset;
        if (bytesWritten < count)
            Array.Clear(buffer, offset + bytesWritten, count - bytesWritten);

        return count;
    }
}
