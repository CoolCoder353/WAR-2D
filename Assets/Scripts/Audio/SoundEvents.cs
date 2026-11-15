using System;
using System.Collections.Generic;
using UnityEngine;
using WAR2D.Art;

namespace WAR2D.Audio
{
    /// <summary>What a sound is for: the mix treats a category as one group (asset-generation spec §3.8, §5.3).</summary>
    public enum SoundCategory { Ui, Alert, Blast, Shot, Impact, Work, Ambient, Stinger }

    /// <summary>One row of the sound event table: how the game plays the sound with this id.</summary>
    [Serializable]
    public sealed class SoundEvent
    {
        public string id;
        public SoundCategory category;
        /// <summary>A trim on the sound's built level, in decibels (0 plays it as built).</summary>
        [Range(-24f, 12f)] public float gainDb;
        /// <summary>Seconds before the same sound may play again.</summary>
        [Range(0f, 5f)] public float cooldown = AudioService.PerSoundCooldown;
        /// <summary>Which sound keeps its voice when the pool is full (higher wins); not used until the mix work of §5.3.</summary>
        [Range(0, 10)] public int priority = 5;
        /// <summary>How many built variants the sound has; 1 until §5.2.</summary>
        [Range(1, 8)] public int variants = 1;

        /// <summary><see cref="gainDb"/> as a factor on volume (1 at 0 dB).</summary>
        public float Gain => gainDb == 0f ? 1f : Mathf.Pow(10f, gainDb / 20f);
    }

    /// <summary>The file <c>Resources/Audio/SoundEvents.json</c>.</summary>
    [Serializable]
    public sealed class SoundEventTable
    {
        public SoundEvent[] events = new SoundEvent[0];
    }

    /// <summary>
    /// The sound event table: one row per sound effect, read strictly (<see cref="SpecJson"/>) from
    /// <c>Resources/Audio/SoundEvents.json</c>. <see cref="AudioService"/> takes a sound's cooldown and gain from its row;
    /// callers keep passing ids. A sound with no row plays with the defaults (the tests require a row for every spec).
    /// </summary>
    public static class SoundEvents
    {
        public const string Resource = "Audio/SoundEvents";

        private static readonly SoundEvent Default = new SoundEvent();
        private static Dictionary<string, SoundEvent> rows;

        /// <summary>The row of sound <paramref name="id"/>, or the defaults when it has none.</summary>
        public static SoundEvent Of(string id)
        {
            if (rows == null)
            {
                TextAsset file = Resources.Load<TextAsset>(Resource);
                rows = file != null ? Index(Read(file.text, Resource)) : new Dictionary<string, SoundEvent>();
            }
            return id != null && rows.TryGetValue(id, out SoundEvent row) ? row : Default;
        }

        /// <summary>The table in <paramref name="json"/>; throws a <see cref="SpecException"/> on a bad file or a repeated id.</summary>
        public static SoundEventTable Read(string json, string file)
        {
            SoundEventTable table = SpecJson.Read<SoundEventTable>(json, file);
            Index(table, file);
            return table;
        }

        private static Dictionary<string, SoundEvent> Index(SoundEventTable table, string file = Resource)
        {
            var map = new Dictionary<string, SoundEvent>(StringComparer.Ordinal);
            foreach (SoundEvent row in table.events)
            {
                if (string.IsNullOrEmpty(row.id)) throw new SpecException($"{file}: a row has no id");
                if (map.ContainsKey(row.id)) throw new SpecException($"{file}: '{row.id}' is listed twice");
                map[row.id] = row;
            }
            return map;
        }
    }
}
