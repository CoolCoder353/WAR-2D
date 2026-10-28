using UnityEngine;
using UnityEngine.UIElements;
using WAR2D.Art;

namespace WAR2D.UI
{
    /// <summary>The command card's icons (Figma: <c>Icons &amp; cursors – v0.8</c>).</summary>
    public enum IconKind
    {
        Move, AttackMove, Stop, Hold, Miner, Spawner, Wall, Radar, Dig, Cancel, Bomb, Repair, Demolish,
        // v0.12: units (named as UnitType), Detonate and the producers.
        Builder, Tank, Digger, Scout, Heavy, Artillery, Sniper, Flamethrower, SuicideBomber, Detonate, Barracks, HeavyFactory, SiegeWorkshop,
        // v0.13: the defences (named as BuildingType).
        GunTurret, Jammer,
        // v0.14: the AI player tag.
        Ai,
        // v0.15 QoL: Jump to HQ, the quick-select counts, Rally all and the minimap filters.
        Hq, IdleBuilders, Army, RallyAll, Eye,
    }

    /// <summary>
    /// A 16×16 pixel icon from <see cref="PixelIcons"/>, drawn point-filtered at the element's size. Its tint
    /// comes from <c>Theme.uss</c> per button state (the bitmaps are white), like the v0.6 line icons' stroke.
    /// </summary>
    [UxmlElement]
    public partial class CommandIcon : VisualElement
    {
        private IconKind icon;

        /// <summary>Which icon to draw.</summary>
        [UxmlAttribute]
        public IconKind Icon
        {
            get => icon;
            set { icon = value; Apply(); }
        }

        /// <summary>The <see cref="PixelIcons"/> id of <paramref name="kind"/>.</summary>
        public static string IdOf(IconKind kind) => "Icon" + kind;

        public CommandIcon()
        {
            AddToClassList("command-icon");
            pickingMode = PickingMode.Ignore;
            style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
            Apply();
        }

        private void Apply() => style.backgroundImage = new StyleBackground(PixelIcons.Texture(IdOf(icon)));
    }

    /// <summary>The AI tag's robot head from <see cref="PixelIcons"/>, in its own colours (blue eyes), 16 px.</summary>
    [UxmlElement]
    public partial class AiIcon : VisualElement
    {
        public AiIcon()
        {
            AddToClassList("ai-tag__icon");
            pickingMode = PickingMode.Ignore;
            style.backgroundImage = new StyleBackground(PixelIcons.Texture(CommandIcon.IdOf(IconKind.Ai)));
            style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
        }
    }

    /// <summary>The rally flag from <see cref="PixelIcons"/>, in its own colours (the production queue's rally hint).</summary>
    [UxmlElement]
    public partial class RallyIcon : VisualElement
    {
        public RallyIcon()
        {
            pickingMode = PickingMode.Ignore;
            style.backgroundImage = new StyleBackground(PixelIcons.Texture("IconRallyFlag"));
            style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
        }
    }
}
