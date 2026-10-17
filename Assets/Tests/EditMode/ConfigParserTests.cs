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
  <Match><Scene>Map_2</Scene><CountdownSeconds>5</CountdownSeconds><Map><Size>1024</Size><Seed>0</Seed><GemChance>0.04</GemChance><LayoutSizes>128 256</LayoutSizes><StartSites>8 16</StartSites><Expansions>2 4</Expansions><CentralAreas>1 1</CentralAreas><RegionScale>100 100</RegionScale><LaneScale>100 100</LaneScale><CaveDepth>3 5</CaveDepth><OutcropShare>0.08</OutcropShare><ChokepointShare>0.4</ChokepointShare><LaneWidth>6 10</LaneWidth><ChokeWidth>3 6</ChokeWidth><MinStartDistance>0.1</MinStartDistance><MaxStartDistance>0.6</MaxStartDistance><ExpansionGemTiles>30</ExpansionGemTiles></Map></Match>
  <Simulation><TickRate>20</TickRate><TargetSearchSliceTicks>8</TargetSearchSliceTicks><SeparationIntervalTicks>2</SeparationIntervalTicks><SeparationStrength>1</SeparationStrength><HashCellSize>5</HashCellSize><MaxFieldRebuildsPerTick>2</MaxFieldRebuildsPerTick><MaxUnitsPerPlayer>10000</MaxUnitsPerPlayer><MaxEntities>131072</MaxEntities><FogCellSize>2</FogCellSize><VisionIntervalTicks>4</VisionIntervalTicks></Simulation>
  <Replication><CorrectionIntervalTicks>4</CorrectionIntervalTicks><CorrectionThreshold>0.25</CorrectionThreshold><DeltaScale>8</DeltaScale><OffscreenThreshold>2</OffscreenThreshold><OffscreenIntervalTicks>20</OffscreenIntervalTicks><SnapshotBytesPerSecond>262144</SnapshotBytesPerSecond><RouteTolerance>1</RouteTolerance></Replication>
  <Orders><MaxQueued>4</MaxQueued></Orders>
  <Diplomacy><ChangeCooldownSeconds>2</ChangeCooldownSeconds></Diplomacy>
  <Lobby><MapSizes>256 512 1024</MapSizes><StartingResources>500 1000 2500 5000</StartingResources><StartOptions>1 3 4</StartOptions><StartPickSeconds>0 15 45</StartPickSeconds><StartPickDefault>45</StartPickDefault><SpareStartSites>2</SpareStartSites></Lobby>
  <MenuBattle><UnitsPerArmy>1500</UnitsPerArmy><MapSize>512</MapSize><Seed>7</Seed></MenuBattle>
  <Gifting><CooldownSeconds>5</CooldownSeconds></Gifting>
  <Alerts><ThrottleSeconds>10</ThrottleSeconds><AreaTiles>32</AreaTiles><ShowSeconds>8</ShowSeconds></Alerts>
  <Siege><BuildPower>25</BuildPower><DemolishRefund>0.5</DemolishRefund><RepairCostPerHealth>0.2</RepairCostPerHealth><WorkReach>1.6</WorkReach><DigTime>1.5</DigTime><GemPayout>150</GemPayout><WallCost>5</WallCost><WallHealth>150</WallHealth><MaxWallLine>64</MaxWallLine><BlastRadius>2.5</BlastRadius><BlastDamage>200</BlastDamage><RadarClearSeconds>5</RadarClearSeconds><RadarAlertCooldownSeconds>30</RadarAlertCooldownSeconds></Siege>
  <Audio><BattleDeathsPerSecond>3</BattleDeathsPerSecond><BattleHoldSeconds>20</BattleHoldSeconds></Audio>
  <Replays><Enabled>1</Enabled><MaxMegabytes>512</MaxMegabytes><SendKilobytesPerSecond>1024</SendKilobytesPerSecond><KeepCount>50</KeepCount></Replays>
  <DamageTable><Entry attacker=""Tank"" target=""Unit"">1.0</Entry><Entry attacker=""Tank"" target=""Building"">1.0</Entry><Entry attacker=""Tank"" target=""Wall"">0.5</Entry><Entry attacker=""Digger"" target=""Unit"">0.5</Entry><Entry attacker=""Digger"" target=""Wall"">6</Entry></DamageTable>
  <Units>
    <Unit type=""Tank""><Health>100</Health><Damage>10</Damage><Range>5</Range><AttackInterval>1</AttackInterval><MoveSpeed>5</MoveSpeed><UpfrontCost>50</UpfrontCost><RunningCost>2</RunningCost><Radius>0.35</Radius><SizeClass>0</SizeClass><Sight>8</Sight><Armour>Light</Armour><BuildSeconds>1</BuildSeconds></Unit>
    <Unit type=""Builder""><Health>60</Health><Damage>0</Damage><Range>1</Range><AttackInterval>1</AttackInterval><MoveSpeed>5</MoveSpeed><UpfrontCost>60</UpfrontCost><RunningCost>2</RunningCost><Radius>0.35</Radius><SizeClass>0</SizeClass><Sight>6</Sight><Armour>Light</Armour><BuildSeconds>2</BuildSeconds></Unit>
    <Unit type=""Digger""><Health>120</Health><Damage>4</Damage><Range>1.5</Range><AttackInterval>1</AttackInterval><MoveSpeed>4</MoveSpeed><UpfrontCost>120</UpfrontCost><RunningCost>4</RunningCost><Radius>0.4</Radius><SizeClass>0</SizeClass><Sight>6</Sight><Armour>Light</Armour><BuildSeconds>1</BuildSeconds></Unit>
    <Unit type=""Scout""><Health>40</Health><Damage>4</Damage><Range>4</Range><AttackInterval>0.5</AttackInterval><MoveSpeed>9</MoveSpeed><UpfrontCost>30</UpfrontCost><RunningCost>1</RunningCost><Radius>0.3</Radius><SizeClass>0</SizeClass><Sight>12</Sight><Armour>Light</Armour><BuildSeconds>0.7</BuildSeconds></Unit>
    <Unit type=""Heavy""><Health>400</Health><Damage>30</Damage><Range>5</Range><AttackInterval>1.5</AttackInterval><MoveSpeed>2.5</MoveSpeed><UpfrontCost>200</UpfrontCost><RunningCost>6</RunningCost><Radius>0.7</Radius><SizeClass>1</SizeClass><Sight>7</Sight><Armour>Heavy</Armour><BuildSeconds>5</BuildSeconds></Unit>
    <Unit type=""Artillery""><Health>120</Health><Damage>40</Damage><Range>14</Range><AttackInterval>4</AttackInterval><MoveSpeed>2.5</MoveSpeed><UpfrontCost>220</UpfrontCost><RunningCost>6</RunningCost><Radius>0.7</Radius><SizeClass>1</SizeClass><Sight>7</Sight><Armour>Heavy</Armour><BuildSeconds>6</BuildSeconds><Splash>2</Splash><MinRange>4</MinRange></Unit>
    <Unit type=""Sniper""><Health>50</Health><Damage>60</Damage><Range>16</Range><AttackInterval>3</AttackInterval><MoveSpeed>4</MoveSpeed><UpfrontCost>150</UpfrontCost><RunningCost>4</RunningCost><Radius>0.35</Radius><SizeClass>0</SizeClass><Sight>10</Sight><Armour>Light</Armour><BuildSeconds>4</BuildSeconds><MinRange>3</MinRange></Unit>
    <Unit type=""Flamethrower""><Health>90</Health><Damage>6</Damage><Range>2.5</Range><AttackInterval>0.5</AttackInterval><MoveSpeed>4.5</MoveSpeed><UpfrontCost>70</UpfrontCost><RunningCost>2</RunningCost><Radius>0.35</Radius><SizeClass>0</SizeClass><Sight>6</Sight><Armour>Light</Armour><BuildSeconds>1.5</BuildSeconds><Splash>1.5</Splash></Unit>
    <Unit type=""SuicideBomber""><Health>30</Health><Damage>0</Damage><Range>0.8</Range><AttackInterval>1</AttackInterval><MoveSpeed>8</MoveSpeed><UpfrontCost>60</UpfrontCost><RunningCost>1</RunningCost><Radius>0.3</Radius><SizeClass>0</SizeClass><Sight>6</Sight><Armour>Light</Armour><BuildSeconds>1</BuildSeconds><BlastRadius>2</BlastRadius><BlastDamage>150</BlastDamage></Unit>
  </Units>
  <Buildings>
    <Building type=""Base""><Health>500</Health><Width>3</Width><Height>3</Height><UpfrontCost>0</UpfrontCost><RunningCost>0</RunningCost><Sight>4</Sight></Building>
    <Building type=""Miner""><Health>50</Health><Width>1</Width><Height>1</Height><UpfrontCost>50</UpfrontCost><RunningCost>0</RunningCost><Sight>4</Sight></Building>
    <Building type=""Barracks""><Health>300</Health><Width>2</Width><Height>2</Height><UpfrontCost>200</UpfrontCost><RunningCost>20</RunningCost><Sight>4</Sight></Building>
    <Building type=""Bomb""><Health>40</Health><Width>1</Width><Height>1</Height><UpfrontCost>75</UpfrontCost><RunningCost>1</RunningCost><Sight>0</Sight></Building>
    <Building type=""Radar""><Health>200</Health><Width>2</Width><Height>2</Height><UpfrontCost>250</UpfrontCost><RunningCost>5</RunningCost><Sight>20</Sight></Building>
    <Building type=""HeavyFactory""><Health>500</Health><Width>3</Width><Height>3</Height><UpfrontCost>400</UpfrontCost><RunningCost>30</RunningCost><Sight>6</Sight></Building>
    <Building type=""SiegeWorkshop""><Health>300</Health><Width>2</Width><Height>2</Height><UpfrontCost>250</UpfrontCost><RunningCost>20</RunningCost><Sight>6</Sight></Building>
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
        Assert.That(c.GetBuilding(BuildingType.Barracks).UpfrontCost, Is.EqualTo(200f));
        Assert.That(c.GetBuilding(BuildingType.Base).Size.x, Is.EqualTo(3));
    }

    [Test]
    public void ShippedConfig_HasEverySiegeValueAndType()
    {
        ConfigLoader.ResetForTests();
        GameConfigData c = ConfigLoader.LoadConfig();
        Assert.IsTrue(ConfigLoader.IsValid);
        Assert.Greater(c.Siege.BuildPower, 0f);
        Assert.Greater(c.Siege.WorkReach, 0f);
        Assert.AreEqual(0.5f, c.Siege.DemolishRefund, "a completed building refunds half its cost (owner, 2026-10-15)");
        Assert.Greater(c.Siege.DigTime, 0f);
        Assert.Greater(c.Siege.WallHealth, 0);
        Assert.Greater(c.Siege.MaxWallLine, 0);
        Assert.Greater(c.Siege.BlastRadius, 0f);
        Assert.Greater(c.Siege.RadarAlertCooldownSeconds, 0f);
        Assert.AreEqual(0, c.GetUnit(UnitType.Builder).Damage, "Builders don't attack");
        Assert.AreEqual(1, c.GetBuilding(BuildingType.Bomb).Width);
        Assert.AreEqual(0f, c.GetBuilding(BuildingType.Bomb).Sight, "a bomb is never a sight source");
        Assert.Greater(c.GetBuilding(BuildingType.Radar).Sight, c.GetBuilding(BuildingType.Base).Sight);
        Assert.Greater(c.Damage.Multiplier(UnitType.Digger, TargetClass.Wall), c.Damage.Multiplier(UnitType.Digger, TargetClass.LightUnit));
    }

    [Test]
    public void MissingSiegeSection_IsAnError()
    {
        var errors = new List<string>();
        ConfigParser.Parse(Regex.Replace(ValidXml, "<Siege>.*?</Siege>", "", RegexOptions.Singleline), errors);
        Assert.That(errors, Has.Some.Contains("Siege"));
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

    [Test]
    public void AudioParse()
    {
        var errors = new List<string>();
        GameConfigData c = ConfigParser.Parse(ValidXml, errors);
        Assert.That(errors, Is.Empty);
        Assert.AreEqual(3, c.Audio.BattleDeathsPerSecond);
        Assert.AreEqual(20f, c.Audio.BattleHoldSeconds, 1e-6f);
    }

    [TestCase("<BattleDeathsPerSecond>0</BattleDeathsPerSecond>", "BattleDeathsPerSecond")]
    [TestCase("", "BattleHoldSeconds")]
    public void AudioRequiredAndInRange(string replacement, string field)
    {
        string xml = Regex.Replace(ValidXml, $"<{field}>[^<]*</{field}>", replacement);
        var errors = new List<string>();
        ConfigParser.Parse(xml, errors);
        Assert.That(errors, Has.Some.Contains(field));
    }

    [Test]
    public void ReplaysEnabledOutOfRangeIsRefused()
    {
        string xml = Regex.Replace(ValidXml, "<Enabled>[^<]*</Enabled>", "<Enabled>2</Enabled>");
        var errors = new List<string>();
        ConfigParser.Parse(xml, errors);
        Assert.That(errors, Has.Some.Contains("Enabled"));
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
