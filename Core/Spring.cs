namespace GlassIsland.Core;

/// <summary>Damped harmonic spring, integrated with fixed sub-steps so motion is identical at any frame rate.</summary>
public sealed class Spring
{
    public double Value;
    public double Velocity;
    public double Target;
    public double Stiffness;
    public double Damping;
    public double Epsilon;

    public Spring(double value, double stiffness = 300, double damping = 26, double epsilon = 0.02)
    {
        Value = Target = value;
        Stiffness = stiffness;
        Damping = damping;
        Epsilon = epsilon;
    }

    public bool IsSettled => Velocity == 0 && Value == Target;

    public void Snap()
    {
        Value = Target;
        Velocity = 0;
    }

    /// <summary>Advances the spring; returns true while it is still moving.</summary>
    public bool Step(double dt)
    {
        if (IsSettled) return false;
        const double h = 1.0 / 240.0;
        while (dt > 1e-7)
        {
            double s = Math.Min(h, dt);
            double a = -Stiffness * (Value - Target) - Damping * Velocity;
            Velocity += a * s;
            Value += Velocity * s;
            dt -= s;
        }
        if (Math.Abs(Velocity) < Epsilon * 5 && Math.Abs(Value - Target) < Epsilon)
        {
            Snap();
            return false;
        }
        return true;
    }
}
