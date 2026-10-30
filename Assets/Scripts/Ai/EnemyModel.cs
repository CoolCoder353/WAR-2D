using Config;

namespace WAR2D.Ai
{
    /// <summary>What an AI guesses an enemy plays (AI overhaul §4.2), from what it has seen.</summary>
    public enum EnemyStyleGuess : byte
    {
        Unknown,
        Rusher,
        Turtle,
        Expander,
    }

    /// <summary>
    /// The AI's picture of one enemy (AI overhaul §4.2), learned only from its view: the most units of each type it has
    /// seen at once (decaying by half every <c>ModelHalfLife</c> while not seen again), the buildings it knows by type and
    /// when it first saw each type, whether the enemy holds an expansion, and a guess at its style.
    /// </summary>
    public sealed class EnemyModel
    {
        public EnemyModel(int owner, int unitTypes, int buildingTypes)
        {
            Owner = owner;
            Peak = new float[unitTypes];
            SeenNow = new int[unitTypes];
            Buildings = new int[buildingTypes];
            FirstBuildingSeen = new double[buildingTypes];
            for (int i = 0; i < buildingTypes; i++) FirstBuildingSeen[i] = double.NaN;
        }

        public int Owner { get; }

        /// <summary>Most units of each type (by <see cref="UnitType"/> value) seen at once, decayed while not seen again.</summary>
        public readonly float[] Peak;

        /// <summary>Its units of each type in sight at the last think.</summary>
        public readonly int[] SeenNow;

        /// <summary>Its buildings the AI knows (seen now or as ghosts), by <see cref="BuildingType"/> value.</summary>
        public readonly int[] Buildings;

        /// <summary>Play seconds when the AI first knew a building of each type (NaN: never).</summary>
        public readonly double[] FirstBuildingSeen;

        /// <summary>Play seconds when the AI first saw one of its combat units (NaN: never).</summary>
        public double FirstCombatSeen = double.NaN;

        /// <summary>Most of its combat units the AI saw at once before <c>RushSeconds</c> of play.</summary>
        public int EarlyArmy;

        /// <summary>Play seconds when the AI first saw a building of its at an expansion (NaN: never).</summary>
        public double FirstExpansionSeen = double.NaN;

        /// <summary>True when it has walls the AI has seen.</summary>
        public bool Walls;

        public EnemyStyleGuess Guess;

        /// <summary>The combat units it remembers (rounded peaks).</summary>
        public int Units
        {
            get
            {
                float n = 0f;
                for (int t = 0; t < Peak.Length; t++) if ((UnitType)t != UnitType.Builder) n += Peak[t];
                return (int)System.Math.Round(n);
            }
        }

        /// <summary>The worth of the army it remembers (peak counts by cost).</summary>
        public float ArmyValue(GameConfigData game)
        {
            float sum = 0f;
            for (int t = 0; t < Peak.Length; t++)
                if (Peak[t] > 0f && (UnitType)t != UnitType.Builder && game.Units.TryGetValue((UnitType)t, out UnitConfig c)) sum += Peak[t] * c.UpfrontCost;
            return sum;
        }

        /// <summary>Guesses its style (AI overhaul §4.2) from what the AI has seen and when.</summary>
        internal void UpdateGuess(AiPolicyConfig policy)
        {
            bool earlyArmy = EarlyArmy >= policy.RushUnits;
            bool earlyBarracks = Buildings[(int)BuildingType.Barracks] >= 2 && FirstBuildingSeen[(int)BuildingType.Barracks] < policy.RushSeconds;
            // Every AI puts up EarlyTurrets in front of its HQ: a Turtle has clearly more than that (or walls).
            if (Buildings[(int)BuildingType.GunTurret] > policy.EarlyTurrets + 1 || Walls) Guess = EnemyStyleGuess.Turtle;
            else if (earlyArmy || earlyBarracks) Guess = EnemyStyleGuess.Rusher;
            else if (!double.IsNaN(FirstExpansionSeen) && FirstExpansionSeen < policy.FastExpandSeconds) Guess = EnemyStyleGuess.Expander;
            else Guess = EnemyStyleGuess.Unknown;
        }
    }
}
