using UnityEngine.InputSystem;
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
    [TestCase("Repair", "<Keyboard>/f")]
    [TestCase("Dig", "<Keyboard>/g")]
    [TestCase("Cancel", "<Keyboard>/delete")]
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

    [Test]
    public void RebindingOverridesPersistThroughSaveAndLoad()
    {
        try
        {
            GameInput.AttackMove.ApplyBindingOverride(0, "<Keyboard>/q");
            string json = Rebinding.Save(GameInput.Map);
            GameInput.Map.RemoveAllBindingOverrides();
            Assert.That(GameInput.AttackMove.bindings[0].effectivePath, Is.EqualTo("<Keyboard>/a"));
            Rebinding.Load(GameInput.Map, json);
            Assert.That(GameInput.AttackMove.bindings[0].effectivePath, Is.EqualTo("<Keyboard>/q"));
        }
        finally
        {
            GameInput.Map.RemoveAllBindingOverrides();
        }
    }

    [TestCase("<Keyboard>/f")]
    [TestCase("<Keyboard>/g")]
    [TestCase("<Keyboard>/delete")]
    public void WorkKeysConflictWithNothing(string path)
    {
        int users = GameInput.Map.actions.Count(a => a.bindings.Any(b => b.path == path));
        Assert.That(users, Is.EqualTo(1), path);
    }

    [Test]
    public void ConflictsAreFoundAndSwapped()
    {
        try
        {
            Assert.That(Rebinding.ConflictWith(GameInput.AttackMove, "<Keyboard>/s"), Is.SameAs(GameInput.Stop));
            Assert.That(Rebinding.ConflictWith(GameInput.AttackMove, "<Keyboard>/f12"), Is.Null);
            Assert.That(Rebinding.ConflictWith(GameInput.AttackMove, "<Keyboard>/shift"), Is.SameAs(GameInput.QueueModifier), "Fast pan and Append share Shift on purpose: only the listed Queue order counts");
            Assert.That(Rebinding.ConflictWith(GameInput.Stop, "<Keyboard>/1"), Is.Not.Null.And.Property("name").EqualTo("Squad1"));
            Rebinding.Swap(GameInput.AttackMove, GameInput.Stop);
            Assert.That(GameInput.AttackMove.bindings[0].effectivePath, Is.EqualTo("<Keyboard>/s"));
            Assert.That(GameInput.Stop.bindings[0].effectivePath, Is.EqualTo("<Keyboard>/a"));
        }
        finally
        {
            GameInput.Map.RemoveAllBindingOverrides();
        }
    }

    [Test]
    public void MenuAndDragPanBindings()
    {
        Assert.That(GameInput.Menu.bindings[0].path, Is.EqualTo("<Keyboard>/escape"));
        Assert.That(GameInput.DragPan.bindings[0].path, Is.EqualTo("<Mouse>/middleButton"));
    }
}
