using NUnit.Framework;

/// <summary>Formations from the player's orders (v0.16): only a Move or Attack-move sent with the setting on forms up.</summary>
public class FormationOrderTests
{
    [TestCase(OrderKind.Move, true, true)]
    [TestCase(OrderKind.AttackMove, true, true)]
    [TestCase(OrderKind.Move, false, false)]
    [TestCase(OrderKind.Stop, true, false)]
    [TestCase(OrderKind.Hold, true, false)]
    [TestCase(OrderKind.Build, true, false)]
    [TestCase(OrderKind.Dig, true, false)]
    public void OnlyMovesFormUp(OrderKind kind, bool formation, bool expected) =>
        Assert.AreEqual(expected, PlayerActions.FormsUp(kind, formation));
}
