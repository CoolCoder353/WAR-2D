using System.Collections.Generic;

/// <summary>How well an AI player plays (v0.14). <see cref="Scripted"/> is the dev-only seat with no brain that the perf harness and tests drive.</summary>
public enum AiDifficulty : byte
{
    Easy,
    Normal,
    Hard,
    /// <summary>Dev only (<c>DevApi.Allowed</c>): a seat with no brain, driven by the perf harness, the control API and tests.</summary>
    Scripted,
}

/// <summary>How an AI player plays (v0.14). <see cref="Random"/> is resolved privately by the server at match start.</summary>
public enum AiStyle : byte
{
    Random,
    Rusher,
    Turtle,
    Expander,
    Balanced,
}

/// <summary>
/// Who may join the host's lobby (v0.14): a public or friends-only Steam lobby, or a private match nobody can join.
/// Friends is 0 so a default <see cref="MatchSettings"/> (dev starts, older replay headers) keeps today's behaviour.
/// </summary>
public enum LobbyVisibility : byte
{
    Public = 1,
    Friends = 0,
    Private = 2,
}

namespace Config
{
    /// <summary>How an AI scouts (spec §6).</summary>
    public enum AiScouting : byte
    {
        /// <summary>One scouting run at the start.</summary>
        Once,
        /// <summary>Re-scouts known bases and expansions every <see cref="AiDifficultyConfig.RescoutSeconds"/>.</summary>
        Regular,
        /// <summary>Regular, plus a Radar whatever the style.</summary>
        RegularWithRadar,
    }

    /// <summary>When an AI accepts peace from a non-teammate (spec §7).</summary>
    public enum AiPeace : byte
    {
        /// <summary>Always.</summary>
        Always,
        /// <summary>Unless it is clearly winning against them (<see cref="AiDiplomacyConfig.WinningRatio"/>).</summary>
        UnlessWinning,
        /// <summary>Only when it is losing against them (<see cref="AiDiplomacyConfig.LosingRatio"/>) or both fight a third player.</summary>
        WhenLosingOrThirdParty,
    }

    /// <summary>The AI players (v0.14, &lt;Ai&gt;): callsigns, difficulties, styles, diplomacy and conceding.</summary>
    public class AiConfig
    {
        /// <summary>Callsigns AIs are named from, unique within a match (at least <see cref="ConfigParser.MinCallsigns"/>).</summary>
        public string[] Callsigns = System.Array.Empty<string>();
        /// <summary>
        /// Easy, Normal and Hard as the managers play them (Scripted has no brain and no settings): the policy resolved with
        /// each difficulty's handicaps (AI overhaul §2, <see cref="AiPolicy.Resolve"/>).
        /// </summary>
        public Dictionary<AiDifficulty, AiDifficultyConfig> Difficulties = new Dictionary<AiDifficulty, AiDifficultyConfig>();
        /// <summary>Each difficulty's handicaps, as &lt;Difficulty&gt; lists them (AI overhaul §2.1).</summary>
        public Dictionary<AiDifficulty, AiHandicapConfig> Handicaps = new Dictionary<AiDifficulty, AiHandicapConfig>();
        /// <summary>The one policy every difficulty plays (Hard unhandicapped; AI overhaul §2).</summary>
        public AiPolicyConfig Policy = new AiPolicyConfig();
        /// <summary>Rusher, Turtle, Expander and Balanced (Random resolves to one of them).</summary>
        public Dictionary<AiStyle, AiStyleConfig> Styles = new Dictionary<AiStyle, AiStyleConfig>();
        public AiDiplomacyConfig Diplomacy = new AiDiplomacyConfig();
        public AiConcedeConfig Concede = new AiConcedeConfig();
        public AiBrainConfig Brain = new AiBrainConfig();
        /// <summary>How every AI plays, whatever its difficulty and style (plan Task 7's managers).</summary>
        public AiPlayConfig Play = new AiPlayConfig();
        /// <summary>
        /// Counters (spec §5.5): per enemy unit type seen, the combat units weighted against it (Normal and Hard)...
        /// </summary>
        public Dictionary<UnitType, Dictionary<UnitType, float>> SeenCounters = new Dictionary<UnitType, Dictionary<UnitType, float>>();
        /// <summary>... and per enemy building type seen, the units weighted against what it makes (Hard).</summary>
        public Dictionary<BuildingType, Dictionary<UnitType, float>> BuildingCounters = new Dictionary<BuildingType, Dictionary<UnitType, float>>();

        /// <summary>A difficulty's settings. Throws for Scripted or a missing entry (validation guarantees Easy, Normal and Hard).</summary>
        public AiDifficultyConfig GetDifficulty(AiDifficulty difficulty)
        {
            if (Difficulties.TryGetValue(difficulty, out AiDifficultyConfig config)) return config;
            throw new KeyNotFoundException($"GameConfig.xml has no <Ai><Difficulty name=\"{difficulty}\">.");
        }

        /// <summary>A style's settings. Throws for Random (resolve it first) or a missing entry.</summary>
        public AiStyleConfig GetStyle(AiStyle style)
        {
            if (Styles.TryGetValue(style, out AiStyleConfig config)) return config;
            throw new KeyNotFoundException($"GameConfig.xml has no <Ai><Style name=\"{style}\">.");
        }
    }

    /// <summary>
    /// How well one difficulty plays, as the managers read it (spec §6): the policy resolved with the difficulty's handicaps
    /// by <see cref="AiPolicy.Resolve"/> (AI overhaul §2.2). No difficulty gets more resources, vision or speed.
    /// </summary>
    public class AiDifficultyConfig
    {
        /// <summary>Seconds between two thinks.</summary>
        public float ThinkSeconds;
        /// <summary>The action budget: actions it may take per minute.</summary>
        public int ActionsPerMinute;
        /// <summary>Seconds before a new event (an alert, a newly seen army) may be acted on.</summary>
        public float ReactionSeconds;
        /// <summary>The handicaps it was resolved from (0..1; Hard 1, 1, 1 and 0).</summary>
        public float Insight, Attention, Ambition, Mistakes;
        /// <summary>Builders it keeps at its main base.</summary>
        public int Builders;
        /// <summary>Extra Builders per expansion it holds.</summary>
        public int BuildersPerExpansion;
        /// <summary>Most expansions it takes (the style may want fewer).</summary>
        public int MaxExpansions;
        /// <summary>Earliest match second it expands; the style scales it (<see cref="AiStyleConfig.ExpandTiming"/>).</summary>
        public float ExpandAfterSeconds;
        /// <summary>True when it plans its upkeep so it is never unpaid (every difficulty since the overhaul; a slip can skip a check).</summary>
        public bool KeepsUpkeepPaid;
        /// <summary>Balance it keeps back, in seconds of its current upkeep, before spending.</summary>
        public float UpkeepReserveSeconds;
        /// <summary>Share (0..1) of the policy's counter shift it makes towards counters of the enemy units it knows.</summary>
        public float CounterWeight;
        /// <summary>Share (0..1) of the building counters (what enemy buildings it has seen will make) it uses.</summary>
        public float BuildingCounterWeight;
        /// <summary>Scales <see cref="AiBrainConfig.ArmyMemorySeconds"/>: how long it remembers an enemy army.</summary>
        public float ArmyMemoryScale;
        /// <summary>True when it pulls back from a losing fight (on its attentive thinks).</summary>
        public bool Retreats;
        /// <summary>Fight-model advantage (ours over theirs) it needs to start an attack; the style scales it.</summary>
        public float AttackAdvantage;
        /// <summary>Local fight-model advantage below which it retreats.</summary>
        public float RetreatAdvantage;
        /// <summary>True when its units in range focus fire on the weakest enemy they see.</summary>
        public bool FocusFire;
        /// <summary>True when it keeps Artillery and Snipers behind its front line.</summary>
        public bool KeepsBacklineBack;
        /// <summary>True when its Diggers tunnel through enemy walls near its attacking army.</summary>
        public bool DigsPastWalls;
        /// <summary>How it scouts.</summary>
        public AiScouting Scouting;
        /// <summary>Seconds between re-scouting runs (Regular scouting).</summary>
        public float RescoutSeconds;
        /// <summary>Share (0..1) of its style's Diggers, Suicide bombers, bombs and Jammers it uses.</summary>
        public float SpecialUse;
        /// <summary>True when it puts defences at chokepoints between its base and known enemies (else beside its buildings).</summary>
        public bool DefendsChokepoints;
        /// <summary>Gun turrets it puts up in front of its HQ early (from <c>EarlyTurretSeconds</c>), against an early rush.</summary>
        public int EarlyTurrets;
        /// <summary>Income it needs per producer it already has before it adds another (0: the style's producers whatever its income); skipped while it banks.</summary>
        public float IncomePerProducer;
        /// <summary>Producers it may add beyond its style's while money piles up (AI overhaul §3.3).</summary>
        public int ExtraProducers;
        /// <summary>Scouts it keeps running its scouting route at once.</summary>
        public int Scouts;
        /// <summary>True when it keeps putting Miners on every gem it can reach at its base and expansions (and expands when they are full), not only its style's count.</summary>
        public bool SaturatesMiners;
        /// <summary>When it accepts peace (spec §7).</summary>
        public AiPeace Peace;
        /// <summary>True when it gifts surplus to teammates (spec O5).</summary>
        public bool GiftsSurplus;
    }

    /// <summary>
    /// One difficulty's handicaps (&lt;Difficulty&gt;, AI overhaul §2.1): numbers that only ever make it play worse than the
    /// policy, plus its diplomacy temperament.
    /// </summary>
    public class AiHandicapConfig
    {
        /// <summary>Seconds between two thinks.</summary>
        public float ThinkSeconds;
        /// <summary>Actions it may take per minute.</summary>
        public int ActionsPerMinute;
        /// <summary>Seconds before a new event may be acted on.</summary>
        public float ReactionSeconds;
        /// <summary>0..1: how much it makes of what it sees (counters, memory, scouting, fight-estimate accuracy).</summary>
        public float Insight;
        /// <summary>0..1: share of thinks its army micro runs.</summary>
        public float Attention;
        /// <summary>0..1: scale of its economy (Builders, expansions, saturation, extra producers).</summary>
        public float Ambition;
        /// <summary>0..1: chance a key decision slips (rolled at most once per <see cref="AiPolicyConfig.SlipSeconds"/> per kind).</summary>
        public float Mistakes;
        /// <summary>When it accepts peace (spec §7).</summary>
        public AiPeace Peace;
        /// <summary>True when it gifts surplus to teammates (spec O5).</summary>
        public bool GiftsSurplus;
    }

    /// <summary>
    /// The policy (&lt;Ai&gt;&lt;Policy&gt;, AI overhaul §2 to §6): the best play the managers know. Hard plays it as is; the
    /// other difficulties play it through their handicaps (<see cref="AiPolicy.Resolve"/>).
    /// </summary>
    public class AiPolicyConfig
    {
        // ---- economy (scaled by Ambition) ----
        /// <summary>Builders at its main base, at full ambition.</summary>
        public int Builders;
        /// <summary>Extra Builders per expansion, at full ambition.</summary>
        public int BuildersPerExpansion;
        /// <summary>Most expansions, at full ambition.</summary>
        public int MaxExpansions;
        /// <summary>Earliest match second it expands, at full ambition (divided by the ambition).</summary>
        public float ExpandAfterSeconds;
        /// <summary>Ambition from which it saturates every gem with Miners.</summary>
        public float SaturateAmbition;
        /// <summary>Producers beyond the style's it may add while banking, at full ambition.</summary>
        public int ExtraProducers;
        /// <summary>Income per producer it has before it adds another (0: none needed).</summary>
        public float IncomePerProducer;
        /// <summary>Balance it keeps back, in seconds of its upkeep shortfall.</summary>
        public float UpkeepReserveSeconds;
        /// <summary>Share of its projected income that buildings costing upkeep may take together (producers leave room for an army).</summary>
        public float BuildingUpkeepShare;
        /// <summary>A balance above this ...</summary>
        public float BankHigh;
        /// <summary>... for this long is money piling up: it adds production (AI overhaul §3.3).</summary>
        public float BankSeconds;

        // ---- defence and scouting ----
        /// <summary>Gun turrets in front of its HQ early.</summary>
        public int EarlyTurrets;
        /// <summary>True when it defends the chokepoints out of its start region.</summary>
        public bool DefendsChokepoints;
        /// <summary>Scouts on its route at once, at full insight.</summary>
        public int Scouts;
        /// <summary>Seconds between re-scouting runs, at full insight (divided by the insight).</summary>
        public float RescoutSeconds;

        // ---- fights (AI overhaul §3.1) ----
        /// <summary>Advantage it needs to attack (scaled by the style and its caution).</summary>
        public float AttackAdvantage;
        /// <summary>Local advantage below which it retreats.</summary>
        public float RetreatAdvantage;
        /// <summary>Home's advantage over a threat above which the rest of the army counter-attacks.</summary>
        public float CounterAttackAdvantage;
        /// <summary>Estimate noise at zero insight: an estimate is multiplied by a factor in 1 ± (1 − insight) × this.</summary>
        public float EstimateNoise;
        /// <summary>Extra targets a splash hit is counted against.</summary>
        public float SplashTargets;
        /// <summary>Damage bonus for out-ranging the other side, at a full range's lead.</summary>
        public float RangeWeight;
        /// <summary>Seconds of fight a Suicide bomber's blast is spread over.</summary>
        public float BomberFightSeconds;
        /// <summary>True when it retreats from losing fights.</summary>
        public bool Retreats;
        /// <summary>True when its units in range focus fire.</summary>
        public bool FocusFire;
        /// <summary>True when it keeps Artillery and Snipers behind the front.</summary>
        public bool KeepsBacklineBack;
        /// <summary>True when its Diggers dig past enemy walls.</summary>
        public bool DigsPastWalls;

        // ---- targets and squads (AI overhaul §3.4, §3.5) ----
        /// <summary>Tiles of distance that halve a target's score.</summary>
        public float TargetDistanceScale;
        /// <summary>An enemy HQ's value as a target.</summary>
        public float HqValue;
        /// <summary>Its advantage over an enemy's remembered army from which that enemy's HQ is worth this many times more (a finishing blow).</summary>
        public float FinishAdvantage;
        /// <summary>Value added to a Miner target for the income it denies.</summary>
        public float MinerIncomeValue;
        /// <summary>Score factor of a target where an attack lost badly ...</summary>
        public float AvoidFactor;
        /// <summary>... for this long.</summary>
        public float AvoidSeconds;
        /// <summary>Share of its army's value an attack leaves at home after a threat at its base ...</summary>
        public float HomeGuardShare;
        /// <summary>... for this long after that threat.</summary>
        public float HomeGuardSeconds;
        /// <summary>Most units in a raid.</summary>
        public int RaidSize;
        /// <summary>Slowest move speed a raider may have.</summary>
        public float RaidMinSpeed;
        /// <summary>Advantage a raid needs over its target's known defence.</summary>
        public float RaidAdvantage;
        /// <summary>Most seconds a raid lasts.</summary>
        public float RaidSeconds;

        // ---- adaptation (AI overhaul §4) ----
        /// <summary>A trade (enemy value lost over its own) below this is a bad attack ...</summary>
        public float BadTrade;
        /// <summary>... and above this a good one.</summary>
        public float GoodTrade;
        /// <summary>Caution (the attack threshold's factor) is multiplied by this after a bad trade ...</summary>
        public float CautionUp;
        /// <summary>... and by this after a good one ...</summary>
        public float CautionDown;
        /// <summary>... within these bounds.</summary>
        public float MinCaution, MaxCaution;
        /// <summary>Factor on its building counters against Gun turrets once turrets beat one of its attacks.</summary>
        public float TurretCounterBoost;
        /// <summary>Seconds in which an enemy model's peak counts halve when not seen again.</summary>
        public float ModelHalfLife;
        /// <summary>This many enemy combat units seen at once ...</summary>
        public int RushUnits;
        /// <summary>... before this much play mark a rusher.</summary>
        public float RushSeconds;
        /// <summary>An enemy expansion before this much play marks an expander.</summary>
        public float FastExpandSeconds;
        /// <summary>Factor on its first attack's time against a greedy enemy.</summary>
        public float PunishFactor;
        /// <summary>Factor on its army mix's counters to what scouting found (a rush: Flamethrowers and Tanks; a Turtle: Artillery and Diggers).</summary>
        public float ReactionBias;
        /// <summary>Factor on the wait between raids against an enemy it guesses is a Turtle.</summary>
        public float TurtleRaidFactor;
        /// <summary>Seconds after the last threat at its base that a rush counts as seen off.</summary>
        public float RushOverSeconds;
        /// <summary>Seconds between two style switches.</summary>
        public float StyleSwitchSeconds;
        /// <summary>Play time after which a Turtle with the advantage breaks out.</summary>
        public float TurtleBreakoutSeconds;

        // ---- the brain (AI overhaul §2.3, §5.1, §6.1) ----
        /// <summary>Seconds within which a slip of one kind is rolled at most once.</summary>
        public float SlipSeconds;
        /// <summary>Share of its actions per minute that go to the economy pool.</summary>
        public float EconomyShare;
        /// <summary>Per-match variation of its style's numbers (each × 1 ± this).</summary>
        public float Variation;
        /// <summary>Most degrees its gathering point turns off the line to the enemy.</summary>
        public float GatherAngle;
    }

    /// <summary>One step of a style's opening: a building to place or a unit to queue.</summary>
    public struct AiBuildStep
    {
        /// <summary>The building to place, or None for a unit step.</summary>
        public BuildingType Building;
        /// <summary>The unit to queue, or None for a building step.</summary>
        public UnitType Unit;

        public override string ToString() => Building != BuildingType.None ? Building.ToString() : Unit.ToString();
    }

    /// <summary>How one style plays (spec §5.6): the same managers with different weights.</summary>
    public class AiStyleConfig
    {
        /// <summary>The openings it may play (AI overhaul §6.2), at least one.</summary>
        public AiBuildStep[][] BuildOrders = System.Array.Empty<AiBuildStep[]>();
        /// <summary>The opening it plays, in order (the first of <see cref="BuildOrders"/> until a brain varies the style).</summary>
        public AiBuildStep[] BuildOrder = System.Array.Empty<AiBuildStep>();
        /// <summary>Army composition weights by unit type (before counters).</summary>
        public Dictionary<UnitType, float> ArmyMix = new Dictionary<UnitType, float>();
        /// <summary>Producers it keeps by type (the HQ aside).</summary>
        public Dictionary<BuildingType, int> Producers = new Dictionary<BuildingType, int>();
        /// <summary>Miners at its main base.</summary>
        public int Miners;
        /// <summary>Miners at each expansion.</summary>
        public int MinersPerExpansion;
        /// <summary>Expansions it wants (capped by the difficulty's <see cref="AiDifficultyConfig.MaxExpansions"/>).</summary>
        public int Expansions;
        /// <summary>Scales the difficulty's <see cref="AiDifficultyConfig.ExpandAfterSeconds"/>: below 1 expands early, above 1 late.</summary>
        public float ExpandTiming;
        /// <summary>Earliest match second of its first attack.</summary>
        public float FirstAttackSeconds;
        /// <summary>Fewest combat units it attacks with.</summary>
        public int AttackArmySize;
        /// <summary>Scales the difficulty's <see cref="AiDifficultyConfig.AttackRatio"/>: below 1 attacks more readily.</summary>
        public float AttackRatioScale;
        /// <summary>Seconds between attack waves once it has attacked.</summary>
        public float AttackIntervalSeconds;
        /// <summary>Gun turrets it keeps per defended spot.</summary>
        public int Turrets;
        /// <summary>Gun turrets it rings its HQ with, spread round it (playtest 2026-10-30: Turtles left their HQ open from the sides).</summary>
        public int HomeTurrets;
        /// <summary>Scales <see cref="AiPlayConfig.DefenceAfterSeconds"/>: below 1 it starts its defences sooner.</summary>
        public float DefenceStart;
        /// <summary>Jammers it keeps per defended spot.</summary>
        public int Jammers;
        /// <summary>Bombs it keeps per defended spot.</summary>
        public int Bombs;
        /// <summary>True when it walls its chokepoints.</summary>
        public bool Walls;
        /// <summary>True when it builds a Radar.</summary>
        public bool Radar;
        /// <summary>True when its army holds its chokepoint (Hold) while it isn't attacking (Turtle).</summary>
        public bool HoldsChokes;
        /// <summary>True when it sends raiders at enemy Miners and expansions (AI overhaul §3.5).</summary>
        public bool Raids;

        /// <summary>A deep copy (a brain varies its own).</summary>
        public AiStyleConfig Clone()
        {
            var copy = (AiStyleConfig)MemberwiseClone();
            copy.BuildOrders = (AiBuildStep[][])BuildOrders.Clone();
            copy.BuildOrder = (AiBuildStep[])BuildOrder.Clone();
            copy.ArmyMix = new Dictionary<UnitType, float>(ArmyMix);
            copy.Producers = new Dictionary<BuildingType, int>(Producers);
            return copy;
        }
    }

    /// <summary>
    /// How every AI plays (&lt;Ai&gt;&lt;Play&gt;, plan Task 7): the managers' shared numbers, the same at every difficulty and
    /// style. Difficulty and style change behaviour through their own blocks; these only size and pace it.
    /// </summary>
    public class AiPlayConfig
    {
        /// <summary>Tiles around a start option counted for its gems and open ground.</summary>
        public float OptionRadius;
        /// <summary>Score per gem tile within <see cref="OptionRadius"/> of an option.</summary>
        public float OptionGemWeight;
        /// <summary>Score per ground tile within <see cref="OptionRadius"/>.</summary>
        public float OptionOpenWeight;
        /// <summary>Score per rich field (expansion) within <see cref="OptionExpansionReach"/>, falling to 0 at the reach.</summary>
        public float OptionExpansionWeight;
        /// <summary>Tiles within which an expansion counts for an option.</summary>
        public float OptionExpansionReach;
        /// <summary>Score lost per tile from the nearest teammate's pick (Teams).</summary>
        public float OptionTeammateWeight;
        /// <summary>Match seconds after which the style's build order is dropped, done or not.</summary>
        public float OpeningSeconds;
        /// <summary>Tiles from the HQ (or an expansion's centre, by its radius) within which buildings are placed.</summary>
        public float BaseRadius;
        /// <summary>Free tiles kept around a new producer, Radar or Jammer, so paths stay open (not Miners, turrets or bombs).</summary>
        public int BuildingGap;
        /// <summary>Candidate anchors a placement search tries per think (a longer search goes on next think).</summary>
        public int PlacementTriesPerThink;
        /// <summary>Seconds a placed building's footprint stays reserved until the building shows up in its view.</summary>
        public float PendingSeconds;
        /// <summary>Seconds an anchor where a placement was refused (or never showed up) stays out of its search.</summary>
        public float BlockedSeconds;
        /// <summary>Seconds before a building request whose search found no spot is searched again.</summary>
        public float SearchRetrySeconds;
        /// <summary>Seconds between two re-plans of its defended spots and gathering point.</summary>
        public float ReplanSeconds;
        /// <summary>Tiles around a defended spot its turrets, bombs and Jammers go within.</summary>
        public float DefenceSpread;
        /// <summary>Builders sent to each blueprint.</summary>
        public int BuildersPerBlueprint;
        /// <summary>Seconds before the same blueprint, repair or scout target is assigned again.</summary>
        public float AssignSeconds;
        /// <summary>Health share below which a finished building is repaired.</summary>
        public float RepairBelow;
        /// <summary>Units kept queued on each producer.</summary>
        public int QueueAhead;
        /// <summary>Units queued at once on an empty army producer when it can pay for them all (a queue button's step).</summary>
        public int QueueBatch;
        /// <summary>Share of its income its upkeep may reach (upkeep-keeping difficulties), without a bank.</summary>
        public float UpkeepIncomeShare;
        /// <summary>Seconds of its upkeep Normal and Hard always keep in hand.</summary>
        public float UpkeepFloorSeconds;
        /// <summary>Seconds of an upkeep deficit its balance must cover before it accepts one.</summary>
        public float UpkeepBankSeconds;
        /// <summary>Income left over its upkeep (per second) below which it builds Miners beyond its style's count.</summary>
        public float UpkeepHeadroom;
        /// <summary>Balance above which an AI whose upkeep has caught up with its income expands before its time.</summary>
        public float CappedExpandResources;
        /// <summary>Tiles from the HQ towards the enemy where the army gathers.</summary>
        public float GatherDistance;
        /// <summary>Tiles around an army's centre that count as its fight (strength on both sides).</summary>
        public float ArmyRadius;
        /// <summary>Seconds between two re-orders of the same group.</summary>
        public float OrderSeconds;
        /// <summary>Units gathered at home sent on as reinforcements to an attack.</summary>
        public int ReinforceSize;
        /// <summary>Tiles from the HQ within which home units come to defend.</summary>
        public float DefendRadius;
        /// <summary>Share (0..1) of the army mix moved to counters when it has seen enemy units.</summary>
        public float CounterShift;
        /// <summary>Weight of the building counters against the unit counters (Hard).</summary>
        public float BuildingCounterWeight;
        /// <summary>Share of light units among the enemies seen above which it adds a turret per defended spot (Normal, Hard).</summary>
        public float LightTurretShare;
        /// <summary>Tiles behind the front its Artillery and Snipers stay (Hard).</summary>
        public float BacklineDistance;
        /// <summary>Visible enemy units within a bomber's blast that make it detonate.</summary>
        public int BomberClump;
        /// <summary>Suicide bombers it checks for a detonation per think (a bound on its work in a huge fight).</summary>
        public int BomberChecks;
        /// <summary>Match seconds before it scouts with any unit when it has no Scout.</summary>
        public float ScoutAfterSeconds;
        /// <summary>Match seconds before it builds defences beyond its build order's.</summary>
        public float DefenceAfterSeconds;
        /// <summary>Tiles past its start region's edge along a lane where a chokepoint defence goes.</summary>
        public float DefenceDistance;
        /// <summary>Most chokepoints it defends.</summary>
        public int MaxDefendedSpots;
        /// <summary>Tiles left open in the middle of a chokepoint wall for its own army.</summary>
        public int WallGap;
        /// <summary>Most seconds a retreat lasts.</summary>
        public float RetreatSeconds;
        /// <summary>A Gun turret's strength as a share of its cost (it can't move, but hits hard).</summary>
        public float TurretStrength;
        /// <summary>Tiles short of its target where an attack group stages (gathers) before it engages.</summary>
        public float StageDistance;
        /// <summary>Share of the group at the staging point that starts the engagement ...</summary>
        public float StageShare;
        /// <summary>... or after this long staging, whichever comes first.</summary>
        public float StageSeconds;
        /// <summary>Normal, Hard: upkeep-capped (no room for another unit) with this much in hand, it attacks without the strength check.</summary>
        public float MaxedAttackResources;
        /// <summary>A blueprint that makes no progress this long is cancelled (unreachable, or never staffed).</summary>
        public float StuckBlueprintSeconds;
        /// <summary>A threat to its base worth at least this share of its attack group (and more than home holds) calls the attack back.</summary>
        public float RecallShare;
        /// <summary>Most seconds an attack group marches to its staging point before the staging wait starts anyway.</summary>
        public float MarchSeconds;
        /// <summary>Seconds of play after which a difficulty's <c>EarlyTurrets</c> go up.</summary>
        public float EarlyTurretSeconds;
        /// <summary>Map size (tiles a side) at which a difficulty runs all its <c>Scouts</c>; smaller maps get proportionally fewer (at least one).</summary>
        public int ScoutsFullMapSize;
        /// <summary>A scout gives up a route stop it hasn't reached or seen in this long.</summary>
        public float ScoutStopSeconds;
        /// <summary>A march's staging wait starts at the latest after its slowest unit's travel time times this (and never before <see cref="MarchSeconds"/>).</summary>
        public float MarchSlack;
        /// <summary>Defence blueprints (turrets, bombs, Jammers) it keeps standing at once: more would hold its Builders off Miners.</summary>
        public int MaxDefenceBlueprints;
        /// <summary>A losing fight at the front whose units are under this share of the attack group's strength is a strung-out vanguard: the group regroups instead of retreating.</summary>
        public float RegroupShare;
        /// <summary>A Miner search that found no spot (and nothing to explore) marks that area full for this long (Miner saturation, expansions).</summary>
        public float SaturatedSeconds;
        /// <summary>Half-side of the patch of anchors kept out of its search around a stuck blueprint it cancelled (its Builders couldn't reach it).</summary>
        public int StuckPatch;
    }

    /// <summary>The AI's diplomacy (spec §7, O5, O6).</summary>
    public class AiDiplomacyConfig
    {
        /// <summary>Seconds a non-teammate sharing vision must go without hitting it before it is offered peace.</summary>
        public float PeaceSeconds;
        /// <summary>Seconds it refuses peace from someone who broke it.</summary>
        public float GrudgeSeconds;
        /// <summary>Its strength over theirs (as seen) above which it is clearly winning.</summary>
        public float WinningRatio;
        /// <summary>Its strength over theirs (as seen) below which it is losing.</summary>
        public float LosingRatio;
        /// <summary>Resources above which it has a surplus to gift.</summary>
        public float GiftSurplus;
        /// <summary>Seconds the surplus must last before it gifts.</summary>
        public float GiftSurplusSeconds;
        /// <summary>Share (0..1) of its balance it gifts.</summary>
        public float GiftShare;
        /// <summary>Factor applied once to <see cref="PeaceSeconds"/> by a gift from a non-teammate.</summary>
        public float GiftPeaceFactor;
    }

    /// <summary>The brain's own bookkeeping (plan Task 6): the action budget, proposals, threats and memory. The same for every difficulty.</summary>
    public class AiBrainConfig
    {
        /// <summary>Seconds of its actions per minute the budget may save up (a burst after a quiet spell).</summary>
        public float BudgetBurstSeconds;
        /// <summary>Seconds a proposal the budget couldn't afford waits before it is dropped (managers propose again).</summary>
        public float ProposalWaitSeconds;
        /// <summary>Tiles from its own buildings within which a seen enemy unit is a threat.</summary>
        public float ThreatRadius;
        /// <summary>Seconds a last-seen enemy army is remembered.</summary>
        public float ArmyMemorySeconds;
        /// <summary>Seconds an event (an alert, a threat) stays on the blackboard.</summary>
        public float EventMemorySeconds;
    }

    /// <summary>When the AI surrenders (spec §8).</summary>
    public class AiConcedeConfig
    {
        /// <summary>Match minutes before it may concede.</summary>
        public float MinMinutes;
        /// <summary>Seconds both conditions must hold.</summary>
        public float Seconds;
        /// <summary>Its strength over the strongest enemy force seen recently below which it is hopeless.</summary>
        public float Ratio;
        /// <summary>Seconds back the strongest enemy force seen counts.</summary>
        public float SeenWindowSeconds;
        /// <summary>Seconds of income it counts on when asking whether it can afford a unit.</summary>
        public float IncomeSeconds;
    }
}
