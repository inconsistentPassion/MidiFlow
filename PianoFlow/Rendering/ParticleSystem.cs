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

    public int MaxParticles { get; set; } = 800;
    public int ParticlesPerBurst { get; set; } = 20;  // more particles per hit
    public double ParticleLifetime { get; set; } = 1.0;
    public double Gravity { get; set; } = 300;

    public struct Particle
    {
        public double X, Y;
        public double VX, VY;
        public double Life;
        public double MaxLife;
        public Color Color;
        public double Size;
        public bool IsGlow;  // glow particles are larger and more transparent
    }

    public void Emit(double x, double y, Color color)
    {
        int count = Math.Min(ParticlesPerBurst, MaxParticles - _particles.Count);

        for (int i = 0; i < count; i++)
        {
            // Mix of upward spray and radial burst
            double angle, speed;
            bool isGlow = _random.NextDouble() < 0.3;  // 30% glow particles

            if (isGlow)
            {
                // Glow particles: mostly upward, slow
                angle = -Math.PI / 2 + (_random.NextDouble() - 0.5) * Math.PI * 0.5;
                speed = 30 + _random.NextDouble() * 80;
            }
            else
            {
                // Regular particles: radial burst with upward bias
                angle = -Math.PI / 2 + (_random.NextDouble() - 0.5) * Math.PI * 1.4;
                speed = 60 + _random.NextDouble() * 250;
            }

            double size = isGlow
                ? 4 + _random.Next(6)   // glow: 4-9 px
                : 1.5 + _random.Next(3); // regular: 1.5-4 px

            _particles.Add(new Particle
            {
                X = x + (_random.NextDouble() - 0.5) * 8,
                Y = y,
                VX = Math.Cos(angle) * speed,
                VY = Math.Sin(angle) * speed,
                Life = ParticleLifetime * (0.4 + _random.NextDouble() * 0.6),
                MaxLife = ParticleLifetime,
                Color = color,
                Size = size,
                IsGlow = isGlow
            });
        }
    }

    /// <summary>Emit a burst of sparkles (small, fast, short-lived).</summary>
    public void EmitSparks(double x, double y, Color color)
    {
        int count = Math.Min(8, MaxParticles - _particles.Count);
        for (int i = 0; i < count; i++)
        {
            double angle = _random.NextDouble() * Math.PI * 2;
            double speed = 150 + _random.NextDouble() * 300;

            _particles.Add(new Particle
            {
                X = x,
                Y = y,
                VX = Math.Cos(angle) * speed,
                VY = Math.Sin(angle) * speed - 50,
                Life = 0.2 + _random.NextDouble() * 0.3,
                MaxLife = 0.5,
                Color = color,
                Size = 1 + _random.NextDouble(),
                IsGlow = false
            });
        }
    }

    public void Update(double dt)
    {
        for (int i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];
            p.X += p.VX * dt;
            p.Y += p.VY * dt;
            p.VY += Gravity * dt;

            // Air resistance for glow particles (they float more)
            if (p.IsGlow)
            {
                p.VX *= 0.98;
                p.VY *= 0.98;
            }

            p.Life -= dt;

            if (p.Life <= 0)
                _particles.RemoveAt(i);
            else
                _particles[i] = p;
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

        var brushCache = new Dictionary<int, SolidColorBrush>(128);

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

            if (!brushCache.TryGetValue(key, out var brush))
            {
                brush = new SolidColorBrush(Color.FromArgb(alpha, p.Color.R, p.Color.G, p.Color.B));
                brush.Freeze();
                brushCache[key] = brush;
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
