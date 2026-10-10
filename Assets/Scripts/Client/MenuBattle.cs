using System;
using System.Collections.Generic;
using System.Linq;
using Config;
using Mirror;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using WAR2D.Net.Replication;
using WAR2D.Sim;
using WAR2D.World;

namespace WAR2D.Client
{
    /// <summary>
    /// The live battle behind the main menu (owner request, v0.6): two bot armies on attack-move on a small
    /// generated map, simulated offline in a World of its own (no Mirror server, no <c>-perf</c>, no dev
    /// APIs) and drawn by a <see cref="ClientWorld"/> fed through a virtual replication client. Reinforced
    /// so it never ends, watched by a slowly drifting camera. Torn down completely before hosting or
    /// joining, so it never shares a <see cref="SimContext"/> or ids with a real match.
    /// </summary>
    public sealed class MenuBattle : MonoBehaviour
    {
        private const int OwnerA = 1, OwnerB = 2;
        private const float StartDelaySeconds = 0.5f, ReinforceSeconds = 4f, CameraSize = 26f, DriftSpeed = 1.5f;

        /// <summary>The running battle, or null.</summary>
        public static MenuBattle Instance { get; private set; }

        /// <summary>True when the player wants the battle (Settings → Graphics).</summary>
        public static bool Enabled
        {
            get => SettingsStore.Current.MenuBattle;
            set => SettingsStore.Current.MenuBattle = value;
        }

        private Unity.Entities.World world;
        private SimTickGroup group;
        private SimContext sim;
        private MapStore map;
        private ReplicationService replication;
        private ClientWorld view;
        private GameConfigData config;
        private int2[] starts;
        private readonly int[] alive = new int[2];
        private double time;
        private float tickTimer, reinforceTimer, startTimer = StartDelaySeconds;
        private Vector3 cameraHome;

        /// <summary>True while the battle's simulation exists.</summary>
        public bool Running => sim != null;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
        }

        private void OnDestroy()
        {
            TearDown();
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            bool online = NetworkClient.active || NetworkServer.active;
            bool wanted = Enabled && !online && !Application.isBatchMode && !DevApi.PerfFlag;
            if (!wanted)
            {
                TearDown();
                startTimer = StartDelaySeconds;
                return;
            }
            if (!Running)
            {
                startTimer -= Time.unscaledDeltaTime;
                if (startTimer <= 0f) Begin();
                return;
            }

            tickTimer += Time.unscaledDeltaTime;
            float dt = config.Simulation.TickSeconds;
            int steps = 0;
            while (tickTimer >= dt && steps++ < 2) // never more than two ticks a frame
            {
                tickTimer -= dt;
                Tick(dt);
            }
            if (tickTimer > dt) tickTimer = 0f;

            reinforceTimer -= Time.unscaledDeltaTime;
            if (reinforceTimer <= 0f)
            {
                reinforceTimer = ReinforceSeconds;
                Reinforce();
            }
            DriftCamera();
        }

        private void LateUpdate()
        {
            if (Running) view.Draw(null);
        }

        /// <summary>Creates the map, the simulation, its replication to the view, and both armies.</summary>
        private void Begin()
        {
            config = ConfigLoader.LoadConfig();
            MenuBattleConfig battle = config.MenuBattle;
            map = MapStore.Generate(battle.MapSize, battle.Seed, config.Match.Map.GemChance);

            world = new Unity.Entities.World("MenuBattle");
            group = world.GetOrCreateSystemManaged<SimTickGroup>();
            group.RateManager = null;
            IEnumerable<Type> stages = typeof(SimTickGroup).Assembly.GetTypes()
                .Where(t => t.Namespace == "WAR2D.Sim" && !t.IsAbstract
                            && (typeof(ComponentSystemBase).IsAssignableFrom(t) || typeof(ISystem).IsAssignableFrom(t))
                            && t.GetCustomAttributes(typeof(UpdateInGroupAttribute), false)
                                .Cast<UpdateInGroupAttribute>().Any(a => a.GroupType == typeof(SimTickGroup)));
            foreach (Type type in stages) group.AddSystemToUpdateList(world.CreateSystem(type));
            group.SortSystems();

            SimContext.RunningOverride = true;
            sim = SimContext.Create(world, map, config);
            sim.Settled += CountAlive;
            replication = new ReplicationService(sim, config);
            replication.AddVirtualClient(OwnerA, Receive);
            // B shares its sight with A, so the watcher sees both sides of the fight.
            sim.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.SetShareVision, OwnerId = OwnerB, TargetOwnerId = OwnerA, Flag = true });

            view = new GameObject("MenuBattleView").AddComponent<ClientWorld>();
            view.ColourIndexOf = owner => owner - 1; // army A in player colour 1, B in colour 2
            MapView.Show(map.Grid);

            int2 centre = new int2(map.Grid.Width / 2, map.Grid.Height / 2);
            starts = new[] { centre + new int2(-24, 0), centre + new int2(24, 0) };
            replication.SetVirtualView(OwnerA, centre - new int2(64, 40), centre + new int2(64, 40));
            Spawn(0, battle.UnitsPerArmy);
            Spawn(1, battle.UnitsPerArmy);
            Order();

            Camera cam = MenuCamera();
            if (cam != null)
            {
                cam.backgroundColor = new Color32(0x26, 0x30, 0x3A, 255); // surface/base
                cameraHome = new Vector3(centre.x, centre.y, cam.transform.position.z);
                cam.transform.position = cameraHome;
                cam.orthographic = true;
                cam.orthographicSize = CameraSize;
            }
            reinforceTimer = ReinforceSeconds;
        }

        /// <summary>Removes everything the battle made: the view, the map backdrop, replication, the simulation and its World.</summary>
        public void TearDown()
        {
            if (sim == null) return;
            if (view != null) Destroy(view.gameObject);
            view = null;
            MapView.Hide();
            replication.Dispose();
            replication = null;
            sim.Settled -= CountAlive;
            sim.Dispose();
            sim = null;
            if (world != null && world.IsCreated) world.Dispose();
            world = null;
            map.Dispose();
            map = null;
            SimContext.RunningOverride = null;
        }

        private void Tick(float dt)
        {
            time += dt;
            world.SetTime(new TimeData(time, dt));
            group.Update();
        }

        private void Receive(ArraySegment<byte> payload, int channel)
        {
            if (channel < 0 || view == null) return; // fog and building payloads: the menu shows neither
            view.Apply(new ReplicationBatch { Tick = sim.Clock.Tick, Payload = payload });
        }

        private void CountAlive(SimData data, int tick, int count)
        {
            alive[0] = alive[1] = 0;
            for (int i = 0; i < count; i++)
            {
                if (data.Health[i] <= 0f) continue;
                if (data.OwnerId[i] == OwnerA) alive[0]++;
                else if (data.OwnerId[i] == OwnerB) alive[1]++;
            }
        }

        /// <summary>Tops each army back up from behind its start, then sends everyone at the enemy again.</summary>
        private void Reinforce()
        {
            int want = config.MenuBattle.UnitsPerArmy;
            bool any = false;
            for (int army = 0; army < 2; army++)
            {
                int missing = want - alive[army];
                if (missing < want / 10) continue;
                Spawn(army, missing);
                any = true;
            }
            if (any) Order();
        }

        /// <summary>Spawns units for an army on walkable tiles in rings behind its start.</summary>
        private void Spawn(int army, int count)
        {
            int owner = army == 0 ? OwnerA : OwnerB;
            int2 origin = starts[army] + new int2(army == 0 ? -12 : 12, 0);
            int placed = 0;
            for (int ring = 0; placed < count && ring < 60; ring++)
            for (int i = 0; i < math.max(1, 8 * ring) && placed < count; i++)
            {
                int2 tile = Ring(origin, ring, i);
                if (!map.Grid.IsWalkable(tile)) continue;
                sim.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.SpawnUnit, OwnerId = owner, Position = (float2)tile + 0.5f, UnitType = UnitType.Tank });
                placed++;
            }
        }

        /// <summary>Attack-move every unit of each army at the other army's start (a box order over the whole map).</summary>
        private void Order()
        {
            int2 max = new int2(map.Grid.Width - 1, map.Grid.Height - 1);
            sim.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.OrderUnits, Order = OrderKind.AttackMove, OwnerId = OwnerA, Tile = starts[1] + new int2(12, 0), BoxMin = int2.zero, BoxMax = max });
            sim.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.OrderUnits, Order = OrderKind.AttackMove, OwnerId = OwnerB, Tile = starts[0] - new int2(12, 0), BoxMin = int2.zero, BoxMax = max });
        }

        /// <summary>The menu scene's camera (it isn't tagged MainCamera).</summary>
        private static Camera MenuCamera() => Camera.main != null ? Camera.main : FindAnyObjectByType<Camera>();

        private void DriftCamera()
        {
            Camera cam = MenuCamera();
            if (cam == null) return;
            float t = Time.unscaledTime * 0.05f;
            Vector3 target = cameraHome + new Vector3(math.sin(t) * 16f, math.sin(t * 0.7f) * 8f, 0f);
            cam.transform.position = Vector3.MoveTowards(cam.transform.position, target, DriftSpeed * Time.unscaledDeltaTime * 4f);
        }

        private static int2 Ring(int2 origin, int ring, int i)
        {
            if (ring == 0) return origin;
            int side = 2 * ring;
            if (i < side) return new int2(origin.x - ring + i, origin.y - ring);
            i -= side;
            if (i < side) return new int2(origin.x + ring, origin.y - ring + i);
            i -= side;
            if (i < side) return new int2(origin.x + ring - i, origin.y + ring);
            i -= side;
            return new int2(origin.x - ring, origin.y + ring - i);
        }
    }
}
