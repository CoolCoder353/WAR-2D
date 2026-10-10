using NUnit.Framework;
using Unity.Mathematics;

public class CommandValidatorTests
{
    [Test]
    public void Box_WithinSpan_IsValid() =>
        Assert.That(CommandValidator.IsBoxValid(new int2(0, 0), new int2(256, -256)), Is.True);

    [Test]
    public void Box_TooLarge_IsInvalid() =>
        Assert.That(CommandValidator.IsBoxValid(new int2(0, 0), new int2(257, 0)), Is.False);

    [Test]
    public void Box_ExtremeValues_DoNotOverflow() =>
        Assert.That(CommandValidator.IsBoxValid(new int2(int.MinValue, 0), new int2(int.MaxValue, 0)), Is.False);

    [TestCase(0, true)]
    [TestCase(1, true)]
    [TestCase(2, true)]
    [TestCase(3, true)]
    [TestCase(4, false)]
    [TestCase(255, false)]
    public void OrderKindRange(int kind, bool valid) =>
        Assert.That(CommandValidator.IsOrderKindValid((byte)kind), Is.EqualTo(valid));

    [TestCase("Alice", "Alice")]
    [TestCase("  Bob  ", "Bob")]
    [TestCase("<color=red>Eve</color>", "color=redEve/color")]
    [TestCase("Tab\tName\n", "TabName")]
    public void Nickname_IsSanitised(string raw, string expected)
    {
        Assert.That(CommandValidator.TrySanitizeNickname(raw, out string clean), Is.True);
        Assert.That(clean, Is.EqualTo(expected));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("<>")]
    public void Nickname_EmptyAfterCleaning_IsRejected(string raw) =>
        Assert.That(CommandValidator.TrySanitizeNickname(raw, out _), Is.False);

    [Test]
    public void Nickname_IsTruncated()
    {
        CommandValidator.TrySanitizeNickname(new string('x', 100), out string clean);
        Assert.That(clean.Length, Is.EqualTo(CommandValidator.MaxNicknameLength));
    }
}
