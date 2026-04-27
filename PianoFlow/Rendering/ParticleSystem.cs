using System.Windows;
using System.Windows.Media.Imaging;

namespace PianoFlow.Rendering;

/// <summary>
/// Simple particle burst effect system.
/// Particles spawn on note hits, affected by gravity, with fade-out.
/// </summary>
public class ParticleSystem
{
    private readonly List<Particle> _particles = new();
    private Random _random = new();

    public int MaxParticles { get; set; } = 500;
    public int ParticlesPerBurst { get; set; } = 12;
    public double ParticleLifetime { get; set; } = 0.8; // seconds
    public double Gravity { get; set; } = 400; // pixels/s²

    public struct Particle
    {
        public double X, Y;
        public double VX, VY;
        public double Life;      // remaining life in seconds
        public double MaxLife;
        public uint Color;
        public int Size;
    }

    /// <summary>Emit a burst of particles at the given position.</summary>
    public void Emit(double x, double y, uint color)
    {
        for (int i = 0; i < ParticlesPerBurst && _particles.Count < MaxParticles; i++)
        {
            double angle = _random.NextDouble() * Math.PI * 2;
            double speed = 50 + _random.NextDouble() * 200;

            _particles.Add(new Particle
            {
                X = x,
                Y = y,
                VX = Math.Cos(angle) * speed,
                VY = Math.Sin(angle) * speed - 100, // initial upward bias
                Life = ParticleLifetime * (0.5 + _random.NextDouble() * 0.5),
                MaxLife = ParticleLifetime,
                Color = color,
                Size = 2 + _random.Next(3)
            });
        }
    }

    /// <summary>Update all particles. Call once per frame.</summary>
    public void Update(double dt)
    {
        for (int i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];
            p.X += p.VX * dt;
            p.Y += p.VY * dt;
            p.VY += Gravity * dt;
            p.Life -= dt;

            if (p.Life <= 0)
                _particles.RemoveAt(i);
            else
                _particles[i] = p;
        }
    }

    /// <summary>Render all particles into the buffer.</summary>
    public unsafe void Render(uint* buffer, int stride, int width, int height)
    {
        foreach (var p in _particles)
        {
            float lifeRatio = (float)(p.Life / p.MaxLife);
            byte alpha = (byte)(lifeRatio * 255);
            uint color = (p.Color & 0x00FFFFFF) | ((uint)alpha << 24);

            int size = p.Size;
            int px = (int)p.X;
            int py = (int)p.Y;

            for (int dy = 0; dy < size; dy++)
            {
                for (int dx = 0; dx < size; dx++)
                {
                    int x = px + dx;
                    int y = py + dy;
                    if (x >= 0 && x < width && y >= 0 && y < height)
                    {
                        buffer[y * stride + x] = color;
                    }
                }
            }
        }
    }

    public int Count => _particles.Count;

    public void Clear() => _particles.Clear();
}
