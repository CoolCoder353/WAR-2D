using System;
using System.Collections.Generic;
using Config;
using Mirror;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using WAR2D.Client.Render;
using WAR2D.Net.Replication;
using ReadOnly = Unity.Collections.ReadOnlyAttribute;

namespace WAR2D.Client
{
    /// <summary>What the client knows about one unit, predicted to now.</summary>
    public struct ClientUnitView
    {
        public int Id, OwnerId;
        public UnitType Type;
        public float2 Position;
        public float Health01;
    }

    /// <summary>
    /// The client's units: replication batches in, predicted positions out, drawn with GPU instancing.
    /// Its clock (<see cref="ServerTime"/>) runs on real time and is nudged toward the newest batch's tick,
    /// never backward. Created by <c>UnitCommander</c> on clients.
    /// </summary>
    public sealed class ClientWorld : MonoBehaviour
    {
        private const int MaxTracersPerFrame = 200;

        public static ClientWorld Instance { get; private set; }

        private ClientUnitStore store;
        private float dt;
        private int lastBatchTick = -1;
        private float serverTime;
        private float sinceBatch; // seconds since the newest batch arrived: the server kept ticking meanwhile
        private NativeArray<float2> lastPosition;
        private NativeArray<float> rotation;
        private NativeArray<uint> color;
        private NativeArray<float> health01;
        private NativeArray<byte> selected;
        private NativeList<UnitInstance> instances, bars;
        private NativeArray<int> barCount;
        private InstancedUnitRenderer renderer;
        private readonly List<(int attacker, int target)> attacks = new List<(int, int)>();
        private readonly List<int> selectedIndices = new List<int>();
        private readonly Dictionary<int, uint> colorOfOwner = new Dictionary<int, uint>();

        /// <summary>Units known.</summary>
        public int KnownCount => store?.Count ?? 0;

        /// <summary>Seconds on the server's tick clock, smoothed.</summary>
        public float ServerTime => serverTime;

        /// <summary>Batches the client could not decode (logged and dropped).</summary>
        public int MalformedBatches { get; private set; }

        /// <summary>The underlying store (tests).</summary>
        internal ClientUnitStore Store => store;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Initialise();
        }

        /// <summary>Allocates the store and buffers and subscribes to batches (Awake; tests call it directly).</summary>
        internal void Initialise()
        {
            if (store != null) return;
            Instance = this;
            GameConfigData config = ConfigLoader.LoadConfig();
            dt = config.Simulation.TickSeconds;
            int capacity = config.Simulation.MaxEntities;
            store = new ClientUnitStore(capacity, dt);
            store.Attack += (attacker, target) => attacks.Add((attacker, target));
            lastPosition = new NativeArray<float2>(capacity, Allocator.Persistent);
            rotation = new NativeArray<float>(capacity, Allocator.Persistent);
            color = new NativeArray<uint>(capacity, Allocator.Persistent);
            health01 = new NativeArray<float>(capacity, Allocator.Persistent);
            selected = new NativeArray<byte>(capacity, Allocator.Persistent);
            instances = new NativeList<UnitInstance>(4096, Allocator.Persistent);
            bars = new NativeList<UnitInstance>(1024, Allocator.Persistent);
            barCount = new NativeArray<int>(1, Allocator.Persistent);
            ReplicationClient.Received += OnBatch;
        }

        private void OnDestroy()
        {
            ReplicationClient.Received -= OnBatch;
            if (Instance == this) Instance = null;
            renderer?.Dispose();
            store?.Dispose();
            if (lastPosition.IsCreated)
            {
                lastPosition.Dispose(); rotation.Dispose(); color.Dispose(); health01.Dispose(); selected.Dispose();
                instances.Dispose(); bars.Dispose(); barCount.Dispose();
            }
        }

        private void OnBatch(ReplicationBatch batch) => Apply(batch);

        /// <summary>Decodes and applies a batch; a malformed one is logged and dropped.</summary>
        public void Apply(in ReplicationBatch batch)
        {
            if (!store.Apply(batch.Payload, batch.Tick))
            {
                MalformedBatches++;
                Debug.LogWarning($"[Replication] dropped a malformed batch for tick {batch.Tick}");
                return;
            }
            if (batch.Tick > lastBatchTick)
            {
                if (lastBatchTick < 0) serverTime = batch.Tick * dt;
                lastBatchTick = batch.Tick;
                sinceBatch = 0f;
            }
        }

        /// <summary>
        /// Advances the clock by <paramref name="deltaSeconds"/>, nudged (at most 10 % a frame, never
        /// backward) toward the server's estimated time: the newest batch's tick plus the time since it
        /// arrived. A quiet server sends no batches, but its clock keeps running.
        /// </summary>
        public void AdvanceClock(float deltaSeconds)
        {
            if (lastBatchTick < 0) return;
            sinceBatch += deltaSeconds;
            float target = lastBatchTick * dt + sinceBatch;
            float next = serverTime + deltaSeconds;
            next += (target - next) * 0.1f;
            serverTime = math.max(serverTime, next);
        }

        private void Update()
        {
            AdvanceClock(Time.unscaledDeltaTime);
            store.PredictAll(serverTime);
            FireTracers();
        }

        /// <summary>True while the client knows the unit with this id.</summary>
        public bool IsKnownId(int id) => id > 0 && store.IdOf(NetIdAllocator.IndexOf(id)) == id;

        /// <summary>A known unit by id, predicted at the current clock.</summary>
        public bool TryGet(int id, out ClientUnitView view)
        {
            int index = NetIdAllocator.IndexOf(id);
            if (store.IdOf(index) != id || id <= 0) { view = default; return false; }
            view = new ClientUnitView
            {
                Id = id,
                OwnerId = store.OwnerOf(index),
                Type = (UnitType)store.TypeOf(index),
                Position = store.PredictOne(index, serverTime),
                Health01 = store.Health01(index),
            };
            return true;
        }

        /// <summary>Adds the ids of the owner's units inside the box (inclusive) to <paramref name="into"/>.</summary>
        public void QueryBox(float2 min, float2 max, int ownerId, List<int> into)
        {
            float2 lo = math.min(min, max), hi = math.max(min, max);
            NativeArray<int> known = store.Known;
            NativeArray<float2> predicted = store.Predicted;
            int n = math.min(known.Length, predicted.Length);
            for (int i = 0; i < n; i++)
            {
                int index = known[i];
                if (store.OwnerOf(index) != ownerId) continue;
                float2 p = predicted[i];
                if (p.x < lo.x || p.y < lo.y || p.x > hi.x || p.y > hi.y) continue;
                into.Add(store.IdOf(index));
            }
        }

        /// <summary>Predicted positions, parallel to the known list (refreshed every frame).</summary>
        public NativeArray<float2> Positions => store.Predicted;

        /// <summary>Packs every known unit and draws it (and the health bars). Selected ids draw highlighted.</summary>
        public void Draw(IReadOnlyCollection<int> selectedIds)
        {
            int count = store.Count;
            if (count == 0 || store.Predicted.Length != count) return;
            if (renderer == null)
            {
                var texture = Resources.Load<Texture2D>(UnitType.Tank.ToString());
                renderer = new InstancedUnitRenderer(texture, 0.8f);
            }

            foreach (int index in selectedIndices) selected[index] = 0;
            selectedIndices.Clear();
            if (selectedIds != null)
                foreach (int id in selectedIds)
                {
                    int index = NetIdAllocator.IndexOf(id);
                    if (store.IdOf(index) != id) continue;
                    selected[index] = 1;
                    selectedIndices.Add(index);
                }

            NativeArray<int> known = store.Known;
            NativeArray<float2> positions = store.Predicted;
            NativeArray<int> owners = store.OwnerArray;
            NativeArray<byte> healthBytes = store.HealthArray;
            for (int i = 0; i < count; i++)
            {
                int index = known[i];
                float2 p = positions[i];
                float2 delta = p - lastPosition[index];
                if (math.lengthsq(delta) > 1e-6f && math.lengthsq(delta) < 4f) rotation[index] = math.atan2(delta.y, delta.x);
                lastPosition[index] = p;
                color[index] = ColorOf(owners[index]);
                health01[index] = healthBytes[index] / 100f;
            }

            instances.ResizeUninitialized(count);
            bars.ResizeUninitialized(count);
            new PackUnitsJob
            {
                Known = known,
                Positions = positions,
                Rotation = rotation,
                Color = color,
                Health01 = health01,
                Selected = selected,
                UnitHalfSize = 0.4f,
                Instances = instances.AsArray(),
                Bars = bars.AsArray(),
                BarCount = barCount,
            }.Run();
            renderer.Draw(instances.AsArray(), count);
            renderer.DrawHealthBars(bars.AsArray(), barCount[0]);
        }

        private uint ColorOf(int ownerId)
        {
            if (colorOfOwner.TryGetValue(ownerId, out uint c)) return c;
            int slot = GameCore.Instance != null ? GameCore.Instance.PlayerOrder.IndexOf(ownerId) : -1;
            if (slot < 0) return InstancedUnitRenderer.Pack(new Color32(160, 160, 160, 255)); // not cached: the order may still arrive
            c = InstancedUnitRenderer.Pack(InstancedUnitRenderer.TeamColors[slot % InstancedUnitRenderer.TeamColors.Length]);
            colorOfOwner[ownerId] = c;
            return c;
        }

        /// <summary>Tracers for this frame's attacks whose attacker is on screen, at most 200 (newest kept).</summary>
        private void FireTracers()
        {
            if (attacks.Count == 0) return;
            Camera cam = Camera.main;
            int start = math.max(0, attacks.Count - MaxTracersPerFrame);
            for (int i = start; i < attacks.Count && cam != null; i++)
            {
                var (attacker, target) = attacks[i];
                float2 from = store.PredictOne(attacker, serverTime);
                Vector3 viewport = cam.WorldToViewportPoint(new Vector3(from.x, from.y, 0));
                if (viewport.x < 0 || viewport.x > 1 || viewport.y < 0 || viewport.y > 1) continue;
                Vector3 to;
                int targetIndex = NetIdAllocator.IndexOf(target);
                if (store.IdOf(targetIndex) == target && target > 0)
                {
                    float2 t = store.PredictOne(targetIndex, serverTime);
                    to = new Vector3(t.x, t.y, 0);
                }
                else if (UnitCommander.Instance != null && UnitCommander.Instance.buildingGameObjects.TryGetValue(target, out GameObject building))
                {
                    to = building.transform.position;
                }
                else continue;
                Effects.Tracer(new Vector3(from.x, from.y, 0), to);
            }
            attacks.Clear();
        }
    }
}
