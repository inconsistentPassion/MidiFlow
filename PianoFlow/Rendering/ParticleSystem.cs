using System.Windows;
using System.Windows.Media;

namespace PianoFlow.Rendering;

/// <summary>
/// WPF-accelerated particle system. Draws particles as small rectangles via DrawingContext.
/// No pixel manipulation needed.
/// </summary>
public class ParticleSystem
{
    private readonly List<Particle> _particles = new();
    private readonly Random _random = new();

    public int MaxParticles { get; set; } = 500;
    public int ParticlesPerBurst { get; set; } = 12;
    public double ParticleLifetime { get; set; } = 0.8;
    public double Gravity { get; set; } = 400;

    public struct Particle
    {
        public double X, Y;
        public double VX, VY;
        public double Life;
        public double MaxLife;
        public Color Color;
        public int Size;
    }

    public void Emit(double x, double y, Color color)
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
                VY = Math.Sin(angle) * speed - 100,
                Life = ParticleLifetime * (0.5 + _random.NextDouble() * 0.5),
                MaxLife = ParticleLifetime,
                Color = color,
                Size = 2 + _random.Next(3)
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
            p.Life -= dt;

            if (p.Life <= 0)
                _particles.RemoveAt(i);
            else
                _particles[i] = p;
        }
    }

    /// <summary>Draw all particles via WPF DrawingContext.</summary>
    public void Draw(DrawingContext dc)
    {
        foreach (var p in _particles)
        {
            float lifeRatio = (float)(p.Life / p.MaxLife);
            byte alpha = (byte)(lifeRatio * 255);
            var brush = new SolidColorBrush(Color.FromArgb(alpha, p.Color.R, p.Color.G, p.Color.B));
            brush.Freeze();

            dc.DrawRectangle(brush, null, new Rect(p.X, p.Y, p.Size, p.Size));
        }
    }

    public int Count => _particles.Count;
    public void Clear() => _particles.Clear();
}
