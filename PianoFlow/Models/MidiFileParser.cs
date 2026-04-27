using System.Text;
using System.IO;

namespace PianoFlow.Models;

/// <summary>Parsed MIDI file data.</summary>
public class MidiFileData
{
    public int Format;
    public int TrackCount;
    public int TicksPerBeat;
    public double TotalSeconds;
    public List<MidiNote> Notes = new();
    public List<TempoEvent> TempoEvents = new();
    public List<string> TrackNames = new();
}

public struct TempoEvent
{
    public long Tick;
    public double Seconds;
    public int MicrosecondsPerBeat;
}

/// <summary>
/// Pure C# MIDI file parser. Handles Format 0 and Format 1 files.
/// Parses MThd/MTrk chunks, variable-length delta times, tempo changes.
/// </summary>
public static class MidiFileParser
{

    /// <summary>Parse a MIDI file from disk.</summary>
    public static MidiFileData Parse(string path)
    {
        using var fs = File.OpenRead(path);
        using var reader = new BinaryReader(fs);
        return Parse(reader);
    }

    /// <summary>Parse a MIDI file from a stream.</summary>
    public static MidiFileData Parse(BinaryReader reader)
    {
        var data = new MidiFileData();

        // --- Read MThd header ---
        var headerChunk = ReadChunkHeader(reader);
        if (headerChunk.Id != "MThd")
            throw new InvalidDataException("Not a valid MIDI file: missing MThd header.");

        data.Format = reader.ReadInt16BigEndian();
        data.TrackCount = reader.ReadInt16BigEndian();
        data.TicksPerBeat = reader.ReadInt16BigEndian();

        // Skip any extra header bytes
        if (headerChunk.Length > 6)
            reader.BaseStream.Seek(headerChunk.Length - 6, SeekOrigin.Current);

        // --- Read tracks ---
        var tempoMap = new List<TempoEvent>();

        for (int trackIndex = 0; trackIndex < data.TrackCount; trackIndex++)
        {
            var trackChunk = ReadChunkHeader(reader);
            if (trackChunk.Id != "MTrk")
            {
                // Skip unknown chunks
                reader.BaseStream.Seek(trackChunk.Length, SeekOrigin.Current);
                continue;
            }

            var trackEnd = reader.BaseStream.Position + trackChunk.Length;
            var trackNotes = ParseTrack(reader, trackEnd, trackIndex, data, tempoMap);

            // Build tempo timeline and convert ticks to seconds
            if (trackIndex == 0 && tempoMap.Count > 0)
            {
                data.TempoEvents.AddRange(tempoMap);
            }

            data.Notes.AddRange(trackNotes);
        }

        // Convert all note times from ticks to seconds
        ConvertNoteTimesToSeconds(data, tempoMap);

        // Compute total duration from last note off
        if (data.Notes.Count > 0)
            data.TotalSeconds = data.Notes.Max(n => n.OffTime);

        // Sort by OnTime
        data.Notes.Sort((a, b) => a.OnTime.CompareTo(b.OnTime));

        return data;
    }

    private static List<MidiNote> ParseTrack(BinaryReader reader, long trackEnd,
        int trackIndex, MidiFileData data, List<TempoEvent> tempoMap)
    {
        var notes = new List<MidiNote>();
        // Active notes: key = (channel, noteNumber)
        var activeNotes = new Dictionary<(int ch, int note), (int velocity, long tick)>();
        long currentTick = 0;
        byte runningStatus = 0;

        while (reader.BaseStream.Position < trackEnd)
        {
            // Read variable-length delta time
            long delta = ReadVariableLength(reader);
            currentTick += delta;

            // Read event byte
            byte statusByte = reader.ReadByte();

            if (statusByte == 0xFF)
            {
                // Meta event
                byte metaType = reader.ReadByte();
                int length = (int)ReadVariableLength(reader);
                byte[] data2 = reader.ReadBytes(length);

                if (metaType == 0x51) // Tempo
                {
                    int usPerBeat = (data2[0] << 16) | (data2[1] << 8) | data2[2];
                    tempoMap.Add(new TempoEvent
                    {
                        Tick = currentTick,
                        MicrosecondsPerBeat = usPerBeat,
                        Seconds = 0 // computed later
                    });
                }
                else if (metaType == 0x03 || metaType == 0x04) // Track name / instrument
                {
                    string name = Encoding.ASCII.GetString(data2);
                    if (trackIndex < data.TrackNames.Count)
                        data.TrackNames[trackIndex] = name;
                    else
                        data.TrackNames.Add(name);
                }
                // Other meta events (0x2F end of track, etc.) - skip
            }
            else if (statusByte == 0xF0 || statusByte == 0xF7)
            {
                // SysEx - read until 0xF7
                int length = (int)ReadVariableLength(reader);
                reader.ReadBytes(length);
            }
            else
            {
                // MIDI event
                byte eventStatus;
                byte data1;

                if ((statusByte & 0x80) != 0)
                {
                    eventStatus = statusByte;
                    data1 = reader.ReadByte();
                    runningStatus = statusByte;
                }
                else
                {
                    // Running status
                    eventStatus = runningStatus;
                    data1 = statusByte;
                }

                int channel = eventStatus & 0x0F;
                int eventType = eventStatus & 0xF0;

                if (eventType == 0x90) // Note On
                {
                    byte data2 = reader.ReadByte();
                    if (data2 > 0) // velocity > 0 = note on
                    {
                        activeNotes[(channel, data1)] = (data2, currentTick);
                    }
                    else
                    {
                        // velocity 0 = note off
                        FinishNote(notes, activeNotes, channel, data1, currentTick, trackIndex);
                    }
                }
                else if (eventType == 0x80) // Note Off
                {
                    reader.ReadByte(); // velocity (ignored)
                    FinishNote(notes, activeNotes, channel, data1, currentTick, trackIndex);
                }
                else if (eventType == 0xA0 || // Polyphonic aftertouch
                         eventType == 0xB0 || // Control change
                         eventType == 0xE0)   // Pitch bend
                {
                    reader.ReadByte(); // 2-byte data
                }
                else if (eventType == 0xC0 || // Program change
                         eventType == 0xD0)    // Channel aftertouch
                {
                    // 1-byte data, already read data1
                }
            }
        }

        return notes;
    }

    private static void FinishNote(List<MidiNote> notes,
        Dictionary<(int ch, int note), (int velocity, long tick)> active,
        int channel, int noteNum, long offTick, int trackIndex)
    {
        var key = (channel, noteNum);
        if (active.TryGetValue(key, out var info))
        {
            notes.Add(new MidiNote
            {
                Note = noteNum,
                Velocity = info.velocity,
                Channel = channel,
                OnTime = info.tick, // will be converted to seconds later
                OffTime = offTick,
                Track = trackIndex
            });
            active.Remove(key);
        }
    }

    private static void ConvertNoteTimesToSeconds(MidiFileData data, List<TempoEvent> tempoMap)
    {
        if (tempoMap.Count == 0)
        {
            // Default tempo: 120 BPM = 500000 us/beat
            tempoMap.Add(new TempoEvent { Tick = 0, MicrosecondsPerBeat = 500000, Seconds = 0 });
        }

        // Sort tempo map by tick
        tempoMap.Sort((a, b) => a.Tick.CompareTo(b.Tick));

        // Compute seconds for each tempo event
        double cumulativeSeconds = 0;
        long prevTick = 0;
        double usPerTick = tempoMap[0].MicrosecondsPerBeat / (double)data.TicksPerBeat;

        for (int i = 0; i < tempoMap.Count; i++)
        {
            if (i > 0)
            {
                long tickDelta = tempoMap[i].Tick - prevTick;
                cumulativeSeconds += tickDelta * usPerTick / 1_000_000.0;
                usPerTick = tempoMap[i].MicrosecondsPerBeat / (double)data.TicksPerBeat;
            }
            var evt = tempoMap[i];
            evt.Seconds = cumulativeSeconds;
            tempoMap[i] = evt;
            prevTick = tempoMap[i].Tick;
        }

        // Convert each note's tick times to seconds
        for (int i = 0; i < data.Notes.Count; i++)
        {
            var note = data.Notes[i];
            note.OnTime = TickToSeconds((long)note.OnTime, data.TicksPerBeat, tempoMap);
            note.OffTime = TickToSeconds((long)note.OffTime, data.TicksPerBeat, tempoMap);
            data.Notes[i] = note;
        }
    }

    private static double TickToSeconds(long tick, int ticksPerBeat, List<TempoEvent> tempoMap)
    {
        // Find the tempo event at or before this tick
        int idx = 0;
        for (int i = tempoMap.Count - 1; i >= 0; i--)
        {
            if (tempoMap[i].Tick <= tick)
            {
                idx = i;
                break;
            }
        }

        double usPerTick = tempoMap[idx].MicrosecondsPerBeat / (double)ticksPerBeat;
        long deltaTicks = tick - tempoMap[idx].Tick;
        return tempoMap[idx].Seconds + deltaTicks * usPerTick / 1_000_000.0;
    }

    private static (string Id, int Length) ReadChunkHeader(BinaryReader reader)
    {
        byte[] idBytes = reader.ReadBytes(4);
        string id = Encoding.ASCII.GetString(idBytes);
        int length = reader.ReadInt32BigEndian();
        return (id, length);
    }

    private static long ReadVariableLength(BinaryReader reader)
    {
        long value = 0;
        byte b;
        do
        {
            b = reader.ReadByte();
            value = (value << 7) | (uint)(b & 0x7F);
        } while ((b & 0x80) != 0);
        return value;
    }
}

/// <summary>Extension methods for BinaryReader to read big-endian values.</summary>
public static class BinaryReaderExtensions
{
    public static short ReadInt16BigEndian(this BinaryReader reader)
    {
        byte[] bytes = reader.ReadBytes(2);
        return (short)((bytes[0] << 8) | bytes[1]);
    }

    public static int ReadInt32BigEndian(this BinaryReader reader)
    {
        byte[] bytes = reader.ReadBytes(4);
        return (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
    }
}
