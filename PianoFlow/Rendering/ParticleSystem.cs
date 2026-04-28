using System.Windows;
using System.Windows.Media;

namespace PianoFlow.Rendering;

/// <summary>
/// GPU-accelerated particle system with dramatic impact effects.
/// Features: upward spray, varied sizes, glow particles, color fading.
/// </summary>
public class ParticleSystem
{
    private readonly List<Particle> _particles = new();
    private readonly Random _random = new();
    private readonly Dictionary<int, SolidColorBrush> _brushCache = new(128);

    public int MaxParticles { get; set; } = 2000; // Increased for continuous streams
    public int ParticlesPerBurst { get; set; } = 20;
    public double ParticleLifetime { get; set; } = 1.0;
    public double Gravity { get; set; } = 300;
    public bool EmberMode { get; set; } = false;

    public struct Particle
    {
        public double X, Y;
        public double VX, VY;
        public double Life;
        public double MaxLife;
        public Color Color;
        public double Size;
        public bool IsGlow;
        public double HorizontalSway; // for ember drift
    }

    public void Emit(double x, double y, Color color)
    {
        int count = Math.Min(ParticlesPerBurst, MaxParticles - _particles.Count);

        for (int i = 0; i < count; i++)
        {
            double angle, speed;
            bool isGlow = _random.NextDouble() < 0.4;

            if (isGlow)
            {
                angle = -Math.PI / 2 + (_random.NextDouble() - 0.5) * Math.PI * 0.6;
                speed = 40 + _random.NextDouble() * 100;
            }
            else
            {
                angle = -Math.PI / 2 + (_random.NextDouble() - 0.5) * Math.PI * 1.2;
                speed = 80 + _random.NextDouble() * 220;
            }

            double size = isGlow
                ? 3 + _random.Next(5)   // glow: 3-8 px
                : 1.0 + _random.Next(3); // regular: 1-4 px

            _particles.Add(new Particle
            {
                X = x + (_random.NextDouble() - 0.5) * 6,
                Y = y,
                VX = Math.Cos(angle) * speed,
                VY = Math.Sin(angle) * speed,
                Life = ParticleLifetime * (0.5 + _random.NextDouble() * 0.7),
                MaxLife = ParticleLifetime,
                Color = color,
                Size = size,
                IsGlow = isGlow,
                HorizontalSway = (_random.NextDouble() - 0.5) * 2.0
            });
        }
    }

    /// <summary>Emit a steady stream of embers for held keys.</summary>
    public void EmitContinuous(double x, double y, Color color)
    {
        if (_particles.Count >= MaxParticles) return;

        // Emit 1-2 particles per call (frame)
        int count = _random.Next(1, 3);
        for (int i = 0; i < count; i++)
        {
            bool isGlow = _random.NextDouble() < 0.3;
            double angle = -Math.PI / 2 + (_random.NextDouble() - 0.5) * 0.4; // tighter upward cone
            double speed = 50 + _random.NextDouble() * 120;

            _particles.Add(new Particle
            {
                X = x + (_random.NextDouble() - 0.5) * 12,
                Y = y + (_random.NextDouble() - 0.5) * 4,
                VX = Math.Cos(angle) * speed,
                VY = Math.Sin(angle) * speed,
                Life = (EmberMode ? 2.5 : 1.0) * (0.6 + _random.NextDouble() * 0.6),
                MaxLife = EmberMode ? 2.5 : 1.0,
                Color = color,
                Size = isGlow ? 2 + _random.Next(4) : 1 + _random.Next(2),
                IsGlow = isGlow,
                HorizontalSway = (_random.NextDouble() - 0.5) * 3.5
            });
        }
    }

    /// <summary>Emit a burst of sparkles (small, fast, short-lived).</summary>
    public void EmitSparks(double x, double y, Color color)
    {
        int count = Math.Min(12, MaxParticles - _particles.Count);
        for (int i = 0; i < count; i++)
        {
            double angle = _random.NextDouble() * Math.PI * 2;
            double speed = 180 + _random.NextDouble() * 350;

            _particles.Add(new Particle
            {
                X = x,
                Y = y,
                VX = Math.Cos(angle) * speed,
                VY = Math.Sin(angle) * speed - 60,
                Life = 0.2 + _random.NextDouble() * 0.4,
                MaxLife = 0.6,
                Color = color,
                Size = 1 + _random.NextDouble() * 1.5,
                IsGlow = false
            });
        }
    }

    public void Update(double dt)
    {
        double gravity = EmberMode ? -180 : Gravity; // float up in ember mode
        double time = DateTime.Now.TimeOfDay.TotalSeconds;

        for (int i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];
            
            // Add horizontal drift/sway
            p.VX += Math.Sin(time * 3 + p.Y * 0.01) * p.HorizontalSway * 10 * dt;
            
            p.X += p.VX * dt;
            p.Y += p.VY * dt;
            p.VY += gravity * dt;

            // Air resistance
            double drag = p.IsGlow ? 0.97 : 0.99;
            p.VX *= drag;
            p.VY *= drag;

            p.Life -= dt;

            if (p.Life <= 0)
            {
                int lastIdx = _particles.Count - 1;
                _particles[i] = _particles[lastIdx];
                _particles.RemoveAt(lastIdx);
            }
            else
            {
                _particles[i] = p;
            }
        }
    }

    /// <summary>Draw all particles. Glow particles get a softer, larger appearance.</summary>
    public void Draw(DrawingContext dc)
    {
        if (_particles.Count == 0) return;

        const int alphaLevels = 8;
        Span<byte> alphaBuckets = stackalloc byte[alphaLevels];
        for (int i = 0; i < alphaLevels; i++)
            alphaBuckets[i] = (byte)(i * 255 / (alphaLevels - 1));

        foreach (var p in _particles)
        {
            float lifeRatio = (float)(p.Life / p.MaxLife);
            byte rawAlpha = (byte)(lifeRatio * 255);

            int bucket = (rawAlpha * (alphaLevels - 1) + 127) / 255;
            byte alpha = alphaBuckets[bucket];

            // Glow particles are more transparent
            if (p.IsGlow)
                alpha = (byte)(alpha * 50 / 100);

            int key = (p.Color.R << 24) | (p.Color.G << 16) | (p.Color.B << 8) | bucket;

            if (!_brushCache.TryGetValue(key, out var brush))
            {
                brush = new SolidColorBrush(Color.FromArgb(alpha, p.Color.R, p.Color.G, p.Color.B));
                brush.Freeze();
                _brushCache[key] = brush;
            }

            double sz = p.Size;

            // Glow particles shrink over lifetime
            if (p.IsGlow)
                sz *= (0.5 + 0.5 * lifeRatio);

            dc.DrawRectangle(brush, null, new Rect(p.X - sz / 2, p.Y - sz / 2, sz, sz));
        }
    }

    public int Count => _particles.Count;
    public void Clear() => _particles.Clear();
}
