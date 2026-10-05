using NUnit.Framework;
using Unity.Mathematics;

public class MovementMathTests
{
    [Test]
    public void Step_NeverOvershootsTarget()
    {
        float speed = 100f;
        float3 p = MovementMath.Step(float3.zero, new float3(1, 0, 0), ref speed, 100f, 0f, 1f);
        Assert.That(p.x, Is.EqualTo(1f).Within(1e-5));
    }

    [Test]
    public void Step_AcceleratesUpToMaxSpeed()
    {
        float speed = 0f;
        MovementMath.Step(float3.zero, new float3(100, 0, 0), ref speed, 5f, 2f, 1f);
        Assert.That(speed, Is.EqualTo(2f).Within(1e-5));
        MovementMath.Step(float3.zero, new float3(100, 0, 0), ref speed, 5f, 2f, 10f);
        Assert.That(speed, Is.EqualTo(5f).Within(1e-5));
    }

    [Test]
    public void Step_ZeroAcceleration_MeansInstantMaxSpeed()
    {
        float speed = 0f;
        float3 p = MovementMath.Step(float3.zero, new float3(100, 0, 0), ref speed, 5f, 0f, 1f);
        Assert.That(p.x, Is.EqualTo(5f).Within(1e-5));
    }

    [Test]
    public void Step_AtTarget_StopsWithoutNaN()
    {
        float speed = 3f;
        float3 p = MovementMath.Step(new float3(1, 1, 0), new float3(1, 1, 0), ref speed, 5f, 1f, 1f);
        Assert.That(math.any(math.isnan(p)), Is.False);
        Assert.That(speed, Is.EqualTo(0f));
    }
}
