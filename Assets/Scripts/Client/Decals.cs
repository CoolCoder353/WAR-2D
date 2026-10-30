using System.Collections.Generic;
using UnityEngine;
using WAR2D.Art;
using WAR2D.Art.Render;

/// <summary>
/// Client-only ground marks from the <c>Effects</c> atlas (polish items 3 and G): scorch where a unit died, rubble over a
/// destroyed building's footprint, and tread marks behind vehicles. Each mark holds, then fades through its dithered frames
/// and goes. Like <see cref="Effects"/>, each kind has a fixed pool of sprites made once; when a pool is full the oldest mark
/// is reused. Marks only follow deaths and movement the client already draws.
/// </summary>
public static class Decals
{
    /// <summary>Pool sizes: the most of each kind on the map at once.</summary>
    public const int ScorchPool = 256, LargeScorchPool = 64, RubblePool = 24, LargeRubblePool = 8, TrackPool = 768;

    /// <summary>Seconds a scorch holds before it fades, and the seconds the fade takes.</summary>
    public const float ScorchHold = 15f, ScorchFade = 10f;

    /// <summary>Seconds rubble holds before it fades, and the seconds the fade takes.</summary>
    public const float RubbleHold = 25f, RubbleFade = 10f;

    /// <summary>Seconds a tread mark holds before it fades, and the seconds the fade takes.</summary>
    public const float TrackHold = 3f, TrackFade = 4f;

    private sealed class Slot
    {
        public GameObject Go;
        public SpriteRenderer Sprite;
        public float Start;
        public int Direction, Frame;
        public bool Active;
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
    private static readonly Pool[] All = { Scorches, LargeScorches, Rubble1, Rubble2, Rubble3, Rubble5, Tracks };
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

    /// <summary>The tread mark direction (0 = east–west, then 45° steps anticlockwise) for a heading in radians.</summary>
    public static int TrackDirection(float heading)
    {
        int d = Mathf.RoundToInt(heading / (Mathf.PI / 4f));
        return ((d % EffectGen.TrackDirections) + EffectGen.TrackDirections) % EffectGen.TrackDirections;
    }

    /// <summary>A tread mark centred on <paramref name="position"/>, laid along <paramref name="heading"/> (radians).</summary>
    public static void Track(Vector2 position, float heading) => Place(Tracks, position, TrackDirection(heading));

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

    private static void Place(Pool pool, Vector2 position, int direction)
    {
        if (runner == null)
        {
            Reset();
            runner = new GameObject("Decals").AddComponent<Runner>();
        }
        if (atlas == null || atlas.Texture == null) atlas = ArtAtlas.Load(ArtAtlas.Effects);
        if (atlas == null || !atlas.Contains(pool.Id)) return;

        Slot s;
        if (pool.Slots.Count < pool.Size)
        {
            var go = new GameObject(pool.Id);
            go.transform.SetParent(runner.transform, false);
            s = new Slot { Go = go, Sprite = go.AddComponent<SpriteRenderer>() };
            s.Sprite.sortingOrder = WAR2D.Client.Render.SortingOrders.Decals;
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
        s.Sprite.sprite = atlas.Sprite(pool.Id, direction * atlas.Frames(pool.Id));
        s.Go.transform.position = new Vector3(position.x, position.y, 0f);
        s.Go.SetActive(true);
    }

    /// <summary>Steps every mark through its fade; hides the ones that are gone.</summary>
    private sealed class Runner : MonoBehaviour
    {
        private void Update()
        {
            if (atlas == null) return;
            float now = Time.time;
            foreach (Pool pool in All)
            {
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
