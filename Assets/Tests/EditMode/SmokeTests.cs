using NUnit.Framework;

public class SmokeTests
{
    [Test]
    public void GameCodeLivesInWar2dAssembly()
    {
        Assert.That(typeof(GameCore).Assembly.GetName().Name, Is.EqualTo("WAR2D"));
    }
}
