using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

public class CombatRulesTests
{
    [Test]
    public void AttackReady_OnlyAfterInterval()
    {
        Assert.That(CombatRules.IsAttackReady(10.0, 9.5, 1f), Is.False);
        Assert.That(CombatRules.IsAttackReady(10.5, 9.5, 1f), Is.True);
    }

    [Test]
    public void FindNearestEnemy_SkipsAlliesDeadAndOutOfRange()
    {
        var pos = new NativeArray<float3>(new[] { new float3(1, 0, 0), new float3(2, 0, 0), new float3(3, 0, 0), new float3(50, 0, 0), new float3(4, 0, 0) }, Allocator.Temp);
        var own = new NativeArray<int>(new[] { 1, 2, 2, 2, 2 }, Allocator.Temp);
        var hp = new NativeArray<float>(new[] { 10f, 0f, 10f, 10f, 10f }, Allocator.Temp);

        int index = CombatRules.FindNearestEnemy(float3.zero, 5f, ownerId: 1, pos, own, hp);

        Assert.That(index, Is.EqualTo(2)); // 0 is an ally, 1 is dead, 3 is out of range, 2 is nearer than 4
        pos.Dispose(); own.Dispose(); hp.Dispose();
    }

    [Test]
    public void FindNearestEnemy_NoneInRange_ReturnsMinusOne()
    {
        var pos = new NativeArray<float3>(new[] { new float3(10, 0, 0) }, Allocator.Temp);
        var own = new NativeArray<int>(new[] { 2 }, Allocator.Temp);
        var hp = new NativeArray<float>(new[] { 10f }, Allocator.Temp);
        Assert.That(CombatRules.FindNearestEnemy(float3.zero, 5f, 1, pos, own, hp), Is.EqualTo(-1));
        pos.Dispose(); own.Dispose(); hp.Dispose();
    }
}
