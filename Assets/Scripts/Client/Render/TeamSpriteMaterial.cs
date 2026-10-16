using WAR2D.Dev.Logging;
using System.Collections.Generic;
using UnityEngine;
using WAR2D.Art;

namespace WAR2D.Client.Render
{
    /// <summary>
    /// Materials for sprites from the <c>Buildings</c> atlas: the <c>TeamSprite</c> shader with the atlas's
    /// team mask and an owner's colour, one per colour and ghost flag (cached). A palette change gives owners
    /// new colours, so asking again after <see cref="Palettes.Changed"/> returns the matching material.
    /// </summary>
    public static class TeamSpriteMaterial
    {
        public const string ShaderPath = "Shaders/TeamSprite";

        private static readonly Dictionary<(Color32, bool, string), Material> Cache = new Dictionary<(Color32, bool, string), Material>();

        /// <summary>
        /// The material for an owner's building (in the current palette); <paramref name="ghost"/> for last-seen
        /// ones. <paramref name="atlasName"/> picks whose team mask it reads (the rally flag is in the Effects atlas).
        /// </summary>
        public static Material For(int ownerId, bool ghost, string atlasName = ArtAtlas.Buildings) => ForColour(PlayerPalette.OfOwner(ownerId), ghost, atlasName);

        /// <summary>The material for a team colour.</summary>
        public static Material ForColour(Color32 colour, bool ghost, string atlasName = ArtAtlas.Buildings)
        {
            colour.a = 255;
            if (Cache.TryGetValue((colour, ghost, atlasName), out Material m) && m != null) return m;
            Shader shader = Resources.Load<Shader>(ShaderPath);
            if (shader == null)
            {
                WarLog.Error(LogCategory.Art, $"Resources/{ShaderPath}.shader is missing");
                shader = Shader.Find("Sprites/Default");
            }
            ArtAtlas atlas = ArtAtlas.Load(atlasName);
            m = new Material(shader) { name = $"Team {colour}{(ghost ? " ghost" : "")}" };
            m.SetTexture("_MaskTex", atlas != null && atlas.Mask != null ? atlas.Mask : Texture2D.blackTexture);
            m.SetColor("_TeamColor", colour);
            m.SetFloat("_TeamGain", InstancedUnitRenderer.TeamGain());
            m.SetFloat("_Ghost", ghost ? 1f : 0f);
            Cache[(colour, ghost, atlasName)] = m;
            return m;
        }

        /// <summary>The building sprite for <paramref name="type"/> (frame <paramref name="frame"/>) from the atlas, or null.</summary>
        public static Sprite Sprite(BuildingType type, int frame = 0) => ArtAtlas.Load(ArtAtlas.Buildings)?.Sprite(type.ToString(), frame);

        /// <summary>
        /// The facing a building of <paramref name="type"/> draws for a rotation in degrees (90° steps,
        /// anticlockwise from east); 0 for buildings drawn in one facing.
        /// </summary>
        public static int Facing(BuildingType type, float rotationDegrees)
        {
            int directions = ArtAtlas.Load(ArtAtlas.Buildings)?.Directions(type.ToString()) ?? 1;
            if (directions <= 1) return 0;
            int step = Mathf.RoundToInt(rotationDegrees / 90f);
            return (step % directions + directions) % directions;
        }

        /// <summary>The first frame of <paramref name="type"/>'s sprite in <paramref name="facing"/>.</summary>
        public static Sprite FacingSprite(BuildingType type, int facing)
        {
            ArtAtlas atlas = ArtAtlas.Load(ArtAtlas.Buildings);
            return atlas?.Sprite(type.ToString(), facing * atlas.Frames(type.ToString()));
        }
    }
}
