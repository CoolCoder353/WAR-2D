using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Config;
using NUnit.Framework;

public class ConfigParserTests
{
    internal const string ValidXml = @"<GameConfig>
  <Resources><PassiveGenerationRate>5</PassiveGenerationRate><StartingResources>1000</StartingResources><MiningRate>10</MiningRate><DecayPercentPerSecond>5</DecayPercentPerSecond></Resources>
  <Match><Scene>Map_2</Scene><CountdownSeconds>5</CountdownSeconds></Match>
  <Units>
    <Unit type=""Tank""><Health>100</Health><Damage>10</Damage><Range>5</Range><AttackInterval>1</AttackInterval><MoveSpeed>5</MoveSpeed><Acceleration>5</Acceleration><UpfrontCost>50</UpfrontCost><RunningCost>2</RunningCost></Unit>
  </Units>
  <Buildings>
    <Building type=""Base""><Health>500</Health><Width>3</Width><Height>3</Height><UpfrontCost>0</UpfrontCost><RunningCost>0</RunningCost><SpawnRate>0</SpawnRate></Building>
    <Building type=""Miner""><Health>50</Health><Width>1</Width><Height>1</Height><UpfrontCost>50</UpfrontCost><RunningCost>0</RunningCost><SpawnRate>0</SpawnRate></Building>
    <Building type=""SmallUnitSpawner""><Health>300</Health><Width>2</Width><Height>2</Height><UpfrontCost>200</UpfrontCost><RunningCost>20</RunningCost><SpawnRate>1</SpawnRate></Building>
  </Buildings>
</GameConfig>";

    [Test]
    public void ValidXml_ParsesWithoutErrors()
    {
        var errors = new List<string>();
        GameConfigData c = ConfigParser.Parse(ValidXml, errors);

        Assert.That(errors, Is.Empty);
        Assert.That(c.Resources.StartingResources, Is.EqualTo(1000f));
        Assert.That(c.Resources.DecayPercentPerSecond, Is.EqualTo(5f));
        Assert.That(c.Match.Scene, Is.EqualTo("Map_2"));
        Assert.That(c.GetUnit(UnitType.Tank).Range, Is.EqualTo(5f));
        Assert.That(c.GetBuilding(BuildingType.SmallUnitSpawner).UpfrontCost, Is.EqualTo(200f));
        Assert.That(c.GetBuilding(BuildingType.Base).Size.x, Is.EqualTo(3));
    }

    [Test]
    public void MissingBuildingType_IsAnError()
    {
        var errors = new List<string>();
        ConfigParser.Parse(ValidXml.Replace(@"type=""Miner""", @"type=""Ignored"""), errors);

        Assert.That(errors, Has.Some.Contains("Miner"));
        Assert.That(errors, Has.Some.Contains("Ignored"));
    }

    [Test]
    public void MissingField_IsAnError()
    {
        var errors = new List<string>();
        ConfigParser.Parse(ValidXml.Replace("<Range>5</Range>", ""), errors);

        Assert.That(errors, Has.Some.Contains("Range"));
    }

    [Test]
    public void NonNumericValue_IsAnError()
    {
        var errors = new List<string>();
        ConfigParser.Parse(ValidXml.Replace("<Health>100</Health>", "<Health>lots</Health>"), errors);

        Assert.That(errors, Has.Some.Contains("Health"));
    }

    [Test]
    public void NegativeCost_IsAnError()
    {
        var errors = new List<string>();
        ConfigParser.Parse(ValidXml.Replace("<UpfrontCost>50</UpfrontCost><RunningCost>2</RunningCost>", "<UpfrontCost>-1</UpfrontCost><RunningCost>2</RunningCost>"), errors);

        Assert.That(errors, Has.Some.Contains("UpfrontCost"));
    }

    [Test]
    public void ParsingIgnoresMachineCulture()
    {
        CultureInfo previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            var errors = new List<string>();
            GameConfigData c = ConfigParser.Parse(ValidXml.Replace("<MiningRate>10</MiningRate>", "<MiningRate>2.5</MiningRate>"), errors);

            Assert.That(errors, Is.Empty);
            Assert.That(c.Resources.MiningRate, Is.EqualTo(2.5f));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Test]
    public void MalformedXml_IsAnError()
    {
        var errors = new List<string>();
        ConfigParser.Parse("<GameConfig><Resources>", errors);

        Assert.That(errors, Is.Not.Empty);
    }

    [Test]
    public void ShippedGameConfig_IsValid()
    {
        var errors = new List<string>();
        ConfigParser.Parse(File.ReadAllText("Assets/Resources/GameConfig.xml"), errors);

        Assert.That(errors, Is.Empty, string.Join("\n", errors));
    }
}
