using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PianoFlow.Rendering;

/// <summary>
/// Draws falling or rising note blocks on a WriteableBitmap.
/// Notes are colored by MIDI channel (16 distinct colors).
/// </summary>
public class NoteRenderer
{
    private WriteableBitmap? _bitmap;
    private int _width;
    private int _height;

    // Piano key layout cache
    private int[] _whiteKeyX = Array.Empty<int>();
    private int _whiteKeyWidth;
    private int _blackKeyWidth;
    private int _pianoY;
    private int _pianoHeight;

    // 16 distinct channel colors (ARGB)
    private static readonly uint[] ChannelColors =
    {
        0xFF4FC3F7, // 0  - light blue
        0xFFFF7043, // 1  - deep orange
        0xFF66BB6A, // 2  - green
        0xFFFFCA28, // 3  - amber
        0xFFAB47BC, // 4  - purple
        0xFFEF5350, // 5  - red
        0xFF26C6DA, // 6  - cyan
        0xFF8D6E63, // 7  - brown
        0xFF78909C, // 8  - blue grey
        0xFFD4E157, // 9  - lime
        0xFFFF8A65, // 10 - deep orange light
        0xFFAED581, // 11 - light green
        0xFF7986CB, // 12 - indigo
        0xFFF06292, // 13 - pink
        0xFF4DB6AC, // 14 - teal
        0xFFFFD54F, // 15 - yellow
    };

    // Key range: A0 (21) to C8 (108)
    public const int FirstNote = 21;
    public const int LastNote = 108;
    public const int NoteCount = LastNote - FirstNote + 1;

    // Black key indices relative to octave (C=0, C#=1, D=2, ...)
    private static readonly bool[] IsBlackKey =
    {
        false, true, false, true, false, false, true, false, true, false, true, false
    };

    public void UpdateLayout(int width, int height, int pianoHeight)
    {
        _width = width;
        _height = height;
        _pianoHeight = pianoHeight;
        _pianoY = height - pianoHeight;

        _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);

        ComputeKeyPositions();
    }

    private void ComputeKeyPositions()
    {
        // Count white keys in range
        int whiteKeyCount = 0;
        for (int note = FirstNote; note <= LastNote; note++)
        {
            if (!IsBlackKey[note % 12]) whiteKeyCount++;
        }

        _whiteKeyWidth = Math.Max(1, _width / whiteKeyCount);
        _blackKeyWidth = Math.Max(1, (int)(_whiteKeyWidth * 0.6));

        // Compute X position for each white key
        var whiteKeyXList = new List<int>();
        int whiteIdx = 0;
        for (int note = FirstNote; note <= LastNote; note++)
        {
            if (!IsBlackKey[note % 12])
            {
                whiteKeyXList.Add(whiteIdx * _whiteKeyWidth);
                whiteIdx++;
            }
        }
        _whiteKeyX = whiteKeyXList.ToArray();
    }

    /// <summary>Render a frame: background + notes + piano.</summary>
    public unsafe void Render(IReadOnlyList<Models.MidiNote> notes,
        double currentTime, bool falling, double noteSpeed,
        HashSet<int>? activeNotes, bool[]? keyActive, double[]? keyFadeTime)
    {
        if (_bitmap == null) return;

        _bitmap.Lock();
        var buffer = (uint*)_bitmap.BackBuffer;
        int stride = _bitmap.BackBufferStride / 4;

        // Clear to dark background
        ClearBuffer(buffer, stride, 0xFF0A0E1A);

        // Draw note blocks
        DrawNotes(buffer, stride, notes, currentTime, falling, noteSpeed);

        // Draw piano keyboard
        DrawPiano(buffer, stride, keyActive, keyFadeTime, currentTime);

        _bitmap.AddDirtyRect(new Int32Rect(0, 0, _width, _height));
        _bitmap.Unlock();
    }

    public WriteableBitmap? Bitmap => _bitmap;

    private unsafe void ClearBuffer(uint* buffer, int stride, uint color)
    {
        for (int y = 0; y < _height; y++)
        {
            for (int x = 0; x < _width; x++)
            {
                buffer[y * stride + x] = color;
            }
        }
    }

    private unsafe void DrawNotes(uint* buffer, int stride,
        IReadOnlyList<Models.MidiNote> notes, double currentTime,
        bool falling, double noteSpeed)
    {
        double visibleDuration = _pianoY / noteSpeed;

        foreach (var note in notes)
        {
            // Skip notes not in visible range
            if (falling)
            {
                if (note.OffTime < currentTime - 0.1 || note.OnTime > currentTime + visibleDuration)
                    continue;
            }
            else
            {
                if (note.OnTime > currentTime + 0.1 || note.OffTime < currentTime - visibleDuration)
                    continue;
            }

            int noteIndex = note.Note - FirstNote;
            if (noteIndex < 0 || noteIndex >= NoteCount) continue;

            // Get key X position and width
            int keyX = GetKeyX(noteIndex);
            int keyW = IsBlackKey[note.Note % 12] ? _blackKeyWidth : _whiteKeyWidth;

            // Compute Y positions
            int yTop, yBottom;
            if (falling)
            {
                yTop = _pianoY - (int)((note.OffTime - currentTime) * noteSpeed);
                yBottom = _pianoY - (int)((note.OnTime - currentTime) * noteSpeed);
            }
            else
            {
                yTop = _pianoY - (int)((note.OnTime - currentTime) * noteSpeed);
                yBottom = _pianoY - (int)((note.OffTime - currentTime) * noteSpeed);
            }

            // Clamp to visible area
            yTop = Math.Max(0, yTop);
            yBottom = Math.Min(_pianoY, yBottom);
            if (yTop >= yBottom) continue;

            uint color = ChannelColors[note.Channel % 16];

            // Add alpha for notes further from current time (fade effect)
            double distFromHit = Math.Abs(
                (falling ? note.OnTime : note.OffTime) - currentTime);
            byte alpha = (byte)Math.Max(80, 255 - (int)(distFromHit * 100));
            uint fadedColor = (color & 0x00FFFFFF) | ((uint)alpha << 24);

            // Draw rectangle
            FillRect(buffer, stride, keyX, yTop, keyW, yBottom - yTop, fadedColor);
        }
    }

    private unsafe void DrawPiano(uint* buffer, int stride,
        bool[]? keyActive, double[]? keyFadeTime, double currentTime)
    {
        // Draw white keys first
        int whiteIdx = 0;
        for (int note = FirstNote; note <= LastNote; note++)
        {
            if (IsBlackKey[note % 12]) continue;

            int x = whiteIdx * _whiteKeyWidth;
            int noteIndex = note - FirstNote;

            // Key background
            uint keyColor = 0xFFE0E0E0; // white key
            bool active = keyActive != null && noteIndex < keyActive.Length && keyActive[noteIndex];

            if (active)
            {
                keyColor = 0xFFFFFFFF; // bright white when active
            }

            FillRect(buffer, stride, x, _pianoY, _whiteKeyWidth - 1, _pianoHeight, keyColor);

            // Hit flash
            if (active && keyFadeTime != null && noteIndex < keyFadeTime.Length)
            {
                double fade = 1.0 - Math.Min(1.0, (currentTime - keyFadeTime[noteIndex]) * 5);
                if (fade > 0)
                {
                    uint flashColor = (uint)((uint)(fade * 180) << 24) | 0x00FF8800;
                    FillRect(buffer, stride, x, _pianoY, _whiteKeyWidth - 1, _pianoHeight, flashColor);
                }
            }

            whiteIdx++;
        }

        // Draw black keys on top
        whiteIdx = 0;
        for (int note = FirstNote; note <= LastNote; note++)
        {
            if (!IsBlackKey[note % 12])
            {
                whiteIdx++;
                continue;
            }

            // Black key is positioned between the adjacent white keys
            int x = whiteIdx * _whiteKeyWidth - _blackKeyWidth / 2;
            int noteIndex = note - FirstNote;

            uint keyColor = 0xFF1A1A2E; // dark black key
            bool active = keyActive != null && noteIndex < keyActive.Length && keyActive[noteIndex];

            if (active)
            {
                keyColor = 0xFF3A3A5E; // lighter when active
            }

            FillRect(buffer, stride, x, _pianoY, _blackKeyWidth, (int)(_pianoHeight * 0.65), keyColor);

            // Hit flash
            if (active && keyFadeTime != null && noteIndex < keyFadeTime.Length)
            {
                double fade = 1.0 - Math.Min(1.0, (currentTime - keyFadeTime[noteIndex]) * 5);
                if (fade > 0)
                {
                    uint flashColor = (uint)((uint)(fade * 200) << 24) | 0x00FF6600;
                    FillRect(buffer, stride, x, _pianoY, _blackKeyWidth, (int)(_pianoHeight * 0.65), flashColor);
                }
            }
        }

        // Separator line between notes and piano
        FillRect(buffer, stride, 0, _pianoY - 2, _width, 2, 0xFF333366);
    }

    private int GetKeyX(int noteIndex)
    {
        int note = noteIndex + FirstNote;
        if (IsBlackKey[note % 12])
        {
            // Find the white key before this black key
            int whiteBefore = 0;
            for (int n = FirstNote; n < note; n++)
            {
                if (!IsBlackKey[n % 12]) whiteBefore++;
            }
            if (whiteBefore > 0 && whiteBefore < _whiteKeyX.Length)
                return _whiteKeyX[whiteBefore] - _blackKeyWidth / 2;
            return 0;
        }
        else
        {
            int whiteIdx = 0;
            for (int n = FirstNote; n < note; n++)
            {
                if (!IsBlackKey[n % 12]) whiteIdx++;
            }
            return whiteIdx < _whiteKeyX.Length ? _whiteKeyX[whiteIdx] : 0;
        }
    }

    private unsafe void FillRect(uint* buffer, int stride, int x, int y, int w, int h, uint color)
    {
        int x2 = Math.Min(x + w, _width);
        int y2 = Math.Min(y + h, _height);
        x = Math.Max(0, x);
        y = Math.Max(0, y);

        byte a = (byte)((color >> 24) & 0xFF);
        if (a == 0) return;

        for (int py = y; py < y2; py++)
        {
            for (int px = x; px < x2; px++)
            {
                if (a == 255)
                {
                    buffer[py * stride + px] = color;
                }
                else
                {
                    // Alpha blend
                    uint dst = buffer[py * stride + px];
                    uint result = AlphaBlend(color, dst, a);
                    buffer[py * stride + px] = result;
                }
            }
        }
    }

    private static uint AlphaBlend(uint src, uint dst, byte alpha)
    {
        float a = alpha / 255f;
        float invA = 1f - a;

        byte sr = (byte)((src >> 16) & 0xFF);
        byte sg = (byte)((src >> 8) & 0xFF);
        byte sb = (byte)(src & 0xFF);

        byte dr = (byte)((dst >> 16) & 0xFF);
        byte dg = (byte)((dst >> 8) & 0xFF);
        byte db = (byte)(dst & 0xFF);

        byte r = (byte)(sr * a + dr * invA);
        byte g = (byte)(sg * a + dg * invA);
        byte b = (byte)(sb * a + db * invA);

        return (uint)(0xFF000000 | ((uint)r << 16) | ((uint)g << 8) | b);
    }
}
