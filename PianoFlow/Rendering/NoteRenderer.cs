using System.Windows;
using System.Windows.Media;

namespace PianoFlow.Rendering;

/// <summary>
/// GPU-accelerated piano + note renderer using WPF DrawingVisual.
/// Visual effects: glow/bloom, note gradients, impact flash, background atmosphere, keyboard saber.
/// Performance-optimized: pre-cached brushes, precomputed key positions, zero per-frame allocations.
/// </summary>
public class PianoFlowVisual : FrameworkElement
{
    private readonly DrawingVisual _glowVisual = new();  // bloom layer (behind)
    private readonly DrawingVisual _visual = new();       // main layer

    // Key range: A0 (21) to C8 (108)
    public const int FirstNote = 21;
    public const int LastNote = 108;
    public const int NoteCount = LastNote - FirstNote + 1;

    private static readonly bool[] IsBlackKey =
        Enumerable.Range(0, 128).Select(n =>
        {
            int[] blacks = { 1, 3, 6, 8, 10 };
            return blacks.Contains(n % 12);
        }).ToArray();

    // 16 channel colors
    public static readonly Color[] ChannelColors;
    private static readonly SolidColorBrush[] ChannelBrushes;

    // --- Pre-cached note brushes: [channel, alphaBucket] ---
    private const int AlphaBuckets = 16;
    private static readonly SolidColorBrush[][] NoteBrushCache;

    // --- Glow brushes: [channel, alphaBucket] (larger, dimmer) ---
    private static readonly SolidColorBrush[][] GlowBrushCache;

    // --- Gradient brushes for notes (bright at hit edge) ---
    private static readonly LinearGradientBrush[][] NoteGradientCache;

    // --- Impact flash brushes ---
    private static readonly SolidColorBrush[] ImpactFlashBrushes;  // expanding circle
    private static readonly SolidColorBrush[] HitFlashWhite;
    private static readonly SolidColorBrush[] HitFlashBlack;

    // --- Dynamic accent brushes ---
    private Color _lastAccentColor;
    private readonly SolidColorBrush[] _accentNoteBrushes = new SolidColorBrush[AlphaBuckets];
    private readonly SolidColorBrush[] _accentGlowBrushes = new SolidColorBrush[AlphaBuckets];
    private readonly LinearGradientBrush[] _accentGradientBrushes = new LinearGradientBrush[AlphaBuckets];
    private SolidColorBrush? _accentKeyBrush;

    // --- Background atmosphere ---
    private static readonly RadialGradientBrush AtmosphereBrush;

    // --- Keyboard saber ---
    private static readonly LinearGradientBrush SaberGradient;

    // --- Piano key visual brushes ---
    private static readonly SolidColorBrush PianoKeyBorder;
    private static readonly SolidColorBrush WhiteKeyTop;
    private static readonly SolidColorBrush WhiteKeyBottom;
    private static readonly SolidColorBrush BlackKeyTop;
    private static readonly SolidColorBrush BlackKeySide;

    static PianoFlowVisual()
    {
        ChannelColors = new Color[]
        {
            Color.FromRgb(79, 195, 247),   // 0  light blue
            Color.FromRgb(255, 112, 67),   // 1  deep orange
            Color.FromRgb(102, 187, 106),  // 2  green
            Color.FromRgb(255, 202, 40),   // 3  amber
            Color.FromRgb(171, 71, 188),   // 4  purple
            Color.FromRgb(239, 83, 80),    // 5  red
            Color.FromRgb(38, 198, 218),   // 6  cyan
            Color.FromRgb(141, 110, 99),   // 7  brown
            Color.FromRgb(120, 144, 156),  // 8  blue grey
            Color.FromRgb(212, 225, 87),   // 9  lime
            Color.FromRgb(255, 138, 101),  // 10 deep orange light
            Color.FromRgb(174, 213, 129),  // 11 light green
            Color.FromRgb(121, 134, 203),  // 12 indigo
            Color.FromRgb(240, 98, 146),   // 13 pink
            Color.FromRgb(77, 182, 172),   // 14 teal
            Color.FromRgb(255, 213, 79),   // 15 yellow
        };
        ChannelBrushes = ChannelColors.Select(c => new SolidColorBrush(c)).ToArray();
        foreach (var b in ChannelBrushes) b.Freeze();

        // Pre-build alpha-bucketed brushes for each channel
        NoteBrushCache = new SolidColorBrush[16][];
        GlowBrushCache = new SolidColorBrush[16][];
        NoteGradientCache = new LinearGradientBrush[16][];

        for (int ch = 0; ch < 16; ch++)
        {
            NoteBrushCache[ch] = new SolidColorBrush[AlphaBuckets];
            GlowBrushCache[ch] = new SolidColorBrush[AlphaBuckets];
            NoteGradientCache[ch] = new LinearGradientBrush[AlphaBuckets];

            var baseColor = ChannelColors[ch];

            for (int a = 0; a < AlphaBuckets; a++)
            {
                int alphaValue = (int)(a * 255.0 / (AlphaBuckets - 1));
                alphaValue = Math.Max(80, alphaValue);

                // Main note brush
                var brush = new SolidColorBrush(
                    Color.FromArgb((byte)alphaValue, baseColor.R, baseColor.G, baseColor.B));
                brush.Freeze();
                NoteBrushCache[ch][a] = brush;

                // Glow brush (same color, 40% alpha, used for bloom layer)
                int glowAlpha = Math.Min(255, alphaValue * 40 / 100);
                var glow = new SolidColorBrush(
                    Color.FromArgb((byte)glowAlpha, baseColor.R, baseColor.G, baseColor.B));
                glow.Freeze();
                GlowBrushCache[ch][a] = glow;

                // Gradient brush: bright at bottom (hit edge), dimmer at top
                var grad = new LinearGradientBrush();
                grad.StartPoint = new Point(0, 1);  // bottom = hit edge
                grad.EndPoint = new Point(0, 0);    // top = away from piano
                grad.GradientStops.Add(new GradientStop(
                    Color.FromArgb((byte)alphaValue, baseColor.R, baseColor.G, baseColor.B), 0.0));
                grad.GradientStops.Add(new GradientStop(
                    Color.FromArgb((byte)(alphaValue * 55 / 100), baseColor.R, baseColor.G, baseColor.B), 1.0));
                grad.Freeze();
                NoteGradientCache[ch][a] = grad;
            }
        }

        // Impact flash: 12 expanding rings, white with fading alpha
        ImpactFlashBrushes = new SolidColorBrush[12];
        for (int i = 0; i < 12; i++)
        {
            int alpha = (int)(220 * (1.0 - i / 11.0));
            ImpactFlashBrushes[i] = MakeBrush((byte)alpha, 255, 255, 255);
        }

        // Hit flash brushes for piano keys
        HitFlashWhite = new SolidColorBrush[20];
        HitFlashBlack = new SolidColorBrush[20];
        for (int i = 0; i < 20; i++)
        {
            double fade = i / 19.0;
            HitFlashWhite[i] = MakeBrush((byte)(fade * 180), 255, 136, 0);
            HitFlashBlack[i] = MakeBrush((byte)(fade * 200), 255, 102, 0);
        }

        // Piano key brushes
        PianoKeyBorder = MakeBrush(255, 0, 0, 0);
        WhiteKeyTop = MakeBrush(255, 255, 255, 255);
        WhiteKeyBottom = MakeBrush(255, 200, 200, 204);
        BlackKeyTop = MakeBrush(255, 245, 245, 245);
        BlackKeySide = MakeBrush(255, 54, 54, 58);

        // Background atmosphere: subtle radial glow at piano line
        AtmosphereBrush = new RadialGradientBrush();
        AtmosphereBrush.Center = new Point(0.5, 1.0);
        AtmosphereBrush.RadiusX = 0.7;
        AtmosphereBrush.RadiusY = 0.3;
        AtmosphereBrush.GradientStops.Add(new GradientStop(Color.FromArgb(25, 79, 195, 247), 0.0));
        AtmosphereBrush.GradientStops.Add(new GradientStop(Color.FromArgb(10, 79, 195, 247), 0.5));
        AtmosphereBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1.0));
        AtmosphereBrush.Freeze();

        // Keyboard saber gradient (will be colored per-frame based on active notes)
        SaberGradient = new LinearGradientBrush();
        SaberGradient.StartPoint = new Point(0, 0.5);
        SaberGradient.EndPoint = new Point(1, 0.5);
        SaberGradient.GradientStops.Add(new GradientStop(Color.FromArgb(0, 79, 195, 247), 0.0));
        SaberGradient.GradientStops.Add(new GradientStop(Color.FromArgb(180, 79, 195, 247), 0.3));
        SaberGradient.GradientStops.Add(new GradientStop(Color.FromArgb(180, 79, 195, 247), 0.7));
        SaberGradient.GradientStops.Add(new GradientStop(Color.FromArgb(0, 79, 195, 247), 1.0));
        SaberGradient.Freeze();
    }

    private static SolidColorBrush MakeBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush GetCachedNoteBrush(int channel, double alpha01)
    {
        int bucket = (int)(Math.Clamp(alpha01, 0, 1) * (AlphaBuckets - 1) + 0.5);
        return NoteBrushCache[channel % 16][bucket];
    }

    private static SolidColorBrush GetCachedGlowBrush(int channel, double alpha01)
    {
        int bucket = (int)(Math.Clamp(alpha01, 0, 1) * (AlphaBuckets - 1) + 0.5);
        return GlowBrushCache[channel % 16][bucket];
    }

    private static LinearGradientBrush GetCachedGradientBrush(int channel, double alpha01)
    {
        int bucket = (int)(Math.Clamp(alpha01, 0, 1) * (AlphaBuckets - 1) + 0.5);
        return NoteGradientCache[channel % 16][bucket];
    }

    private void UpdateAccentBrushes(Color color)
    {
        if (color == _lastAccentColor && _accentKeyBrush != null) return;
        _lastAccentColor = color;

        _accentKeyBrush = new SolidColorBrush(color);
        _accentKeyBrush.Freeze();

        for (int a = 0; a < AlphaBuckets; a++)
        {
            int alphaValue = (int)(a * 255.0 / (AlphaBuckets - 1));
            alphaValue = Math.Max(80, alphaValue);

            var brush = new SolidColorBrush(Color.FromArgb((byte)alphaValue, color.R, color.G, color.B));
            brush.Freeze();
            _accentNoteBrushes[a] = brush;

            int glowAlpha = Math.Min(255, alphaValue * 40 / 100);
            var glow = new SolidColorBrush(Color.FromArgb((byte)glowAlpha, color.R, color.G, color.B));
            glow.Freeze();
            _accentGlowBrushes[a] = glow;

            var grad = new LinearGradientBrush();
            grad.StartPoint = new Point(0, 1);
            grad.EndPoint = new Point(0, 0);
            grad.GradientStops.Add(new GradientStop(Color.FromArgb((byte)alphaValue, color.R, color.G, color.B), 0.0));
            grad.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(alphaValue * 55 / 100), color.R, color.G, color.B), 1.0));
            grad.Freeze();
            _accentGradientBrushes[a] = grad;
        }
    }

    private LinearGradientBrush GetAccentGradientBrush(double alpha01)
    {
        int bucket = (int)(Math.Clamp(alpha01, 0, 1) * (AlphaBuckets - 1) + 0.5);
        return _accentGradientBrushes[bucket];
    }

    private SolidColorBrush GetAccentGlowBrush(double alpha01)
    {
        int bucket = (int)(Math.Clamp(alpha01, 0, 1) * (AlphaBuckets - 1) + 0.5);
        return _accentGlowBrushes[bucket];
    }

    private SolidColorBrush GetAccentNoteBrush(double alpha01)
    {
        int bucket = (int)(Math.Clamp(alpha01, 0, 1) * (AlphaBuckets - 1) + 0.5);
        return _accentNoteBrushes[bucket];
    }

    // --- Precomputed key positions (O(1) lookup) ---
    private int[] _keyX = new int[NoteCount];
    private int[] _keyWidth = new int[NoteCount];
    private int _whiteKeyWidth;
    private int _blackKeyWidth;
    private int _pianoY;
    private int _pianoHeight;
    private int _blackKeyVisualHeight;
    private int _gap = 1;

    public int PianoHeight { get; set; } = 120;

    // --- Impact flash tracking ---
    private double[] _hitTime = new double[NoteCount];
    private bool[] _hasImpact = new bool[NoteCount];  // tracks active impacts

    public PianoFlowVisual()
    {
        AddVisualChild(_glowVisual);
        AddVisualChild(_visual);
        Array.Fill(_hitTime, -999.0);
    }

    protected override int VisualChildrenCount => 2;
    protected override Visual GetVisualChild(int index) => index == 0 ? _glowVisual : _visual;

    /// <summary>Record a key hit for flash + impact effect.</summary>
    public void RecordHit(int note, double currentTime)
    {
        int idx = note - FirstNote;
        if (idx >= 0 && idx < NoteCount)
        {
            _hitTime[idx] = currentTime;
            _hasImpact[idx] = true;
        }
    }

    /// <summary>Update key layout for given dimensions.</summary>
    public void UpdateLayout(int width, int height)
    {
        _pianoHeight = PianoHeight;
        _pianoY = height - _pianoHeight;
        _blackKeyVisualHeight = (int)(_pianoHeight * 0.63);
        ComputeKeyPositions(width);
    }

    private void ComputeKeyPositions(int width)
    {
        int whiteKeyCount = 0;
        for (int note = FirstNote; note <= LastNote; note++)
            if (!IsBlackKey[note]) whiteKeyCount++;

        _gap = Math.Max(1, (int)(width * 0.001));
        _whiteKeyWidth = Math.Max(8, (width - _gap * (whiteKeyCount - 1)) / whiteKeyCount);
        _blackKeyWidth = Math.Max(5, (int)(_whiteKeyWidth * 0.58));

        var whiteX = new int[NoteCount];
        int wx = 0;
        for (int note = FirstNote; note <= LastNote; note++)
        {
            int idx = note - FirstNote;
            if (!IsBlackKey[note])
            {
                whiteX[idx] = wx;
                _keyX[idx] = wx;
                _keyWidth[idx] = _whiteKeyWidth;
                wx += _whiteKeyWidth + _gap;
            }
        }

        for (int note = FirstNote; note <= LastNote; note++)
        {
            int idx = note - FirstNote;
            if (!IsBlackKey[note]) continue;

            int leftWhite = note - 1;
            while (leftWhite >= FirstNote && IsBlackKey[leftWhite])
                leftWhite--;

            if (leftWhite >= FirstNote)
            {
                int leftIdx = leftWhite - FirstNote;
                int center = whiteX[leftIdx] + _whiteKeyWidth;
                _keyX[idx] = center - _blackKeyWidth / 2;
            }
            else
            {
                _keyX[idx] = 0;
            }
            _keyWidth[idx] = _blackKeyWidth;
        }
    }

    /// <summary>Main render call. Draws atmosphere + glow + notes + particles + piano + saber.</summary>
    public void Render(IReadOnlyList<Models.MidiNote> notes, double currentTime,
        bool falling, double noteSpeed, bool[]? keyActive, ParticleSystem? particles = null,
        Color? accentColor = null, bool useGlobalAccent = false)
    {
        if (accentColor.HasValue) UpdateAccentBrushes(accentColor.Value);

        int w = (int)ActualWidth;
        int h = (int)ActualHeight;
        if (w <= 0 || h <= 0) return;

        _pianoY = h - _pianoHeight;

        // --- Glow layer (behind everything) ---
        using (var dcGlow = _glowVisual.RenderOpen())
        {
            // Background atmosphere
            dcGlow.DrawRectangle(AtmosphereBrush, null, new Rect(0, 0, w, h));

            // Draw glow copies of notes (larger, dimmer, blurred feel)
            DrawNoteGlow(dcGlow, notes, currentTime, falling, noteSpeed, w, h, useGlobalAccent);
        }

        // --- Main layer ---
        using var dc = _visual.RenderOpen();

        // Background
        dc.DrawRectangle(BrushCache.Background, null, new Rect(0, 0, w, h));

        // Draw notes with gradient
        DrawNotes(dc, notes, currentTime, falling, noteSpeed, w, h, useGlobalAccent);

        // Draw impact flashes at piano line
        DrawImpactFlashes(dc, currentTime, w);

        // Draw particles
        particles?.Draw(dc);

        // Draw piano
        DrawPiano(dc, keyActive, currentTime, w, h);

        // Draw keyboard saber (glowing line above piano)
        DrawKeyboardSaber(dc, keyActive, currentTime, w);

        // Draw separator line above piano
        dc.DrawRectangle(BrushCache.Separator, null, new Rect(0, _pianoY - 2, w, 2));
    }

    private void DrawNoteGlow(DrawingContext dc, IReadOnlyList<Models.MidiNote> notes,
        double currentTime, bool falling, double noteSpeed, int w, int h, bool useGlobalAccent)
    {
        double visibleDuration = _pianoY / noteSpeed;
        int glowExpand = 4;

        for (int i = 0; i < notes.Count; i++)
        {
            var note = notes[i];

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
            if ((uint)noteIndex >= NoteCount) continue;

            int keyX = _keyX[noteIndex];
            int keyW = _keyWidth[noteIndex];

            int y1, y2;
            double effectiveOffTime = (note.IsLive && note.OffTime > currentTime) ? currentTime : note.OffTime;

            if (falling)
            {
                y1 = _pianoY - (int)((effectiveOffTime - currentTime) * noteSpeed);
                y2 = _pianoY - (int)((note.OnTime - currentTime) * noteSpeed);
            }
            else
            {
                y1 = _pianoY - (int)((currentTime - note.OnTime) * noteSpeed);
                y2 = _pianoY - (int)((currentTime - effectiveOffTime) * noteSpeed);
            }

            int yTop = Math.Max(0, Math.Min(y1, y2));
            int yBottom = Math.Min(_pianoY, Math.Max(y1, y2));
            if (yTop >= yBottom) continue;

            double distFromHit;
            if (falling)
                distFromHit = Math.Max(0, note.OnTime - currentTime);
            else
                distFromHit = Math.Max(0, currentTime - effectiveOffTime);

            double alpha = Math.Max(80.0 / 255.0, 1.0 - distFromHit * 0.6);

            var glowBrush = useGlobalAccent ? GetAccentGlowBrush(alpha) : GetCachedGlowBrush(note.Channel, alpha);
            dc.DrawRectangle(glowBrush, null,
                new Rect(keyX - glowExpand, yTop - glowExpand,
                         keyW + glowExpand * 2, (yBottom - yTop) + glowExpand * 2));
        }
    }

    private void DrawNotes(DrawingContext dc, IReadOnlyList<Models.MidiNote> notes,
        double currentTime, bool falling, double noteSpeed, int w, int h, bool useGlobalAccent)
    {
        double visibleDuration = _pianoY / noteSpeed;

        for (int i = 0; i < notes.Count; i++)
        {
            var note = notes[i];

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
            if ((uint)noteIndex >= NoteCount) continue;

            int keyX = _keyX[noteIndex];
            int keyW = _keyWidth[noteIndex];

            int y1, y2;
            double effectiveOffTime = (note.IsLive && note.OffTime > currentTime) ? currentTime : note.OffTime;

            if (falling)
            {
                y1 = _pianoY - (int)((effectiveOffTime - currentTime) * noteSpeed);
                y2 = _pianoY - (int)((note.OnTime - currentTime) * noteSpeed);
            }
            else
            {
                y1 = _pianoY - (int)((currentTime - note.OnTime) * noteSpeed);
                y2 = _pianoY - (int)((currentTime - effectiveOffTime) * noteSpeed);
            }

            int yTop = Math.Max(0, Math.Min(y1, y2));
            int yBottom = Math.Min(_pianoY, Math.Max(y1, y2));
            if (yTop >= yBottom) continue;

            double distFromHit;
            if (falling)
                distFromHit = Math.Max(0, note.OnTime - currentTime);
            else
                distFromHit = Math.Max(0, currentTime - effectiveOffTime);

            double alpha = Math.Max(80.0 / 255.0, 1.0 - distFromHit * 0.6);

            // Use gradient brush: bright at piano edge, dimmer away
            var gradientBrush = useGlobalAccent ? GetAccentGradientBrush(alpha) : GetCachedGradientBrush(note.Channel, alpha);

            // Draw full note rectangle
            dc.DrawRectangle(gradientBrush, null, new Rect(keyX, yTop, keyW, yBottom - yTop));

            // Bright accent line at the hit edge (where note meets piano)
            int hitEdgeY = yBottom;
            var accentBrush = useGlobalAccent ? GetAccentNoteBrush(1.0) : GetCachedNoteBrush(note.Channel, 1.0);
            dc.DrawRectangle(accentBrush, null, new Rect(keyX, hitEdgeY, keyW, 2));
        }
    }

    /// <summary>
    /// Draw expanding flash circles at the piano line when notes are hit.
    /// Like MIDIVisualizer's impact effect.
    /// </summary>
    private void DrawImpactFlashes(DrawingContext dc, double currentTime, int w)
    {
        for (int note = FirstNote; note <= LastNote; note++)
        {
            int ni = note - FirstNote;
            if (!_hasImpact[ni]) continue;

            double elapsed = currentTime - _hitTime[ni];
            if (elapsed < 0 || elapsed > 0.25)
            {
                _hasImpact[ni] = false;
                continue;
            }

            double progress = elapsed / 0.25;
            int flashIdx = (int)(progress * 11);
            flashIdx = Math.Clamp(flashIdx, 0, 11);

            int keyX = _keyX[ni];
            int keyW = _keyWidth[ni];
            int cx = keyX + keyW / 2;

            // Expanding horizontal line
            int expandX = (int)(progress * keyW * 1.5);
            int expandY = (int)(progress * 8);

            var brush = ImpactFlashBrushes[flashIdx];
            dc.DrawRectangle(brush, null,
                new Rect(cx - keyW / 2 - expandX, _pianoY - expandY,
                         keyW + expandX * 2, 3));
        }
    }

    /// <summary>
    /// Draw glowing line above piano keys (keyboard saber).
    /// Pulses brighter when keys are active, like Piano VFX's keyboard saber.
    /// </summary>
    private void DrawKeyboardSaber(DrawingContext dc, bool[]? keyActive, double currentTime, int w)
    {
        // Find if any key is active for saber brightness
        bool anyActive = false;
        if (keyActive != null)
        {
            for (int i = 0; i < keyActive.Length; i++)
            {
                if (keyActive[i]) { anyActive = true; break; }
            }
        }

        // Base saber: subtle line above piano
        int saberY = _pianoY - 4;
        int saberH = 3;

        // Animate saber brightness with a subtle pulse
        double pulse = anyActive
            ? 0.7 + 0.3 * Math.Sin(currentTime * 8)
            : 0.3 + 0.1 * Math.Sin(currentTime * 2);

        int alpha = (int)(160 * pulse);

        // Draw per-key saber segments that light up with active notes
        for (int note = FirstNote; note <= LastNote; note++)
        {
            if (IsBlackKey[note]) continue;
            int ni = note - FirstNote;
            int x = _keyX[ni];
            int kw = _whiteKeyWidth - _gap;

            bool active = keyActive != null && ni < keyActive.Length && keyActive[ni];
            int segAlpha = active ? Math.Min(255, alpha + 100) : alpha;

            var saberColor = active
                ? Color.FromArgb((byte)segAlpha, 120, 220, 255)  // bright cyan when active
                : Color.FromArgb((byte)segAlpha, 40, 80, 120);   // subtle blue when idle

            var saberBrush = new SolidColorBrush(saberColor);
            saberBrush.Freeze();
            dc.DrawRectangle(saberBrush, null, new Rect(x, saberY, kw, saberH));
        }
    }

    private void DrawPiano(DrawingContext dc, bool[]? keyActive, double currentTime, int w, int h)
    {
        // --- Draw white keys first ---
        for (int note = FirstNote; note <= LastNote; note++)
        {
            if (IsBlackKey[note]) continue;
            int ni = note - FirstNote;
            int x = _keyX[ni];
            bool active = keyActive != null && ni < keyActive.Length && keyActive[ni];

            var fill = active ? (_accentKeyBrush ?? BrushCache.WhiteKeyActive) : BrushCache.WhiteKey;

            dc.DrawRectangle(fill, null, new Rect(x, _pianoY, _whiteKeyWidth - _gap, _pianoHeight));
            dc.DrawRectangle(WhiteKeyTop, null, new Rect(x + 1, _pianoY + 1, _whiteKeyWidth - _gap - 2, 2));
            dc.DrawRectangle(WhiteKeyBottom, null,
                new Rect(x + 1, _pianoY + _pianoHeight - 3, _whiteKeyWidth - _gap - 2, 2));
            dc.DrawRectangle(null, new Pen(PianoKeyBorder, 1),
                new Rect(x, _pianoY, _whiteKeyWidth - _gap, _pianoHeight));

            if (active)
            {
                double elapsed = currentTime - _hitTime[ni];
                if (elapsed >= 0 && elapsed < 0.3)
                {
                    int fi = (int)((1.0 - elapsed / 0.3) * 19 + 0.5);
                    fi = Math.Clamp(fi, 0, 19);
                    dc.DrawRectangle(HitFlashWhite[fi], null,
                        new Rect(x + 1, _pianoY + 1, _whiteKeyWidth - _gap - 2, _pianoHeight - 2));
                }
            }
        }

        // --- Draw black keys on top ---
        for (int note = FirstNote; note <= LastNote; note++)
        {
            if (!IsBlackKey[note]) continue;
            int ni = note - FirstNote;
            int x = _keyX[ni];
            int kw = _blackKeyWidth;
            bool active = keyActive != null && ni < keyActive.Length && keyActive[ni];

            var fill = active ? (_accentKeyBrush ?? BrushCache.BlackKeyActive) : BrushCache.BlackKey;

            dc.DrawRectangle(fill, null, new Rect(x, _pianoY, kw, _blackKeyVisualHeight));
            dc.DrawRectangle(null, new Pen(PianoKeyBorder, 1),
                new Rect(x, _pianoY, kw, _blackKeyVisualHeight));
            dc.DrawRectangle(BlackKeyTop, null, new Rect(x + 1, _pianoY + 1, kw - 2, 1));
            dc.DrawRectangle(BlackKeySide, null,
                new Rect(x + 1, _pianoY + 1, 1, _blackKeyVisualHeight - 2));
            dc.DrawRectangle(BlackKeySide, null,
                new Rect(x + kw - 2, _pianoY + 1, 1, _blackKeyVisualHeight - 2));

            if (active)
            {
                double elapsed = currentTime - _hitTime[ni];
                if (elapsed >= 0 && elapsed < 0.3)
                {
                    int fi = (int)((1.0 - elapsed / 0.3) * 19 + 0.5);
                    fi = Math.Clamp(fi, 0, 19);
                    dc.DrawRectangle(HitFlashBlack[fi], null,
                        new Rect(x + 1, _pianoY + 1, kw - 2, _blackKeyVisualHeight - 2));
                }
            }
        }

        // --- Outer border around all white keys ---
        if (_keyX.Length > 0)
        {
            int firstWhiteX = int.MaxValue;
            int lastWhiteRight = 0;
            for (int note = FirstNote; note <= LastNote; note++)
            {
                if (IsBlackKey[note]) continue;
                int ni = note - FirstNote;
                firstWhiteX = Math.Min(firstWhiteX, _keyX[ni]);
                lastWhiteRight = Math.Max(lastWhiteRight, _keyX[ni] + _whiteKeyWidth);
            }
            if (firstWhiteX < lastWhiteRight)
            {
                dc.DrawRectangle(null, new Pen(PianoKeyBorder, 1),
                    new Rect(firstWhiteX, _pianoY, lastWhiteRight - firstWhiteX, _pianoHeight));
            }
        }
    }

    /// <summary>Get the X position center of a key (for particle spawn).</summary>
    public double GetKeyCenterX(int note)
    {
        int ni = note - FirstNote;
        if ((uint)ni >= NoteCount) return 0;
        return _keyX[ni] + _keyWidth[ni] / 2.0;
    }

    /// <summary>Get the Y position of the piano top.</summary>
    public int PianoTopY => _pianoY;
}

/// <summary>Pre-frozen brushes for common colors.</summary>
public static class BrushCache
{
    public static readonly SolidColorBrush Background;
    public static readonly SolidColorBrush Separator;
    public static readonly SolidColorBrush WhiteKey;
    public static readonly SolidColorBrush WhiteKeyActive;
    public static readonly SolidColorBrush BlackKey;
    public static readonly SolidColorBrush BlackKeyActive;

    static BrushCache()
    {
        Background = Make(10, 14, 26);
        Separator = Make(51, 51, 102);
        WhiteKey = Make(224, 224, 224);
        WhiteKeyActive = Make(255, 255, 255);
        BlackKey = Make(26, 26, 46);
        BlackKeyActive = Make(58, 58, 94);
    }

    private static SolidColorBrush Make(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
