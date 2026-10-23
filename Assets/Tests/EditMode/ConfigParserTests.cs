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
  <Orders><MaxQueued>4</MaxQueued><MaxDigArea>24</MaxDigArea></Orders>
  <Diplomacy><ChangeCooldownSeconds>2</ChangeCooldownSeconds></Diplomacy>
  <Lobby><MapSizes>256 512 1024</MapSizes><StartingResources>500 1000 2500 5000</StartingResources><StartOptions>1 3 4</StartOptions><StartPickSeconds>0 15 45</StartPickSeconds><StartPickDefault>45</StartPickDefault><SpareStartSites>2</SpareStartSites><Visibility>Friends</Visibility></Lobby>
  <MenuBattle><UnitsPerArmy>1500</UnitsPerArmy><MapSize>512</MapSize><Seed>7</Seed></MenuBattle>
  <Gifting><CooldownSeconds>5</CooldownSeconds></Gifting>
  <Alerts><ThrottleSeconds>10</ThrottleSeconds><AreaTiles>32</AreaTiles><ShowSeconds>8</ShowSeconds></Alerts>
  <Siege><BuildPower>25</BuildPower><DemolishRefund>0.5</DemolishRefund><RepairCostPerHealth>0.2</RepairCostPerHealth><WorkReach>1.6</WorkReach><DigTime>1.5</DigTime><GemPayout>150</GemPayout><WallCost>5</WallCost><WallHealth>150</WallHealth><MaxWallLine>64</MaxWallLine><BlastRadius>2.5</BlastRadius><BlastDamage>200</BlastDamage><RadarClearSeconds>5</RadarClearSeconds><RadarAlertCooldownSeconds>30</RadarAlertCooldownSeconds><JamRadius>12</JamRadius><JamSight>3</JamSight></Siege>
  <Audio><BattleDeathsPerSecond>3</BattleDeathsPerSecond><BattleHoldSeconds>20</BattleHoldSeconds></Audio>
  <Replays><Enabled>1</Enabled><MaxMegabytes>512</MaxMegabytes><SendKilobytesPerSecond>1024</SendKilobytesPerSecond><KeepCount>50</KeepCount></Replays>
  <Ai><Callsigns>Cmdr. A, Maj. B, Capt. C, Col. D, Lt. E, Maj. F, Capt. G, Cmdr. H, Col. I</Callsigns>
    <Difficulty name=""Easy""><ThinkSeconds>1.0</ThinkSeconds><ActionsPerMinute>30</ActionsPerMinute><ReactionSeconds>6</ReactionSeconds><Builders>2</Builders><BuildersPerExpansion>0</BuildersPerExpansion><MaxExpansions>1</MaxExpansions><ExpandAfterSeconds>600</ExpandAfterSeconds><KeepsUpkeepPaid>0</KeepsUpkeepPaid><UpkeepReserveSeconds>0</UpkeepReserveSeconds><Counters>None</Counters><Retreats>0</Retreats><RetreatRatio>0.5</RetreatRatio><FocusFire>0</FocusFire><KeepsBacklineBack>0</KeepsBacklineBack><DigsPastWalls>0</DigsPastWalls><Scouting>Once</Scouting><RescoutSeconds>600</RescoutSeconds><SpecialUse>0.25</SpecialUse><DefendsChokepoints>0</DefendsChokepoints><AttackRatio>1.0</AttackRatio><Peace>Always</Peace><GiftsSurplus>0</GiftsSurplus></Difficulty>
    <Difficulty name=""Normal""><ThinkSeconds>0.5</ThinkSeconds><ActionsPerMinute>80</ActionsPerMinute><ReactionSeconds>2</ReactionSeconds><Builders>4</Builders><BuildersPerExpansion>1</BuildersPerExpansion><MaxExpansions>2</MaxExpansions><ExpandAfterSeconds>300</ExpandAfterSeconds><KeepsUpkeepPaid>1</KeepsUpkeepPaid><UpkeepReserveSeconds>20</UpkeepReserveSeconds><Counters>Seen</Counters><Retreats>1</Retreats><RetreatRatio>0.6</RetreatRatio><FocusFire>0</FocusFire><KeepsBacklineBack>0</KeepsBacklineBack><DigsPastWalls>0</DigsPastWalls><Scouting>Regular</Scouting><RescoutSeconds>120</RescoutSeconds><SpecialUse>1</SpecialUse><DefendsChokepoints>0</DefendsChokepoints><AttackRatio>1.2</AttackRatio><Peace>UnlessWinning</Peace><GiftsSurplus>1</GiftsSurplus></Difficulty>
    <Difficulty name=""Hard""><ThinkSeconds>0.25</ThinkSeconds><ActionsPerMinute>200</ActionsPerMinute><ReactionSeconds>0.5</ReactionSeconds><Builders>5</Builders><BuildersPerExpansion>2</BuildersPerExpansion><MaxExpansions>3</MaxExpansions><ExpandAfterSeconds>180</ExpandAfterSeconds><KeepsUpkeepPaid>1</KeepsUpkeepPaid><UpkeepReserveSeconds>30</UpkeepReserveSeconds><Counters>SeenAndBuildings</Counters><Retreats>1</Retreats><RetreatRatio>0.7</RetreatRatio><FocusFire>1</FocusFire><KeepsBacklineBack>1</KeepsBacklineBack><DigsPastWalls>1</DigsPastWalls><Scouting>RegularWithRadar</Scouting><RescoutSeconds>60</RescoutSeconds><SpecialUse>1</SpecialUse><DefendsChokepoints>1</DefendsChokepoints><AttackRatio>1.3</AttackRatio><Peace>WhenLosingOrThirdParty</Peace><GiftsSurplus>1</GiftsSurplus></Difficulty>
    <Style name=""Rusher""><BuildOrder>Miner Barracks Scout Tank</BuildOrder><ArmyMix>Scout:2 Tank:6</ArmyMix><Producers>Barracks:2 HeavyFactory:1</Producers><Miners>4</Miners><MinersPerExpansion>3</MinersPerExpansion><Expansions>1</Expansions><ExpandTiming>1</ExpandTiming><FirstAttackSeconds>200</FirstAttackSeconds><AttackArmySize>10</AttackArmySize><AttackRatioScale>1</AttackRatioScale><AttackIntervalSeconds>90</AttackIntervalSeconds><Turrets>2</Turrets><Jammers>1</Jammers><Bombs>2</Bombs><Walls>0</Walls><Radar>1</Radar><HoldsChokes>0</HoldsChokes></Style>
    <Style name=""Turtle""><BuildOrder>Miner GunTurret HeavyFactory Heavy</BuildOrder><ArmyMix>Heavy:4 Artillery:4</ArmyMix><Producers>Barracks:1 HeavyFactory:2</Producers><Miners>4</Miners><MinersPerExpansion>3</MinersPerExpansion><Expansions>1</Expansions><ExpandTiming>1</ExpandTiming><FirstAttackSeconds>200</FirstAttackSeconds><AttackArmySize>10</AttackArmySize><AttackRatioScale>1</AttackRatioScale><AttackIntervalSeconds>90</AttackIntervalSeconds><Turrets>2</Turrets><Jammers>1</Jammers><Bombs>2</Bombs><Walls>0</Walls><Radar>1</Radar><HoldsChokes>0</HoldsChokes></Style>
    <Style name=""Expander""><BuildOrder>Miner Miner Builder Barracks</BuildOrder><ArmyMix>Tank:5 Heavy:2</ArmyMix><Producers>Barracks:2</Producers><Miners>4</Miners><MinersPerExpansion>3</MinersPerExpansion><Expansions>1</Expansions><ExpandTiming>1</ExpandTiming><FirstAttackSeconds>200</FirstAttackSeconds><AttackArmySize>10</AttackArmySize><AttackRatioScale>1</AttackRatioScale><AttackIntervalSeconds>90</AttackIntervalSeconds><Turrets>2</Turrets><Jammers>1</Jammers><Bombs>2</Bombs><Walls>0</Walls><Radar>1</Radar><HoldsChokes>0</HoldsChokes></Style>
    <Style name=""Balanced""><BuildOrder>Miner Barracks Tank</BuildOrder><ArmyMix>Tank:4 Heavy:2 Digger:1 SuicideBomber:1</ArmyMix><Producers>Barracks:2 HeavyFactory:1 SiegeWorkshop:1</Producers><Miners>4</Miners><MinersPerExpansion>3</MinersPerExpansion><Expansions>1</Expansions><ExpandTiming>1</ExpandTiming><FirstAttackSeconds>200</FirstAttackSeconds><AttackArmySize>10</AttackArmySize><AttackRatioScale>1</AttackRatioScale><AttackIntervalSeconds>90</AttackIntervalSeconds><Turrets>2</Turrets><Jammers>1</Jammers><Bombs>2</Bombs><Walls>0</Walls><Radar>1</Radar><HoldsChokes>0</HoldsChokes></Style>
    <Diplomacy><PeaceSeconds>60</PeaceSeconds><GrudgeSeconds>180</GrudgeSeconds><WinningRatio>1.5</WinningRatio><LosingRatio>0.8</LosingRatio><GiftSurplus>3000</GiftSurplus><GiftSurplusSeconds>60</GiftSurplusSeconds><GiftShare>0.25</GiftShare><GiftPeaceFactor>0.5</GiftPeaceFactor></Diplomacy>
    <Concede><MinMinutes>10</MinMinutes><Seconds>60</Seconds><Ratio>0.1</Ratio><SeenWindowSeconds>120</SeenWindowSeconds><IncomeSeconds>60</IncomeSeconds></Concede><Brain><BudgetBurstSeconds>5</BudgetBurstSeconds><ProposalWaitSeconds>10</ProposalWaitSeconds><ThreatRadius>14</ThreatRadius><ArmyMemorySeconds>180</ArmyMemorySeconds><EventMemorySeconds>30</EventMemorySeconds></Brain><Play><OptionRadius>18</OptionRadius><OptionGemWeight>1</OptionGemWeight><OptionOpenWeight>0.05</OptionOpenWeight><OptionExpansionWeight>40</OptionExpansionWeight><OptionExpansionReach>140</OptionExpansionReach><OptionTeammateWeight>0.3</OptionTeammateWeight><OpeningSeconds>300</OpeningSeconds><BaseRadius>24</BaseRadius><BuildingGap>1</BuildingGap><PlacementTriesPerThink>600</PlacementTriesPerThink><PendingSeconds>5</PendingSeconds><BlockedSeconds>60</BlockedSeconds><SearchRetrySeconds>15</SearchRetrySeconds><ReplanSeconds>30</ReplanSeconds><DefenceSpread>6</DefenceSpread><BuildersPerBlueprint>2</BuildersPerBlueprint><AssignSeconds>4</AssignSeconds><RepairBelow>0.6</RepairBelow><QueueAhead>2</QueueAhead><QueueBatch>10</QueueBatch><UpkeepIncomeShare>0.8</UpkeepIncomeShare><UpkeepFloorSeconds>3</UpkeepFloorSeconds><UpkeepBankSeconds>120</UpkeepBankSeconds><UpkeepHeadroom>25</UpkeepHeadroom><CappedExpandResources>600</CappedExpandResources><GatherDistance>12</GatherDistance><ArmyRadius>14</ArmyRadius><OrderSeconds>3</OrderSeconds><ReinforceSize>4</ReinforceSize><DefendRadius>40</DefendRadius><CounterShift>0.5</CounterShift><BuildingCounterWeight>0.5</BuildingCounterWeight><LightTurretShare>0.6</LightTurretShare><BacklineDistance>6</BacklineDistance><BomberClump>3</BomberClump><BomberChecks>16</BomberChecks><ScoutAfterSeconds>20</ScoutAfterSeconds><DefenceAfterSeconds>240</DefenceAfterSeconds><DefenceDistance>3</DefenceDistance><MaxDefendedSpots>2</MaxDefendedSpots><WallGap>2</WallGap><RetreatSeconds>15</RetreatSeconds><TurretStrength>2</TurretStrength></Play><Counters><Counter seen=""Heavy"">Sniper:2 Artillery:2</Counter><Counter building=""HeavyFactory"">Sniper:2</Counter></Counters></Ai>
  <DamageTable><Entry attacker=""Tank"" target=""Unit"">1.0</Entry><Entry attacker=""Tank"" target=""Building"">1.0</Entry><Entry attacker=""Tank"" target=""Wall"">0.5</Entry><Entry attacker=""Digger"" target=""Unit"">0.5</Entry><Entry attacker=""Digger"" target=""Wall"">6</Entry><Entry attacker=""GunTurret"" target=""Unit"">1.5</Entry><Entry attacker=""GunTurret"" target=""HeavyUnit"">0.4</Entry><Entry attacker=""GunTurret"" target=""Building"">0.5</Entry><Entry attacker=""GunTurret"" target=""Wall"">0.5</Entry></DamageTable>
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
    <Building type=""GunTurret""><Health>350</Health><Width>1</Width><Height>1</Height><UpfrontCost>150</UpfrontCost><RunningCost>4</RunningCost><Sight>8</Sight><Damage>8</Damage><Range>7</Range><AttackInterval>0.4</AttackInterval></Building>
    <Building type=""Jammer""><Health>200</Health><Width>2</Width><Height>2</Height><UpfrontCost>200</UpfrontCost><RunningCost>6</RunningCost><Sight>6</Sight></Building>
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
        Assert.AreEqual(24, c.Orders.MaxDigArea);
    }

    [TestCase("<MaxDigArea>0</MaxDigArea>")]
    [TestCase("<MaxDigArea>65</MaxDigArea>")]
    [TestCase("")]
    public void OrdersMaxDigAreaRequiredAndInRange(string replacement)
    {
        string xml = Regex.Replace(ValidXml, "<MaxDigArea>[^<]*</MaxDigArea>", replacement);
        var errors = new List<string>();
        ConfigParser.Parse(xml, errors);
        Assert.That(errors, Has.Some.Contains("MaxDigArea"));
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

    // ---- v0.13 Bastion ----

    private const string TurretWeapon = "<Damage>8</Damage><Range>7</Range><AttackInterval>0.4</AttackInterval>";

    [Test]
    public void BuildingWeaponsAndTheJamParse()
    {
        var errors = new List<string>();
        GameConfigData c = ConfigParser.Parse(ValidXml, errors);
        Assert.That(errors, Is.Empty);
        BuildingConfig turret = c.GetBuilding(BuildingType.GunTurret);
        Assert.AreEqual((8, 7f, 0.4f), (turret.Damage, turret.Range, turret.AttackInterval));
        Assert.IsTrue(turret.IsAttacker);
        Assert.IsFalse(c.GetBuilding(BuildingType.Jammer).IsAttacker, "no weapon block: not an attacker");
        Assert.AreEqual(0, c.GetBuilding(BuildingType.Radar).Damage);
        Assert.AreEqual((12f, 3f), (c.Siege.JamRadius, c.Siege.JamSight));
    }

    [TestCase("<Damage>8</Damage><Range>7</Range>")]
    [TestCase("<Damage>8</Damage>")]
    [TestCase("<Range>7</Range><AttackInterval>0.4</AttackInterval>")]
    public void APartialWeaponBlockIsAnError(string partial)
    {
        var errors = new List<string>();
        ConfigParser.Parse(ValidXml.Replace(TurretWeapon, partial), errors);
        Assert.That(errors, Has.Some.Matches<string>(e => e.Contains("GunTurret") && e.Contains("all of")));
    }

    [TestCase("<Damage>8</Damage><Range>0</Range><AttackInterval>0.4</AttackInterval>", "Range")]
    [TestCase("<Damage>8</Damage><Range>7</Range><AttackInterval>0</AttackInterval>", "AttackInterval")]
    [TestCase("<Damage>8</Damage><Range>-1</Range><AttackInterval>0.4</AttackInterval>", "Range")]
    public void AnAttackerNeedsAPositiveRangeAndInterval(string weapon, string field)
    {
        var errors = new List<string>();
        ConfigParser.Parse(ValidXml.Replace(TurretWeapon, weapon), errors);
        Assert.That(errors, Has.Some.Contains(field));
    }

    [Test]
    public void AZeroDamageWeaponIsNotAnAttacker()
    {
        var errors = new List<string>();
        string xml = ValidXml.Replace(TurretWeapon, "<Damage>0</Damage><Range>0</Range><AttackInterval>0</AttackInterval>");
        xml = Regex.Replace(xml, @"<Entry attacker=""GunTurret""[^>]*>[^<]*</Entry>", "");
        GameConfigData c = ConfigParser.Parse(xml, errors);
        Assert.That(errors, Is.Empty);
        Assert.IsFalse(c.GetBuilding(BuildingType.GunTurret).IsAttacker);
    }

    [Test]
    public void ABuildingAttackerWithoutATableRowIsAnError()
    {
        var errors = new List<string>();
        ConfigParser.Parse(Regex.Replace(ValidXml, @"<Entry attacker=""GunTurret""[^>]*>[^<]*</Entry>", ""), errors);
        Assert.That(errors, Has.Some.Contains("building attacker GunTurret"));
    }

    [Test]
    public void ARowForABuildingThatDoesNotAttackIsAnError()
    {
        var errors = new List<string>();
        ConfigParser.Parse(ValidXml.Replace("<DamageTable>", @"<DamageTable><Entry attacker=""Jammer"" target=""Unit"">1</Entry>"), errors);
        Assert.That(errors, Has.Some.Matches<string>(e => e.Contains("Jammer") && e.Contains("does not attack")));
    }

    [Test]
    public void TheUnitShorthandWorksForBuildingRowsAndAnExplicitClassWins()
    {
        var errors = new List<string>();
        GameConfigData c = ConfigParser.Parse(ValidXml, errors); // GunTurret: "Unit" 1.5, then HeavyUnit 0.4
        Assert.That(errors, Is.Empty);
        Assert.AreEqual(1.5f, c.Damage.Multiplier(BuildingType.GunTurret, TargetClass.LightUnit), 1e-6f);
        Assert.AreEqual(0.4f, c.Damage.Multiplier(BuildingType.GunTurret, TargetClass.HeavyUnit), 1e-6f, "the explicit class beats the shorthand");
        Assert.AreEqual(0.5f, c.Damage.Multiplier(BuildingType.GunTurret, TargetClass.Wall), 1e-6f);
        Assert.AreEqual(1f, c.Damage.Multiplier(UnitType.Scout, TargetClass.LightUnit), "building rows don't touch unit rows");
    }

    [TestCase("<JamSight>12</JamSight>", "JamSight")]  // not less than the radius
    [TestCase("<JamSight>0</JamSight>", "JamSight")]
    [TestCase("", "JamSight")]
    public void JamSightIsRequiredAndBelowTheRadius(string replacement, string field)
    {
        var errors = new List<string>();
        ConfigParser.Parse(Regex.Replace(ValidXml, "<JamSight>[^<]*</JamSight>", replacement), errors);
        Assert.That(errors, Has.Some.Contains(field));
    }

    [TestCase("<JamRadius>0</JamRadius>")]
    [TestCase("")]
    public void JamRadiusIsRequiredAndPositive(string replacement)
    {
        var errors = new List<string>();
        ConfigParser.Parse(Regex.Replace(ValidXml, "<JamRadius>[^<]*</JamRadius>", replacement), errors);
        Assert.That(errors, Has.Some.Contains("JamRadius"));
    }

    [Test]
    public void ShippedConfigHasTheBastionSpecNumbers()
    {
        ConfigLoader.ResetForTests();
        GameConfigData c = ConfigLoader.LoadConfig();
        Assert.IsTrue(ConfigLoader.IsValid);
        BuildingConfig turret = c.GetBuilding(BuildingType.GunTurret);
        Assert.AreEqual((350, 1, 1, 150f, 4f, 8f), (turret.Health, turret.Width, turret.Height, turret.UpfrontCost, turret.RunningCost, turret.Sight));
        Assert.AreEqual((8, 7f, 0.4f), (turret.Damage, turret.Range, turret.AttackInterval));
        BuildingConfig jammer = c.GetBuilding(BuildingType.Jammer);
        Assert.AreEqual((200, 2, 2, 200f, 6f, 6f), (jammer.Health, jammer.Width, jammer.Height, jammer.UpfrontCost, jammer.RunningCost, jammer.Sight));
        Assert.IsFalse(jammer.IsAttacker);
        Assert.AreEqual((12f, 3f), (c.Siege.JamRadius, c.Siege.JamSight));
        Assert.AreEqual(1.5f, c.Damage.Multiplier(BuildingType.GunTurret, TargetClass.LightUnit));
        Assert.AreEqual(0.4f, c.Damage.Multiplier(BuildingType.GunTurret, TargetClass.HeavyUnit));
        Assert.AreEqual(0.5f, c.Damage.Multiplier(BuildingType.GunTurret, TargetClass.Building));
        Assert.AreEqual(0.5f, c.Damage.Multiplier(BuildingType.GunTurret, TargetClass.Wall));
    }

    // ---------- v0.14 AI ----------

    [Test]
    public void AiBlockParses()
    {
        var errors = new List<string>();
        GameConfigData c = ConfigParser.Parse(ValidXml, errors);
        Assert.That(errors, Is.Empty);
        Assert.That(c.Ai.Callsigns, Has.Length.EqualTo(9));
        Assert.That(c.Ai.Callsigns[0], Is.EqualTo("Cmdr. A"));
        Assert.That(c.Ai.GetDifficulty(AiDifficulty.Hard).ThinkSeconds, Is.EqualTo(0.25f));
        Assert.That(c.Ai.GetDifficulty(AiDifficulty.Hard).Counters, Is.EqualTo(AiCounters.SeenAndBuildings));
        Assert.That(c.Ai.GetDifficulty(AiDifficulty.Normal).KeepsUpkeepPaid, Is.True);
        Assert.That(c.Ai.GetDifficulty(AiDifficulty.Easy).Peace, Is.EqualTo(AiPeace.Always));
        AiStyleConfig turtle = c.Ai.GetStyle(AiStyle.Turtle);
        Assert.That(turtle.BuildOrder[1].Building, Is.EqualTo(BuildingType.GunTurret));
        Assert.That(turtle.BuildOrder[3].Unit, Is.EqualTo(UnitType.Heavy));
        Assert.That(turtle.ArmyMix[UnitType.Artillery], Is.EqualTo(4f));
        Assert.That(turtle.Producers[BuildingType.HeavyFactory], Is.EqualTo(2));
        Assert.That(c.Ai.Diplomacy.GrudgeSeconds, Is.EqualTo(180f));
        Assert.That(c.Ai.Concede.Ratio, Is.EqualTo(0.1f));
        Assert.That(c.Lobby.Visibility, Is.EqualTo(LobbyVisibility.Friends));
        Assert.Throws<KeyNotFoundException>(() => c.Ai.GetDifficulty(AiDifficulty.Scripted), "Scripted has no settings");
        Assert.Throws<KeyNotFoundException>(() => c.Ai.GetStyle(AiStyle.Random), "Random is resolved before use");
    }

    [TestCase("Easy")]
    [TestCase("Normal")]
    [TestCase("Hard")]
    public void AMissingDifficultyIsAnError(string name)
    {
        var errors = new List<string>();
        ConfigParser.Parse(Regex.Replace(ValidXml, $"<Difficulty name=\"{name}\">.*?</Difficulty>", ""), errors);
        Assert.That(errors, Has.Some.Contains($"missing <Difficulty name=\"{name}\">"));
    }

    [TestCase("Rusher")]
    [TestCase("Turtle")]
    [TestCase("Expander")]
    [TestCase("Balanced")]
    public void AMissingStyleIsAnError(string name)
    {
        var errors = new List<string>();
        ConfigParser.Parse(Regex.Replace(ValidXml, $"<Style name=\"{name}\">.*?</Style>", ""), errors);
        Assert.That(errors, Has.Some.Contains($"missing <Style name=\"{name}\">"));
    }

    [TestCase("<Difficulty name=\"Easy\">", "<Difficulty name=\"Scripted\">", "unknown difficulty")]
    [TestCase("<Style name=\"Rusher\">", "<Style name=\"Random\">", "unknown style")]
    [TestCase("<Peace>Always</Peace>", "<Peace>Sometimes</Peace>", "Peace")]
    [TestCase("<Counters>None</Counters>", "<Counters>2</Counters>", "Counters")]
    [TestCase("<Retreats>0</Retreats>", "<Retreats>2</Retreats>", "Retreats")]
    [TestCase("<ActionsPerMinute>30</ActionsPerMinute>", "<ActionsPerMinute>0</ActionsPerMinute>", "ActionsPerMinute")]
    [TestCase("<ArmyMix>Scout:2 Tank:6</ArmyMix>", "<ArmyMix>Builder:2</ArmyMix>", "ArmyMix")]
    [TestCase("<ArmyMix>Scout:2 Tank:6</ArmyMix>", "<ArmyMix>Tank:0</ArmyMix>", "ArmyMix")]
    [TestCase("<ArmyMix>Scout:2 Tank:6</ArmyMix>", "<ArmyMix>Tank</ArmyMix>", "ArmyMix")]
    [TestCase("<Producers>Barracks:2 HeavyFactory:1</Producers>", "<Producers>Miner:2</Producers>", "Producers")]
    [TestCase("<Producers>Barracks:2 HeavyFactory:1</Producers>", "<Producers>Barracks:1.5</Producers>", "Producers")]
    [TestCase("<BuildOrder>Miner Barracks Scout Tank</BuildOrder>", "<BuildOrder>Miner Base</BuildOrder>", "BuildOrder")]
    [TestCase("<BuildOrder>Miner Barracks Scout Tank</BuildOrder>", "<BuildOrder>Miner Castle</BuildOrder>", "BuildOrder")]
    [TestCase("<GrudgeSeconds>180</GrudgeSeconds>", "", "GrudgeSeconds")]
    [TestCase("<Ratio>0.1</Ratio>", "<Ratio>2</Ratio>", "Ratio")]
    [TestCase("<Visibility>Friends</Visibility>", "<Visibility>Everyone</Visibility>", "Visibility")]
    [TestCase("<Visibility>Friends</Visibility>", "", "Visibility")]
    [TestCase("<QueueBatch>10</QueueBatch>", "<QueueBatch>7</QueueBatch>", "QueueBatch")]
    [TestCase("<QueueBatch>10</QueueBatch>", "", "QueueBatch")]
    [TestCase("<CounterShift>0.5</CounterShift>", "<CounterShift>1.5</CounterShift>", "CounterShift")]
    [TestCase("<BaseRadius>24</BaseRadius>", "<BaseRadius>0</BaseRadius>", "BaseRadius")]
    [TestCase("<HoldsChokes>0</HoldsChokes>", "<HoldsChokes>2</HoldsChokes>", "HoldsChokes")]
    [TestCase("<DigsPastWalls>0</DigsPastWalls>", "", "DigsPastWalls")]
    [TestCase("<Counter seen=\"Heavy\">Sniper:2 Artillery:2</Counter>", "<Counter seen=\"Heavy\">Builder:2</Counter>", "combat unit")]
    [TestCase("<Counter seen=\"Heavy\">Sniper:2 Artillery:2</Counter>", "<Counter seen=\"Castle\">Sniper:2</Counter>", "not a unit type")]
    [TestCase("<Counter building=\"HeavyFactory\">Sniper:2</Counter>", "<Counter>Sniper:2</Counter>", "exactly one")]
    [TestCase("<Counter building=\"HeavyFactory\">Sniper:2</Counter>", "<Counter building=\"HeavyFactory\">Sniper:0</Counter>", "weight")]
    public void BadAiPlayValuesAreErrors(string from, string to, string expected) => BadAiValuesAreErrors(from, to, expected);

    [Test]
    public void TheAiPlayAndCountersBlocksParse()
    {
        var errors = new List<string>();
        GameConfigData c = ConfigParser.Parse(ValidXml, errors);
        Assert.That(errors, Is.Empty, string.Join("\n", errors));
        Assert.That(c.Ai.Play.QueueBatch, Is.EqualTo(10));
        Assert.That(c.Ai.Play.BaseRadius, Is.EqualTo(24f));
        Assert.That(c.Ai.SeenCounters[UnitType.Heavy][UnitType.Sniper], Is.EqualTo(2f));
        Assert.That(c.Ai.BuildingCounters[BuildingType.HeavyFactory][UnitType.Sniper], Is.EqualTo(2f));
        Assert.That(c.Ai.GetDifficulty(AiDifficulty.Hard).DigsPastWalls, Is.True);
        errors.Clear();
        ConfigParser.Parse(Regex.Replace(ValidXml, "<Play>.*?</Play>", "", RegexOptions.Singleline), errors);
        Assert.That(errors, Has.Some.Contains("Play"), "the block is required");
    }

    [Test]
    public void ShippedConfigHasEveryAiPlayNumber()
    {
        ConfigLoader.ResetForTests();
        GameConfigData c = ConfigLoader.LoadConfig();
        Assert.That(ConfigLoader.IsValid, Is.True, string.Join("\n", ConfigLoader.Errors));
        Assert.That(c.Ai.GetStyle(AiStyle.Turtle).HoldsChokes, Is.True);
        Assert.That(c.Ai.SeenCounters.ContainsKey(UnitType.Heavy), Is.True, "Heavies seen: Snipers and Artillery (spec 5.5)");
        Assert.That(c.Ai.SeenCounters[UnitType.Heavy].ContainsKey(UnitType.Sniper) && c.Ai.SeenCounters[UnitType.Heavy].ContainsKey(UnitType.Artillery), Is.True);
        Assert.That(c.Ai.BuildingCounters.ContainsKey(BuildingType.HeavyFactory), Is.True, "a Heavy Factory ghost: anti-Heavy (Hard)");
        Assert.That(SpawnerRules.IsQueueStep(c.Ai.Play.QueueBatch), Is.True);
        ConfigLoader.ResetForTests();
    }

    public void BadAiValuesAreErrors(string from, string to, string expected)
    {
        var errors = new List<string>();
        Assert.That(ValidXml, Does.Contain(from), "the test's anchor");
        ConfigParser.Parse(ReplaceFirst(ValidXml, from, to), errors);
        Assert.That(errors, Has.Some.Contains(expected));
    }

    [Test]
    public void DuplicateCallsignsAreAnError()
    {
        var errors = new List<string>();
        ConfigParser.Parse(ValidXml.Replace("Maj. B,", "Cmdr. a,"), errors);
        Assert.That(errors, Has.Some.Contains("listed twice"));
    }

    [Test]
    public void TooFewCallsignsAreAnError()
    {
        var errors = new List<string>();
        ConfigParser.Parse(Regex.Replace(ValidXml, "<Callsigns>[^<]*</Callsigns>", "<Callsigns>Cmdr. A, Maj. B</Callsigns>"), errors);
        Assert.That(errors, Has.Some.Contains($"at least {ConfigParser.MinCallsigns}"));
    }

    [Test]
    public void ACallsignLongerThanANicknameIsAnError()
    {
        var errors = new List<string>();
        ConfigParser.Parse(ValidXml.Replace("Cmdr. A,", "Commander Alexandra Vasquez-Okafor,"), errors);
        Assert.That(errors, Has.Some.Contains("valid nickname"));
    }

    [Test]
    public void AMissingAiBlockIsAnError()
    {
        var errors = new List<string>();
        ConfigParser.Parse(Regex.Replace(ValidXml, "<Ai>.*?</Ai>", "", RegexOptions.Singleline), errors);
        Assert.That(errors, Has.Some.Contains("Missing <Ai> section"));
    }

    [Test]
    public void TheAiBrainBlockIsRequiredAndRangeChecked()
    {
        var errors = new List<string>();
        ConfigParser.Parse(Regex.Replace(ValidXml, "<Brain>.*?</Brain>", "", RegexOptions.Singleline), errors);
        Assert.That(errors, Has.Some.Contains("Brain"), "the block is required");
        errors.Clear();
        ConfigParser.Parse(Regex.Replace(ValidXml, "<ThreatRadius>[^<]*</ThreatRadius>", "<ThreatRadius>0</ThreatRadius>"), errors);
        Assert.That(errors, Has.Some.Contains("ThreatRadius"));
    }

    [Test]
    public void ShippedConfigHasTheAiSpecNumbers()
    {
        ConfigLoader.ResetForTests();
        GameConfigData c = ConfigLoader.LoadConfig();
        Assert.IsTrue(ConfigLoader.IsValid, string.Join("\n", ConfigLoader.Errors));
        Assert.That(c.Ai.Callsigns.Length, Is.GreaterThanOrEqualTo(ConfigParser.MinCallsigns));
        Assert.That(c.Ai.Callsigns, Does.Contain("Cmdr. Vasquez"));
        // Spec §6's starting values.
        Assert.AreEqual((1.0f, 30, 6.0f), Row(c.Ai.GetDifficulty(AiDifficulty.Easy)));
        Assert.AreEqual((0.5f, 80, 2.0f), Row(c.Ai.GetDifficulty(AiDifficulty.Normal)));
        Assert.AreEqual((0.25f, 200, 0.5f), Row(c.Ai.GetDifficulty(AiDifficulty.Hard)));
        Assert.IsFalse(c.Ai.GetDifficulty(AiDifficulty.Easy).KeepsUpkeepPaid, "Easy may go unpaid");
        Assert.IsTrue(c.Ai.GetDifficulty(AiDifficulty.Normal).KeepsUpkeepPaid && c.Ai.GetDifficulty(AiDifficulty.Hard).KeepsUpkeepPaid, "Normal and Hard never");
        Assert.AreEqual((60f, 180f), (c.Ai.Diplomacy.PeaceSeconds, c.Ai.Diplomacy.GrudgeSeconds));
        Assert.AreEqual((10f, 60f, 0.1f), (c.Ai.Concede.MinMinutes, c.Ai.Concede.Seconds, c.Ai.Concede.Ratio));
        Assert.AreEqual((5f, 10f, 14f), (c.Ai.Brain.BudgetBurstSeconds, c.Ai.Brain.ProposalWaitSeconds, c.Ai.Brain.ThreatRadius));
        Assert.AreEqual((3000f, 0.25f, 0.5f), (c.Ai.Diplomacy.GiftSurplus, c.Ai.Diplomacy.GiftShare, c.Ai.Diplomacy.GiftPeaceFactor));
        foreach (AiStyle style in new[] { AiStyle.Rusher, AiStyle.Turtle, AiStyle.Expander, AiStyle.Balanced })
        {
            AiStyleConfig s = c.Ai.GetStyle(style);
            Assert.That(s.BuildOrder, Is.Not.Empty, $"{style} has an opening");
            foreach (UnitType unit in s.ArmyMix.Keys)
            {
                bool made = false;
                foreach (var producer in s.Producers) if (producer.Value > 0 && SpawnerRules.MayProduce(producer.Key, unit)) made = true;
                Assert.IsTrue(made, $"{style} keeps a producer for every unit in its mix ({unit})");
            }
        }
        Assert.That(c.Lobby.Visibility, Is.EqualTo(LobbyVisibility.Friends), "Friends is today's behaviour and the default");
    }

    private static (float, int, float) Row(AiDifficultyConfig d) => (d.ThinkSeconds, d.ActionsPerMinute, d.ReactionSeconds);

    private static string ReplaceFirst(string text, string from, string to)
    {
        int at = text.IndexOf(from, System.StringComparison.Ordinal);
        return at < 0 ? text : text.Substring(0, at) + to + text.Substring(at + from.Length);
    }
}
