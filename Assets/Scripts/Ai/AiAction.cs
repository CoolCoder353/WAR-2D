using System.Collections.Generic;
using Unity.Mathematics;

namespace WAR2D.Ai
{
    /// <summary>The player actions an AI can take: one per <see cref="PlayerActions"/> method.</summary>
    public enum AiActionKind : byte
    {
        None,
        PlaceHq,
        PlaceBuilding,
        CancelBuildings,
        PlaceWall,
        SetProduction,
        ChangeQueues,
        SetRally,
        Order,
        Detonate,
        DigArea,
        AssignSquad,
        OrderSquad,
        SetAttack,
        SetShareVision,
        Gift,
        Surrender,
    }

    /// <summary>
    /// One player action as a value (v0.14): what a manager proposes and the budget spends. <see cref="Execute"/> is the
    /// only way the AI changes the match, and it only calls <see cref="PlayerActions"/>, so an AI passes exactly the
    /// checks a client's Command does. Make one with the static constructors; ids are copied, so the proposer may reuse its lists.
    /// </summary>
    public readonly struct AiAction
    {
        public readonly AiActionKind Kind;
        /// <summary>A tile: the anchor, the order goal, the rally, or a wall's / dig box's first corner.</summary>
        public readonly int2 Tile;
        /// <summary>A wall's or dig box's second corner.</summary>
        public readonly int2 To;
        /// <summary>The building or unit type (as its enum's value), the queue step, the squad, or the other player.</summary>
        public readonly int Value;
        /// <summary>A rotation (degrees) or a gift amount.</summary>
        public readonly float Amount;
        /// <summary>Queue the order, clear the rally, or turn attack / vision sharing on.</summary>
        public readonly bool Flag;
        public readonly OrderKind Order;
        /// <summary>The units or buildings it names (a private copy).</summary>
        public readonly int[] Ids;

        private AiAction(AiActionKind kind, int2 tile = default, int2 to = default, int value = 0, float amount = 0f, bool flag = false, OrderKind order = default, int[] ids = null)
        {
            Kind = kind;
            Tile = tile;
            To = to;
            Value = value;
            Amount = amount;
            Flag = flag;
            Order = order;
            Ids = ids;
        }

        private static int[] Copy(IReadOnlyList<int> ids)
        {
            if (ids == null) return System.Array.Empty<int>();
            var copy = new int[ids.Count];
            for (int i = 0; i < copy.Length; i++) copy[i] = ids[i];
            return copy;
        }

        public static AiAction PlaceHq(int2 anchor) => new AiAction(AiActionKind.PlaceHq, anchor);
        public static AiAction PlaceBuilding(BuildingType type, int2 anchor, float rotation = 0f) => new AiAction(AiActionKind.PlaceBuilding, anchor, value: (int)type, amount: rotation);
        public static AiAction CancelBuildings(IReadOnlyList<int> buildings) => new AiAction(AiActionKind.CancelBuildings, ids: Copy(buildings));
        public static AiAction PlaceWall(int2 from, int2 to) => new AiAction(AiActionKind.PlaceWall, from, to);
        public static AiAction SetProduction(IReadOnlyList<int> producers, UnitType type) => new AiAction(AiActionKind.SetProduction, value: (int)type, ids: Copy(producers));
        public static AiAction ChangeQueues(IReadOnlyList<int> producers, int delta) => new AiAction(AiActionKind.ChangeQueues, value: delta, ids: Copy(producers));
        public static AiAction SetRally(IReadOnlyList<int> producers, int2 tile, bool clear = false) => new AiAction(AiActionKind.SetRally, tile, flag: clear, ids: Copy(producers));
        public static AiAction OrderUnits(IReadOnlyList<int> units, OrderKind order, int2 goal, bool queue = false) => new AiAction(AiActionKind.Order, goal, flag: queue, order: order, ids: Copy(units));
        public static AiAction Detonate(IReadOnlyList<int> bombers) => new AiAction(AiActionKind.Detonate, ids: Copy(bombers));
        public static AiAction DigArea(IReadOnlyList<int> diggers, int2 from, int2 to, bool queue = false) => new AiAction(AiActionKind.DigArea, from, to, flag: queue, ids: Copy(diggers));
        public static AiAction AssignSquad(int squad, IReadOnlyList<int> units) => new AiAction(AiActionKind.AssignSquad, value: squad, ids: Copy(units));
        public static AiAction OrderSquad(int squad, OrderKind order, int2 goal, bool queue = false) => new AiAction(AiActionKind.OrderSquad, goal, value: squad, flag: queue, order: order);
        public static AiAction SetAttack(int other, bool on) => new AiAction(AiActionKind.SetAttack, value: other, flag: on);
        public static AiAction SetShareVision(int other, bool on) => new AiAction(AiActionKind.SetShareVision, value: other, flag: on);
        public static AiAction Gift(int other, float amount) => new AiAction(AiActionKind.Gift, value: other, amount: amount);
        public static AiAction Surrender() => new AiAction(AiActionKind.Surrender);

        /// <summary>Takes the action as <paramref name="owner"/>, through <see cref="PlayerActions"/>; true when it was accepted.</summary>
        public bool Execute(int owner)
        {
            switch (Kind)
            {
                case AiActionKind.PlaceHq: return PlayerActions.PlaceHq(owner, Tile);
                case AiActionKind.PlaceBuilding: return PlayerActions.PlaceBuilding(owner, Tile, (BuildingType)Value, Amount);
                case AiActionKind.CancelBuildings: return PlayerActions.CancelBuildings(owner, Ids);
                case AiActionKind.PlaceWall: return PlayerActions.PlaceWall(owner, Tile, To);
                case AiActionKind.SetProduction: return PlayerActions.SetProduction(owner, Ids, (UnitType)Value);
                case AiActionKind.ChangeQueues: return PlayerActions.ChangeQueues(owner, Ids, Value);
                case AiActionKind.SetRally: return PlayerActions.SetRally(owner, Ids, Tile, Flag);
                case AiActionKind.Order: return PlayerActions.Order(owner, Ids, Order, Flag, Tile);
                case AiActionKind.Detonate: return PlayerActions.Detonate(owner, Ids);
                case AiActionKind.DigArea: return PlayerActions.DigArea(owner, Ids, Flag, Tile, To);
                case AiActionKind.AssignSquad: return PlayerActions.AssignSquad(owner, Value, Ids);
                case AiActionKind.OrderSquad: return PlayerActions.OrderSquad(owner, Value, Order, Flag, Tile);
                case AiActionKind.SetAttack: return PlayerActions.SetAttack(owner, Value, Flag);
                case AiActionKind.SetShareVision: return PlayerActions.SetShareVision(owner, Value, Flag);
                case AiActionKind.Gift: return PlayerActions.Gift(owner, Value, Amount);
                case AiActionKind.Surrender: return PlayerActions.Surrender(owner);
                default: return false;
            }
        }

        public override string ToString() => $"{Kind}({Tile}, {Value})";
    }
}
