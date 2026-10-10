using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using Config;
using NUnit.Framework;

public class ConfigParserTests
{
    internal const string ValidXml = @"<GameConfig>
  <Resources><PassiveGenerationRate>5</PassiveGenerationRate><StartingResources>1000</StartingResources><MiningRate>10</MiningRate><DecayPercentPerSecond>5</DecayPercentPerSecond></Resources>
  <Match><Scene>Map_2</Scene><CountdownSeconds>5</CountdownSeconds><Map><Size>1024</Size><Seed>0</Seed><GemChance>0.04</GemChance></Map></Match>
  <Simulation><TickRate>20</TickRate><TargetSearchSliceTicks>8</TargetSearchSliceTicks><SeparationIntervalTicks>2</SeparationIntervalTicks><SeparationStrength>1</SeparationStrength><HashCellSize>5</HashCellSize><MaxFieldRebuildsPerTick>2</MaxFieldRebuildsPerTick><MaxUnitsPerPlayer>10000</MaxUnitsPerPlayer><MaxEntities>131072</MaxEntities><FogCellSize>2</FogCellSize><VisionIntervalTicks>4</VisionIntervalTicks></Simulation>
  <Replication><CorrectionIntervalTicks>4</CorrectionIntervalTicks><CorrectionThreshold>0.25</CorrectionThreshold><DeltaScale>8</DeltaScale><OffscreenThreshold>2</OffscreenThreshold><OffscreenIntervalTicks>20</OffscreenIntervalTicks><SnapshotBytesPerSecond>262144</SnapshotBytesPerSecond></Replication>
  <Orders><MaxQueued>4</MaxQueued></Orders>
  <Diplomacy><ChangeCooldownSeconds>2</ChangeCooldownSeconds></Diplomacy>
  <Lobby><MapSizes>256 512 1024</MapSizes><StartingResources>500 1000 2500 5000</StartingResources></Lobby>
  <MenuBattle><UnitsPerArmy>1500</UnitsPerArmy><MapSize>512</MapSize><Seed>7</Seed></MenuBattle>
  <Gifting><CooldownSeconds>5</CooldownSeconds></Gifting>
  <Alerts><ThrottleSeconds>10</ThrottleSeconds><AreaTiles>32</AreaTiles><ShowSeconds>8</ShowSeconds></Alerts>
  <DamageTable><Entry attacker=""Tank"" target=""Unit"">1.0</Entry><Entry attacker=""Tank"" target=""Building"">1.0</Entry><Entry attacker=""Tank"" target=""Wall"">0.5</Entry></DamageTable>
  <Units>
    <Unit type=""Tank""><Health>100</Health><Damage>10</Damage><Range>5</Range><AttackInterval>1</AttackInterval><MoveSpeed>5</MoveSpeed><Acceleration>5</Acceleration><UpfrontCost>50</UpfrontCost><RunningCost>2</RunningCost><Radius>0.35</Radius><SizeClass>0</SizeClass><Sight>8</Sight></Unit>
  </Units>
  <Buildings>
    <Building type=""Base""><Health>500</Health><Width>3</Width><Height>3</Height><UpfrontCost>0</UpfrontCost><RunningCost>0</RunningCost><SpawnRate>0</SpawnRate><Sight>4</Sight></Building>
    <Building type=""Miner""><Health>50</Health><Width>1</Width><Height>1</Height><UpfrontCost>50</UpfrontCost><RunningCost>0</RunningCost><SpawnRate>0</SpawnRate><Sight>4</Sight></Building>
    <Building type=""SmallUnitSpawner""><Health>300</Health><Width>2</Width><Height>2</Height><UpfrontCost>200</UpfrontCost><RunningCost>20</RunningCost><SpawnRate>1</SpawnRate><Sight>4</Sight></Building>
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

    [Test]
    public void ParsesV04Sections()
    {
        var errors = new List<string>();
        GameConfigData c = ConfigParser.Parse(ValidXml, errors);
        Assert.That(errors, Is.Empty);
        Assert.AreEqual(1024, c.Match.Map.Size);
        Assert.AreEqual(20, c.Simulation.TickRate);
        Assert.AreEqual(0.05f, c.Simulation.TickSeconds, 1e-6f);
        Assert.AreEqual(8, c.Simulation.TargetSearchSliceTicks);
        Assert.AreEqual(4, c.Replication.CorrectionIntervalTicks);
        Assert.AreEqual(0.35f, c.GetUnit(UnitType.Tank).Radius, 1e-6f);
        Assert.AreEqual(0.5f, c.Damage.Multiplier(UnitType.Tank, TargetClass.Wall), 1e-6f);
    }

    [TestCase("<TickRate>0</TickRate>", "TickRate")]
    [TestCase("<HashCellSize>1</HashCellSize>", "HashCellSize")]
    [TestCase("<Size>100</Size>", "Size")]          // map sizes are 0 (scene) or 128..4096
    [TestCase("<DeltaScale>200</DeltaScale>", "DeltaScale")] // the header keeps 7 bits
    public void RejectsOutOfRangeV04Values(string replacement, string field)
    {
        string xml = Regex.Replace(ValidXml, $"<{field}>[^<]*</{field}>", replacement);
        var errors = new List<string>();
        ConfigParser.Parse(xml, errors);
        Assert.That(errors, Has.Some.Contains(field));
    }

    [Test]
    public void OrdersMaxQueuedParses()
    {
        var errors = new List<string>();
        GameConfigData c = ConfigParser.Parse(ValidXml, errors);
        Assert.That(errors, Is.Empty);
        Assert.AreEqual(4, c.Orders.MaxQueued);
    }

    [TestCase("<MaxQueued>-1</MaxQueued>")]
    [TestCase("<MaxQueued>17</MaxQueued>")]
    [TestCase("")]
    public void OrdersMaxQueuedRequiredAndInRange(string replacement)
    {
        string xml = Regex.Replace(ValidXml, "<MaxQueued>[^<]*</MaxQueued>", replacement);
        var errors = new List<string>();
        ConfigParser.Parse(xml, errors);
        Assert.That(errors, Has.Some.Contains("MaxQueued"));
    }

    [Test]
    public void DiplomacyCooldownParses()
    {
        var errors = new List<string>();
        GameConfigData c = ConfigParser.Parse(ValidXml, errors);
        Assert.That(errors, Is.Empty);
        Assert.AreEqual(2f, c.Diplomacy.ChangeCooldownSeconds, 1e-6f);
    }

    [Test]
    public void AlertsParse()
    {
        var errors = new List<string>();
        GameConfigData c = ConfigParser.Parse(ValidXml, errors);
        Assert.That(errors, Is.Empty);
        Assert.AreEqual(10f, c.Alerts.ThrottleSeconds, 1e-6f);
        Assert.AreEqual(32, c.Alerts.AreaTiles);
        Assert.AreEqual(8f, c.Alerts.ShowSeconds, 1e-6f);
    }

    [TestCase("<AreaTiles>0</AreaTiles>", "AreaTiles")]
    [TestCase("<ShowSeconds>0</ShowSeconds>", "ShowSeconds")]
    [TestCase("", "AreaTiles")]
    public void AlertsRequiredAndInRange(string replacement, string field)
    {
        string xml = Regex.Replace(ValidXml, $"<{field}>[^<]*</{field}>", replacement);
        var errors = new List<string>();
        ConfigParser.Parse(xml, errors);
        Assert.That(errors, Has.Some.Contains(field));
    }

    [TestCase("<ChangeCooldownSeconds>-1</ChangeCooldownSeconds>")]
    [TestCase("<ChangeCooldownSeconds>61</ChangeCooldownSeconds>")]
    [TestCase("")]
    public void DiplomacyCooldownRequiredAndInRange(string replacement)
    {
        string xml = Regex.Replace(ValidXml, "<ChangeCooldownSeconds>[^<]*</ChangeCooldownSeconds>", replacement);
        var errors = new List<string>();
        ConfigParser.Parse(xml, errors);
        Assert.That(errors, Has.Some.Contains("ChangeCooldownSeconds"));
    }

    [Test]
    public void UnknownDamageTableTypesAreErrors()
    {
        string xml = ValidXml.Replace("attacker=\"Tank\" target=\"Wall\"", "attacker=\"Ship\" target=\"Wall\"");
        var errors = new List<string>();
        ConfigParser.Parse(xml, errors);
        Assert.That(errors, Has.Some.Contains("Ship"));
    }
}
