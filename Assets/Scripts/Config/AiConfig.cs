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

/// <summary>Who may join the host's lobby (v0.14): a public or friends-only Steam lobby, or a private match nobody can join.</summary>
public enum LobbyVisibility : byte
{
    Public,
    Friends,
    Private,
}

namespace Config
{
    /// <summary>How far an AI shifts its army mix towards counters (spec §5.5).</summary>
    public enum AiCounters : byte
    {
        /// <summary>The style's fixed mix.</summary>
        None,
        /// <summary>Counters the enemy units it has seen.</summary>
        Seen,
        /// <summary>Also counters what the enemy buildings it has seen will make.</summary>
        SeenAndBuildings,
    }

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
        /// <summary>Easy, Normal and Hard (Scripted has no brain and no settings).</summary>
        public Dictionary<AiDifficulty, AiDifficultyConfig> Difficulties = new Dictionary<AiDifficulty, AiDifficultyConfig>();
        /// <summary>Rusher, Turtle, Expander and Balanced (Random resolves to one of them).</summary>
        public Dictionary<AiStyle, AiStyleConfig> Styles = new Dictionary<AiStyle, AiStyleConfig>();
        public AiDiplomacyConfig Diplomacy = new AiDiplomacyConfig();
        public AiConcedeConfig Concede = new AiConcedeConfig();
        public AiBrainConfig Brain = new AiBrainConfig();

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

    /// <summary>How well one difficulty plays (spec §6). No difficulty gets more resources, vision or speed.</summary>
    public class AiDifficultyConfig
    {
        /// <summary>Seconds between two thinks.</summary>
        public float ThinkSeconds;
        /// <summary>The action budget: actions it may take per minute.</summary>
        public int ActionsPerMinute;
        /// <summary>Seconds before a new event (an alert, a newly seen army) may be acted on.</summary>
        public float ReactionSeconds;
        /// <summary>Builders it keeps at its main base.</summary>
        public int Builders;
        /// <summary>Extra Builders per expansion it holds.</summary>
        public int BuildersPerExpansion;
        /// <summary>Most expansions it takes (the style may want fewer).</summary>
        public int MaxExpansions;
        /// <summary>Earliest match second it expands; the style scales it (<see cref="AiStyleConfig.ExpandTiming"/>).</summary>
        public float ExpandAfterSeconds;
        /// <summary>True when it never lets upkeep go unpaid: it pauses production and builds Miners first.</summary>
        public bool KeepsUpkeepPaid;
        /// <summary>Balance it keeps back, in seconds of its current upkeep, before spending.</summary>
        public float UpkeepReserveSeconds;
        /// <summary>How far it counters what it has seen (spec §5.5).</summary>
        public AiCounters Counters;
        /// <summary>True when it pulls back from a losing fight.</summary>
        public bool Retreats;
        /// <summary>Its strength over the enemy's (as seen) below which it retreats.</summary>
        public float RetreatRatio;
        /// <summary>True when it focuses fire on one target at a time.</summary>
        public bool FocusFire;
        /// <summary>True when it keeps Artillery and Snipers behind its front line.</summary>
        public bool KeepsBacklineBack;
        /// <summary>How it scouts.</summary>
        public AiScouting Scouting;
        /// <summary>Seconds between re-scouting runs (Regular scouting).</summary>
        public float RescoutSeconds;
        /// <summary>Share (0..1) of its style's Diggers, Suicide bombers, bombs and Jammers it uses: rarely below 1, as the style wants at 1.</summary>
        public float SpecialUse;
        /// <summary>True when it puts defences at chokepoints between its base and known enemies (else beside its buildings).</summary>
        public bool DefendsChokepoints;
        /// <summary>Its strength over the enemy's (as seen) it needs before attacking; the style scales it.</summary>
        public float AttackRatio;
        /// <summary>When it accepts peace (spec §7).</summary>
        public AiPeace Peace;
        /// <summary>True when it gifts surplus to teammates (spec O5).</summary>
        public bool GiftsSurplus;
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
        /// <summary>The opening, in order.</summary>
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
        /// <summary>Jammers it keeps per defended spot.</summary>
        public int Jammers;
        /// <summary>Bombs it keeps per defended spot.</summary>
        public int Bombs;
        /// <summary>True when it walls its chokepoints.</summary>
        public bool Walls;
        /// <summary>True when it builds a Radar.</summary>
        public bool Radar;
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
