using System.Collections.Generic;
using Config;
using Unity.Collections;
using Unity.Mathematics;
using WAR2D.Ai;
using WAR2D.AiView;
using WAR2D.World;

/// <summary>
/// A scripted world for the AI's managers (v0.14 plan Task 7): a generated map, one AI and one passive enemy, the
/// economy (income, upkeep, unpaid seconds), blueprints that finish after a while, producers that spawn when paid, units
/// that walk straight to their goals, and fog from sight. It builds the AI's <see cref="TeamView"/> by the client rules
/// (own units always, others only on cells it sees now, enemy buildings seen now or as ghosts) and checks every action
/// the way the server does (placements through <see cref="PlacementRules"/> on the true map), so a test can tell a
/// refused click from an accepted one.
/// </summary>
public sealed class AiSandbox
{
    public const float TickSeconds = 0.05f;
    public readonly GameConfigData Game;
    public readonly int Width, Height;
    public readonly byte[] Tiles;
    public readonly int2[] Sites;
    public readonly AiRegion[] Regions;
    public readonly AiLane[] Lanes;
    public readonly int Ai = AiOwnerIds.For(0), Enemy = 7;
    public GameState State = GameState.PlacingHQ;
    public double Time;
    public float Resources;
    public int UnpaidSeconds;
    public readonly List<AiBuilding> Buildings = new List<AiBuilding>();
    public readonly List<AiUnit> Units = new List<AiUnit>();
    public readonly int[] Options;
    public readonly List<AiAction> Actions = new List<AiAction>(), Refused = new List<AiAction>();
    /// <summary>When each of <see cref="Actions"/> was taken.</summary>
    public readonly List<double> ActionTimes = new List<double>();
    /// <summary>Placements refused by the placement rules (not for money): an illegal spot.</summary>
    public readonly List<(AiAction action, PlacementResult why)> IllegalPlacements = new List<(AiAction, PlacementResult)>();
    /// <summary>When the AI's side first saw each enemy building.</summary>
    public readonly Dictionary<int, double> FirstSeen = new Dictionary<int, double>();
    public readonly TeamView View = new TeamView();
    public AiBrain Brain;

    // ---- diplomacy (plan Task 8): the AI's own row and what reaches it, as the server would send them ----
    public bool DiplomacyOn;
    /// <summary>Every player but the AI (team 0): the enemy on team 1 by default.</summary>
    public readonly List<AiPlayer> Others = new List<AiPlayer>();
    /// <summary>Its own row: whom it attacks and shares vision with; and who shares vision with it.</summary>
    public readonly HashSet<int> AiAttacks = new HashSet<int>(), AiShares = new HashSet<int>(), SharesWithAi = new HashSet<int>();
    /// <summary>Alerts and attack events for its next view.</summary>
    public readonly List<AiAlert> PendingAlerts = new List<AiAlert>();
    public readonly List<AiAttack> PendingAttacks = new List<AiAttack>();
    public readonly List<(int to, float amount)> Gifts = new List<(int, float)>();
    public bool Surrendered;
    /// <summary>Seconds a blueprint takes once placed (the sandbox doesn't model Builders' work).</summary>
    public float BlueprintSeconds = 12f;

    private readonly byte[] explored, visible;
    private readonly int fogW, fogH, cell;
    private readonly Dictionary<int, float2> goals = new Dictionary<int, float2>();
    private readonly Dictionary<int, float> placedAt = new Dictionary<int, float>();
    private readonly Dictionary<int, float> progress = new Dictionary<int, float>();
    private readonly Dictionary<int, AiBuilding> ghosts = new Dictionary<int, AiBuilding>();
    private int nextId = 1;
    private double nextThink, nextSecond = 1.0;

    public AiSandbox(int size = 256, uint seed = 5, int[] options = null)
    {
        ConfigLoader.ResetForTests();
        Game = ConfigLoader.LoadConfig();
        MapLayout layout = RegionMapGenerator.Generate(size, seed, Game.Match.Map.GemChance, Allocator.Temp, Game.Match.Map);
        Width = Height = size;
        Tiles = layout.Tiles.ToArray();
        layout.Dispose();
        Sites = layout.StartSites;
        Regions = new AiRegion[layout.Regions.Length];
        for (int i = 0; i < Regions.Length; i++) Regions[i] = new AiRegion { Centre = layout.Regions[i].Centre, Radius = layout.Regions[i].Radius, Kind = (AiRegionKind)layout.Regions[i].Kind };
        Lanes = new AiLane[layout.Lanes.Length];
        for (int i = 0; i < Lanes.Length; i++) Lanes[i] = new AiLane { A = layout.Lanes[i].A, B = layout.Lanes[i].B, Width = layout.Lanes[i].Width, ChokeWidth = layout.Lanes[i].ChokeWidth };
        Options = options ?? new[] { 0, 1, 2 };
        cell = Game.Simulation.FogCellSize;
        fogW = (Width + cell - 1) / cell;
        fogH = (Height + cell - 1) / cell;
        explored = new byte[fogW * fogH];
        visible = new byte[fogW * fogH];
        Resources = Game.Resources.StartingResources;
        Others.Add(new AiPlayer { OwnerId = Enemy, Team = 1 });
        AiAttacks.Add(Enemy);
    }

    /// <summary>Adds another player (a teammate on team 0, else an enemy it attacks) and returns its owner id.</summary>
    public int AddPlayer(int owner, int team)
    {
        Others.Add(new AiPlayer { OwnerId = owner, Team = team });
        if (team != 0) AiAttacks.Add(owner);
        else AiShares.Add(owner);
        return owner;
    }

    /// <summary>Marks a player out of the match.</summary>
    public void Eliminate(int owner)
    {
        for (int i = 0; i < Others.Count; i++)
            if (Others[i].OwnerId == owner) { AiPlayer p = Others[i]; p.Eliminated = true; Others[i] = p; }
    }

    /// <summary>The AI's side is told that <paramref name="attacker"/> hit <paramref name="target"/> (ids).</summary>
    public void ReportAttack(int attacker, int target) => PendingAttacks.Add(new AiAttack { AttackerId = attacker, TargetId = target, Time = Time });

    /// <summary>A brain with only the given managers (diplomacy and concede tests).</summary>
    public AiBrain MakeBrain(AiDifficulty difficulty, AiStyle style, params IAiManager[] managers)
    {
        Brain = new AiBrain(Ai, difficulty, style, Game.Ai, managers.Length > 0 ? managers : null, Game);
        Brain.Execute = Execute;
        return Brain;
    }

    /// <summary>Makes the AI's brain; its actions come to <see cref="Execute"/>.</summary>
    public AiBrain MakeBrain(AiDifficulty difficulty, AiStyle style)
    {
        Brain = new AiBrain(Ai, difficulty, style, Game.Ai, null, Game);
        Brain.Execute = Execute;
        return Brain;
    }

    // ---- the world ----

    public TileType TileAt(int2 t) => t.x >= 0 && t.y >= 0 && t.x < Width && t.y < Height ? (TileType)Tiles[t.y * Width + t.x] : TileType.Border;

    public bool IsUsed(int2 t)
    {
        foreach (AiBuilding b in Buildings)
        {
            int2 start = Footprint.Start(b.Anchor, Size(b.Type)), end = start + Size(b.Type);
            if (t.x >= start.x && t.y >= start.y && t.x < end.x && t.y < end.y) return true;
        }
        return false;
    }

    public int2 Size(BuildingType type) => Game.GetBuilding(type).Size;

    public bool InOwnOptions(int2 tile)
    {
        foreach (int o in Options) if (PlacementRules.InClearing(tile, Sites[o], MapGenerator.HqClearRadius)) return true;
        return false;
    }

    public bool Explored(int2 t) => explored[FogIndex((float2)t)] != 0;
    public bool Sees(float2 p) => visible[FogIndex(p)] != 0;
    private int FogIndex(float2 p)
    {
        int2 c = math.clamp((int2)math.floor(p / cell), 0, new int2(fogW - 1, fogH - 1));
        return c.y * fogW + c.x;
    }

    /// <summary>Adds a finished building (no checks) and returns its id.</summary>
    public int AddBuilding(int owner, BuildingType type, int2 anchor, float rotation = 0f)
    {
        BuildingConfig c = Game.GetBuilding(type);
        int id = nextId++;
        Buildings.Add(new AiBuilding { Id = id, OwnerId = owner, Type = type, Anchor = anchor, Rotation = (byte)((int)math.round(rotation / 90f) & 3), Health = c.Health, MaxHealth = c.Health, Progress = 100, Producing = SpawnerRules.DefaultUnit(type) });
        return id;
    }

    public int AddUnit(int owner, UnitType type, float2 at)
    {
        UnitConfig c = Game.GetUnit(type);
        int id = nextId++;
        Units.Add(new AiUnit { Id = id, OwnerId = owner, Type = type, Position = at, Health = c.Health, MaxHealth = c.Health });
        return id;
    }

    public int Count(int owner, BuildingType type, bool completeOnly = false)
    {
        int n = 0;
        foreach (AiBuilding b in Buildings) if (b.OwnerId == owner && b.Type == type && (!completeOnly || b.Progress >= 100)) n++;
        return n;
    }

    public int Count(int owner, UnitType type)
    {
        int n = 0;
        foreach (AiUnit u in Units) if (u.OwnerId == owner && u.Type == type) n++;
        return n;
    }

    // ---- the server's checks ----

    public bool Execute(AiAction a)
    {
        Actions.Add(a);
        ActionTimes.Add(Time);
        bool ok = Apply(a);
        if (!ok) Refused.Add(a);
        return ok;
    }

    private bool HasHq(int owner)
    {
        foreach (AiBuilding b in Buildings) if (b.OwnerId == owner && b.Type == BuildingType.Base) return true;
        return false;
    }

    /// <summary>The server's placement check for the AI (explored ground except the HQ, then <see cref="PlacementRules"/> on the true map).</summary>
    public PlacementResult CheckPlacement(BuildingType type, int2 anchor, float rotation)
    {
        int2 size = Size(type);
        if (type != BuildingType.Base)
            foreach (int2 t in Footprint.Tiles(anchor, size)) if (!Explored(t)) return PlacementResult.Unexplored;
        return PlacementRules.Check(type, anchor, rotation, size, State, HasHq(Ai), TileAt, IsUsed, _ => false, InOwnOptions);
    }

    private bool Apply(AiAction a)
    {
        switch (a.Kind)
        {
            case AiActionKind.PlaceHq:
            case AiActionKind.PlaceBuilding:
            {
                BuildingType type = a.Kind == AiActionKind.PlaceHq ? BuildingType.Base : (BuildingType)a.Value;
                PlacementResult why = CheckPlacement(type, a.Tile, a.Amount);
                if (why != PlacementResult.Ok) { IllegalPlacements.Add((a, why)); return false; }
                float cost = Game.GetBuilding(type).UpfrontCost;
                if (Resources < cost) return false;
                Resources -= cost;
                int id = AddBuilding(Ai, type, a.Tile, a.Amount);
                if (type != BuildingType.Base)
                {
                    int i = Buildings.Count - 1;
                    AiBuilding b = Buildings[i];
                    b.Progress = 0;
                    b.Producing = UnitType.None;
                    Buildings[i] = b;
                    placedAt[id] = (float)Time;
                }
                return true;
            }
            case AiActionKind.SetProduction:
            {
                if (State != GameState.Playing) return false;
                bool any = false;
                for (int i = 0; i < Buildings.Count; i++)
                {
                    AiBuilding b = Buildings[i];
                    if (System.Array.IndexOf(a.Ids, b.Id) < 0 || b.OwnerId != Ai || b.Progress < 100 || !SpawnerRules.MayProduce(b.Type, (UnitType)a.Value) || b.Producing == (UnitType)a.Value) continue;
                    b.Producing = (UnitType)a.Value;
                    b.Queued = 0;
                    progress[b.Id] = 0f;
                    Buildings[i] = b;
                    any = true;
                }
                return any;
            }
            case AiActionKind.ChangeQueues:
            {
                if (State != GameState.Playing || !SpawnerRules.IsQueueStep(a.Value)) return false;
                bool any = false;
                for (int i = 0; i < Buildings.Count; i++)
                {
                    AiBuilding b = Buildings[i];
                    if (System.Array.IndexOf(a.Ids, b.Id) < 0 || b.OwnerId != Ai || b.Progress < 100 || !SpawnerRules.IsProducer(b.Type)) continue;
                    int count = b.Queued;
                    if (SpawnerRules.Apply(ref count, a.Value) == 0) continue;
                    b.Queued = count;
                    Buildings[i] = b;
                    any = true;
                }
                return any;
            }
            case AiActionKind.SetRally:
            {
                bool any = false;
                for (int i = 0; i < Buildings.Count; i++)
                {
                    AiBuilding b = Buildings[i];
                    if (System.Array.IndexOf(a.Ids, b.Id) < 0 || b.OwnerId != Ai) continue;
                    b.HasRally = !a.Flag;
                    b.Rally = a.Tile;
                    Buildings[i] = b;
                    any = true;
                }
                return any;
            }
            case AiActionKind.Order:
            {
                if (State != GameState.Playing) return false;
                for (int i = 0; i < Units.Count; i++)
                {
                    AiUnit u = Units[i];
                    if (System.Array.IndexOf(a.Ids, u.Id) < 0 || u.OwnerId != Ai) continue;
                    bool stand = a.Order == OrderKind.Hold || a.Order == OrderKind.Stop;
                    u.HasOrder = !stand;
                    u.OrderGoal = (float2)a.Tile + 0.5f;
                    u.Stance = a.Order == OrderKind.Hold ? (byte)3 : a.Order == OrderKind.AttackMove ? (byte)2 : stand ? (byte)0 : (byte)1;
                    u.Work = a.Order == OrderKind.Build ? (byte)1 : a.Order == OrderKind.Repair ? (byte)2 : (byte)0;
                    Units[i] = u;
                    if (!stand) goals[u.Id] = u.OrderGoal; else goals.Remove(u.Id);
                }
                return true;
            }
            case AiActionKind.CancelBuildings:
            {
                bool any = false;
                for (int i = Buildings.Count - 1; i >= 0; i--)
                {
                    AiBuilding b = Buildings[i];
                    if (System.Array.IndexOf(a.Ids, b.Id) < 0 || b.OwnerId != Ai || b.Type == BuildingType.Base) continue;
                    Resources += Game.GetBuilding(b.Type).UpfrontCost * (b.Progress < 100 ? 1f : Game.Siege.DemolishRefund);
                    Buildings.RemoveAt(i);
                    any = true;
                }
                return any;
            }
            case AiActionKind.SetAttack:
            case AiActionKind.SetShareVision:
            {
                if (!DiplomacyOn || State != GameState.Playing || a.Value == Ai || !Others.Exists(p => p.OwnerId == a.Value && !p.Eliminated)) return false;
                HashSet<int> row = a.Kind == AiActionKind.SetAttack ? AiAttacks : AiShares;
                if (a.Flag) row.Add(a.Value); else row.Remove(a.Value);
                return true;
            }
            case AiActionKind.Gift:
            {
                if (State != GameState.Playing || a.Amount <= 0f || a.Amount > Resources || !Others.Exists(p => p.OwnerId == a.Value && !p.Eliminated)) return false;
                Resources -= a.Amount;
                Gifts.Add((a.Value, a.Amount));
                return true;
            }
            case AiActionKind.Surrender:
            {
                if (State != GameState.Playing || Surrendered) return false;
                Surrendered = true;
                return true;
            }
            case AiActionKind.PlaceWall:
            case AiActionKind.DigArea:
            case AiActionKind.Detonate:
            case AiActionKind.AssignSquad:
            case AiActionKind.OrderSquad:
                return State == GameState.Playing;
            default:
                return false;
        }
    }

    // ---- time ----

    /// <summary>Runs the world for <paramref name="seconds"/>, the AI thinking every <c>ThinkSeconds</c> on a fresh view.</summary>
    public void Run(double seconds)
    {
        double end = Time + seconds;
        while (Time < end - 1e-9) Tick();
    }

    /// <summary>Builds the view and lets the brain think once now.</summary>
    public void ThinkNow()
    {
        UpdateFog();
        BuildView();
        Brain.Think(View);
    }

    private void Tick()
    {
        Time += TickSeconds;
        Move(TickSeconds);
        if (State == GameState.Playing)
        {
            Construct();
            Produce(TickSeconds);
            if (Time >= nextSecond) { nextSecond += 1.0; Economy(); }
        }
        if (Time >= nextThink && Brain != null)
        {
            nextThink = Time + Brain.ThinkSeconds;
            ThinkNow();
        }
    }

    private void Move(float dt)
    {
        for (int i = 0; i < Units.Count; i++)
        {
            AiUnit u = Units[i];
            if (!goals.TryGetValue(u.Id, out float2 goal)) continue;
            float step = Game.GetUnit(u.Type).MoveSpeed * dt;
            float2 d = goal - u.Position;
            float len = math.length(d);
            if (len <= step)
            {
                u.Position = goal;
                goals.Remove(u.Id);
                if (u.Work == 0) { u.HasOrder = false; u.Stance = 0; }
            }
            else u.Position += d / len * step;
            Units[i] = u;
        }
    }

    private void Construct()
    {
        for (int i = 0; i < Buildings.Count; i++)
        {
            AiBuilding b = Buildings[i];
            if (b.Progress >= 100 || !placedAt.TryGetValue(b.Id, out float at)) continue;
            float share = math.saturate(((float)Time - at) / BlueprintSeconds);
            b.Progress = (byte)(share * 100f);
            if (b.Progress >= 100)
            {
                b.Producing = SpawnerRules.DefaultUnit(b.Type);
                // Builders working on it are free again.
                for (int k = 0; k < Units.Count; k++)
                {
                    AiUnit u = Units[k];
                    if (u.Work != 0 && math.distancesq(u.OrderGoal, (float2)b.Anchor + 0.5f) < 9f) { u.Work = 0; u.HasOrder = false; Units[k] = u; goals.Remove(u.Id); }
                }
            }
            Buildings[i] = b;
        }
        // A Builder whose blueprint is gone (or which only repaired) stops working after a while.
        for (int k = 0; k < Units.Count; k++)
        {
            AiUnit u = Units[k];
            if (u.Work == 0 || goals.ContainsKey(u.Id)) continue;
            bool blueprint = false;
            foreach (AiBuilding b in Buildings) if (b.Progress < 100 && math.distancesq(u.OrderGoal, (float2)b.Anchor + 0.5f) < 9f) blueprint = true;
            if (!blueprint) { u.Work = 0; u.HasOrder = false; Units[k] = u; }
        }
    }

    private void Produce(float dt)
    {
        for (int i = 0; i < Buildings.Count; i++)
        {
            AiBuilding b = Buildings[i];
            if (b.OwnerId != Ai || b.Progress < 100 || b.Queued <= 0 || b.Producing == UnitType.None) continue;
            UnitConfig c = Game.GetUnit(b.Producing);
            progress.TryGetValue(b.Id, out float p);
            p = math.min(p + dt, c.BuildSeconds);
            if (p >= c.BuildSeconds && Resources >= c.UpfrontCost)
            {
                Resources -= c.UpfrontCost;
                p = 0f;
                b.Queued--;
                int id = AddUnit(Ai, b.Producing, Footprint.VisualCenter(b.Anchor, Size(b.Type)) + new float2(Size(b.Type).x * 0.5f + 1f, 0f));
                if (b.HasRally) goals[id] = (float2)b.Rally + 0.5f;
                if (b.HasRally) { AiUnit u = Units[Units.Count - 1]; u.HasOrder = true; u.OrderGoal = (float2)b.Rally + 0.5f; u.Stance = 1; Units[Units.Count - 1] = u; }
                Buildings[i] = b;
            }
            progress[b.Id] = p;
        }
    }

    /// <summary>Once a second: income (passive and Miners facing gems), then upkeep for every unit and building; short means unpaid.</summary>
    private void Economy()
    {
        float income = Game.Resources.PassiveGenerationRate, upkeep = 0f;
        foreach (AiBuilding b in Buildings)
        {
            if (b.OwnerId != Ai) continue;
            upkeep += Game.GetBuilding(b.Type).RunningCost;
            if (b.Type == BuildingType.Miner && b.Progress >= 100 && TileAt(b.Anchor + MinerRules.FacingOffset(b.Rotation * 90f)) == TileType.Gem) income += Game.Resources.MiningRate;
        }
        foreach (AiUnit u in Units) if (u.OwnerId == Ai) upkeep += Game.GetUnit(u.Type).RunningCost;
        Resources += income;
        LastIncome = income;
        LastUpkeep = upkeep;
        if (Resources >= upkeep) Resources -= upkeep;
        else { Resources = 0f; UnpaidSeconds++; }
    }

    public float LastIncome, LastUpkeep;

    // ---- what the AI may know ----

    private void UpdateFog()
    {
        System.Array.Clear(visible, 0, visible.Length);
        foreach (AiBuilding b in Buildings)
            if (b.OwnerId == Ai && (b.Progress >= 100 || b.Type == BuildingType.Base)) Light(Footprint.VisualCenter(b.Anchor, Size(b.Type)), Game.GetBuilding(b.Type).Sight);
        foreach (AiUnit u in Units) if (u.OwnerId == Ai) Light(u.Position, Game.GetUnit(u.Type).Sight);
        for (int i = 0; i < visible.Length; i++) if (visible[i] != 0) explored[i] = 1;
    }

    private void Light(float2 at, float sight)
    {
        int r = (int)math.ceil(sight / cell) + 1;
        int2 c = (int2)math.floor(at / cell);
        for (int y = -r; y <= r; y++)
            for (int x = -r; x <= r; x++)
            {
                int2 f = c + new int2(x, y);
                if (f.x < 0 || f.y < 0 || f.x >= fogW || f.y >= fogH) continue;
                if (math.distance(((float2)f + 0.5f) * cell, at) <= sight) visible[f.y * fogW + f.x] = 1;
            }
    }

    /// <summary>Marks every fog cell explored and seen (tests that aren't about fog).</summary>
    public void RevealAll()
    {
        for (int i = 0; i < explored.Length; i++) explored[i] = 1;
    }

    /// <summary>Fills the AI's view by the client rules.</summary>
    public void BuildView()
    {
        TeamView v = View;
        v.Owner = Ai;
        v.Team = 0;
        v.State = State;
        v.Time = Time;
        v.Tick = (int)(Time / TickSeconds);
        v.Width = Width;
        v.Height = Height;
        if (v.Tiles.Length != Tiles.Length) v.Tiles = (byte[])Tiles.Clone();
        v.HqSites = Sites;
        v.HqClearRadius = MapGenerator.HqClearRadius;
        v.Regions = Regions;
        v.Lanes = Lanes;
        v.Resources = Resources;
        v.Income = LastIncome;
        v.Upkeep = LastUpkeep;
        v.DiplomacyOn = DiplomacyOn;
        v.Players.Clear();
        v.Players.Add(new AiPlayer { OwnerId = Ai, Team = 0, IsAi = true, Eliminated = Surrendered });
        foreach (AiPlayer p in Others) v.Players.Add(p);
        v.Attacking.Clear();
        foreach (int o in AiAttacks) v.Attacking.Add(o);
        v.SharingWith.Clear();
        foreach (int o in AiShares) v.SharingWith.Add(o);
        v.SharedWithMe.Clear();
        foreach (int o in SharesWithAi) v.SharedWithMe.Add(o);
        v.StartOptions.Clear();
        foreach (int o in Options) v.StartOptions.Add(new AiStartOption { OwnerId = Ai, Site = o, Centre = Sites[o] });
        v.OwnUnits.Clear();
        v.OtherUnits.Clear();
        foreach (AiUnit u in Units)
        {
            if (u.OwnerId == Ai) v.OwnUnits.Add(u);
            else if (Sees(u.Position)) v.OtherUnits.Add(new AiUnit { Id = u.Id, OwnerId = u.OwnerId, Type = u.Type, Position = u.Position, Health = u.Health, MaxHealth = u.MaxHealth });
        }
        v.Buildings.Clear();
        foreach (AiBuilding b in Buildings)
        {
            if (b.OwnerId == Ai) { v.Buildings.Add(b); continue; }
            if (Sees(Footprint.VisualCenter(b.Anchor, Size(b.Type))) && !FirstSeen.ContainsKey(b.Id)) FirstSeen[b.Id] = Time;
            if (Sees(Footprint.VisualCenter(b.Anchor, Size(b.Type)))) ghosts[b.Id] = new AiBuilding { Id = b.Id, OwnerId = b.OwnerId, Type = b.Type, Anchor = b.Anchor, Health = b.Health, MaxHealth = b.MaxHealth, Progress = b.Progress };
        }
        var gone = new List<int>();
        foreach (KeyValuePair<int, AiBuilding> g in ghosts)
        {
            AiBuilding known = g.Value;
            bool exists = Buildings.Exists(b => b.Id == g.Key);
            bool seenNow = Sees(Footprint.VisualCenter(known.Anchor, Size(known.Type)));
            if (!exists && seenNow) { gone.Add(g.Key); continue; } // a ghost goes when its spot is seen again
            known.Ghost = !seenNow;
            v.Buildings.Add(known);
        }
        foreach (int id in gone) ghosts.Remove(id);
        v.FogW = fogW;
        v.FogH = fogH;
        v.FogCellSize = cell;
        if (v.Fog.Length != explored.Length) v.Fog = new byte[explored.Length];
        for (int i = 0; i < explored.Length; i++) v.Fog[i] = visible[i] != 0 ? (byte)2 : explored[i] != 0 ? (byte)1 : (byte)0;
        v.Alerts.Clear();
        v.Attacks.Clear();
        v.Blasts.Clear();
        foreach (AiAlert alert in PendingAlerts) v.Alerts.Add(alert);
        foreach (AiAttack attack in PendingAttacks) v.Attacks.Add(attack);
        PendingAlerts.Clear();
        PendingAttacks.Clear();
    }
}
