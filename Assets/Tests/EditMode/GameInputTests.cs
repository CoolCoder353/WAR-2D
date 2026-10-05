using System.Linq;
using NUnit.Framework;

public class GameInputTests
{
    [TestCase("Pan", "<Keyboard>/w")]
    [TestCase("Pan", "<Keyboard>/upArrow")]
    [TestCase("Zoom", "<Mouse>/scroll/y")]
    [TestCase("FastPan", "<Keyboard>/shift")]
    [TestCase("Select", "<Mouse>/leftButton")]
    [TestCase("Command", "<Mouse>/rightButton")]
    [TestCase("Rotate", "<Keyboard>/r")]
    [TestCase("Point", "<Pointer>/position")]
    public void ActionHasDefaultBinding(string action, string path)
    {
        Assert.That(GameInput.Map[action].bindings.Select(b => b.path), Has.Member(path));
    }
}
