using System.Linq;
using NUnit.Framework;

public class GameInputTests
{
    [TestCase("Pan", "<Keyboard>/upArrow")]
    [TestCase("Zoom", "<Mouse>/scroll/y")]
    [TestCase("FastPan", "<Keyboard>/shift")]
    [TestCase("Select", "<Mouse>/leftButton")]
    [TestCase("Command", "<Mouse>/rightButton")]
    [TestCase("Rotate", "<Keyboard>/r")]
    [TestCase("Point", "<Pointer>/position")]
    [TestCase("AssignModifier", "<Keyboard>/ctrl")]
    [TestCase("AppendModifier", "<Keyboard>/shift")]
    [TestCase("AttackMove", "<Keyboard>/a")]
    [TestCase("Stop", "<Keyboard>/s")]
    [TestCase("Hold", "<Keyboard>/h")]
    [TestCase("QueueModifier", "<Keyboard>/shift")]
    [TestCase("Squad1", "<Keyboard>/1")]
    [TestCase("Squad0", "<Keyboard>/0")]
    public void ActionHasDefaultBinding(string action, string path)
    {
        Assert.That(GameInput.Map[action].bindings.Select(b => b.path), Has.Member(path));
    }

    [TestCase("<Keyboard>/a")]
    [TestCase("<Keyboard>/s")]
    [TestCase("<Keyboard>/h")]
    public void OrderKeysDoNotPan(string path)
    {
        Assert.That(GameInput.Pan.bindings.Select(b => b.path), Has.No.Member(path));
    }
}
