using System.Collections.Generic;
using UnityEngine;
using WAR2D.Art;
using WAR2D.Art.Render;

/// <summary>
/// Client-only ground marks from the <c>Effects</c> atlas (polish items 3 and G): scorch where a unit died, rubble over a
/// destroyed building's footprint, and tread marks behind vehicles. Each mark holds, then fades through its dithered frames
/// and goes. Like <see cref="Effects"/>, each kind has a fixed pool of sprites made once; when a pool is full the oldest mark
/// is reused. Marks only follow deaths and movement the client already draws. A destroyed unit also leaves its wreck (its own
/// sprite at its last facing, charred), which fades out by alpha (playtest 2026-10-30: wrecks, and longer-lasting marks).
/// </summary>
public static class Decals
{
    /// <summary>Pool sizes: the most of each kind on the map at once.</summary>
    public const int ScorchPool = 256, LargeScorchPool = 64, RubblePool = 48, LargeRubblePool = 16, TrackPool = 2048, WreckPool = 192;

    /// <summary>Seconds a scorch holds before it fades, and the seconds the fade takes.</summary>
    public const float ScorchHold = 60f, ScorchFade = 20f;

    /// <summary>Seconds rubble holds before it fades, and the seconds the fade takes.</summary>
    public const float RubbleHold = 180f, RubbleFade = 30f;

    /// <summary>Seconds a tread mark holds before it fades, and the seconds the fade takes.</summary>
    public const float TrackHold = 12f, TrackFade = 10f;

    /// <summary>Seconds a unit's wreck holds before it fades, and the seconds the fade takes.</summary>
    public const float WreckHold = 60f, WreckFade = 15f;

    /// <summary>The tint on a wreck's sprite: charred, dark and a little warm.</summary>
    public static readonly Color WreckTint = new Color(0.32f, 0.29f, 0.27f, 1f);

    private sealed class Slot
    {
        public GameObject Go;
        public SpriteRenderer Sprite;
        public float Start;
        public int Direction, Frame;
        public bool Active;
        /// <summary>A wreck: drawn from another atlas and faded by alpha rather than through dithered frames.</summary>
        public bool Wreck;
    }

    private sealed class Pool
    {
        public readonly string Id;
        public readonly int Size;
        public readonly float Hold, Fade;
        public readonly List<Slot> Slots = new List<Slot>();
        public int Next;

        public Pool(string id, int size, float hold, float fade) { Id = id; Size = size; Hold = hold; Fade = fade; }
    }

    private static readonly Pool Scorches = new Pool("Scorch", ScorchPool, ScorchHold, ScorchFade);
    private static readonly Pool LargeScorches = new Pool("ScorchLarge", LargeScorchPool, ScorchHold, ScorchFade);
    private static readonly Pool Rubble1 = new Pool("Rubble1", RubblePool, RubbleHold, RubbleFade);
    private static readonly Pool Rubble2 = new Pool("Rubble2", RubblePool, RubbleHold, RubbleFade);
    private static readonly Pool Rubble3 = new Pool("Rubble3", RubblePool, RubbleHold, RubbleFade);
    private static readonly Pool Rubble5 = new Pool("Rubble5", LargeRubblePool, RubbleHold, RubbleFade);
    private static readonly Pool Tracks = new Pool("Tracks", TrackPool, TrackHold, TrackFade);
    private static readonly Pool Wrecks = new Pool("Wreck", WreckPool, WreckHold, WreckFade);
    private static readonly Pool[] All = { Scorches, LargeScorches, Rubble1, Rubble2, Rubble3, Rubble5, Tracks, Wrecks };
    private static Runner runner;
    private static ArtAtlas atlas;

    /// <summary>Sprite objects created so far (tests: stays at most the pool sizes).</summary>
    public static int Created { get; private set; }

    /// <summary>Scorches (both sizes) on the map now.</summary>
    public static int ActiveScorches => Count(Scorches) + Count(LargeScorches);

    /// <summary>Rubble patches on the map now.</summary>
    public static int ActiveRubble => Count(Rubble1) + Count(Rubble2) + Count(Rubble3) + Count(Rubble5);

    /// <summary>Tread marks on the map now.</summary>
    public static int ActiveTracks => Count(Tracks);

    /// <summary>Unit wrecks on the map now.</summary>
    public static int ActiveWrecks => Count(Wrecks);

    /// <summary>A wreck's alpha <paramref name="age"/> seconds after it was left: 1 while it holds, then down to 0; below 0 once it is gone.</summary>
    public static float WreckAlpha(float age) => age < WreckHold ? 1f : 1f - (age - WreckHold) / WreckFade;

    /// <summary>
    /// The fade frame a mark shows <paramref name="age"/> seconds after it was made: 0 while it holds, then one frame per equal
    /// share of the fade; -1 once it is gone.
    /// </summary>
    public static int FrameAt(float age, float hold, float fade, int frames = EffectGen.DecalFrames)
    {
        if (age < hold) return 0;
        float into = (age - hold) / fade;
        if (into >= 1f) return -1;
        return Mathf.Min(frames - 1, 1 + (int)(into * (frames - 1)));
    }

    /// <summary>The scorch left where a unit died; <paramref name="large"/> for a 2-tile unit.</summary>
    public static void Scorch(Vector2 position, bool large) => Place(large ? LargeScorches : Scorches, position, 0);

    /// <summary>Rubble over a destroyed building's footprint, centred on <paramref name="centre"/>; <paramref name="tiles"/> is its width.</summary>
    public static void Rubble(Vector2 centre, int tiles) =>
        Place(tiles >= 5 ? Rubble5 : tiles >= 3 ? Rubble3 : tiles == 2 ? Rubble2 : Rubble1, centre, 0);

    /// <summary>
    /// A tread mark centred on <paramref name="position"/>, laid along <paramref name="heading"/> (radians). The east–west mark is
    /// turned to the exact heading (playtest 2026-10-30: marks snapped to 45° steps made a staircase behind a vehicle).
    /// </summary>
    public static void Track(Vector2 position, float heading)
    {
        Slot s = Place(Tracks, position, 0);
        if (s != null) s.Go.transform.rotation = Quaternion.Euler(0f, 0f, heading * Mathf.Rad2Deg);
    }

    /// <summary>The wreck a destroyed unit leaves at <paramref name="position"/>: <paramref name="sprite"/> (its last facing), charred.</summary>
    public static void Wreck(Vector2 position, Sprite sprite)
    {
        if (sprite == null) return;
        Slot s = Place(Wrecks, position, 0, sprite);
        if (s != null) s.Sprite.color = WreckTint;
    }

    /// <summary>Destroys every pooled sprite (tests, and the end of a match).</summary>
    public static void Clear()
    {
        if (runner != null)
        {
            if (Application.isPlaying) Object.Destroy(runner.gameObject);
            else Object.DestroyImmediate(runner.gameObject);
        }
        Reset();
    }

    private static void Reset()
    {
        runner = null;
        Created = 0;
        foreach (Pool p in All)
        {
            p.Slots.Clear();
            p.Next = 0;
        }
    }

    private static int Count(Pool p)
    {
        int n = 0;
        foreach (Slot s in p.Slots) if (s.Active) n++;
        return n;
    }

    private static Slot Place(Pool pool, Vector2 position, int direction, Sprite own = null)
    {
        if (runner == null)
        {
            Reset();
            runner = new GameObject("Decals").AddComponent<Runner>();
        }
        if (atlas == null || atlas.Texture == null) atlas = ArtAtlas.Load(ArtAtlas.Effects);
        if (own == null && (atlas == null || !atlas.Contains(pool.Id))) return null;

        Slot s;
        if (pool.Slots.Count < pool.Size)
        {
            var go = new GameObject(pool.Id);
            go.transform.SetParent(runner.transform, false);
            s = new Slot { Go = go, Sprite = go.AddComponent<SpriteRenderer>(), Wreck = own != null };
            s.Sprite.sortingOrder = WAR2D.Client.Render.SortingOrders.Decals + (own != null ? 1 : 0); // a wreck lies on its scorch
            pool.Slots.Add(s);
            Created++;
        }
        else
        {
            s = pool.Slots[pool.Next];
            pool.Next = (pool.Next + 1) % pool.Size;
        }
        s.Active = true;
        s.Start = Time.time;
        s.Direction = direction;
        s.Frame = 0;
        s.Sprite.sprite = own != null ? own : atlas.Sprite(pool.Id, direction * atlas.Frames(pool.Id));
        s.Go.transform.position = new Vector3(position.x, position.y, 0f);
        s.Go.SetActive(true);
        return s;
    }

    /// <summary>Steps every mark through its fade; hides the ones that are gone.</summary>
    private sealed class Runner : MonoBehaviour
    {
        private void Update()
        {
            float now = Time.time;
            foreach (Slot s in Wrecks.Slots)
            {
                if (!s.Active) continue;
                float alpha = WreckAlpha(now - s.Start);
                if (alpha <= 0f)
                {
                    s.Active = false;
                    s.Go.SetActive(false);
                    continue;
                }
                if (alpha < 1f) s.Sprite.color = new Color(WreckTint.r, WreckTint.g, WreckTint.b, alpha);
            }
            if (atlas == null) return;
            foreach (Pool pool in All)
            {
                if (pool == Wrecks) continue;
                int frames = atlas.Frames(pool.Id);
                foreach (Slot s in pool.Slots)
                {
                    if (!s.Active) continue;
                    int frame = FrameAt(now - s.Start, pool.Hold, pool.Fade, frames);
                    if (frame < 0)
                    {
                        s.Active = false;
                        s.Go.SetActive(false);
                        continue;
                    }
                    if (frame == s.Frame) continue;
                    s.Frame = frame;
                    s.Sprite.sprite = atlas.Sprite(pool.Id, s.Direction * frames + frame);
                }
            }
        }

        private void OnDestroy()
        {
            if (runner == this) Reset();
        }
    }
}
