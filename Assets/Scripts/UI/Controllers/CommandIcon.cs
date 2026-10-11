using UnityEngine;
using UnityEngine.UIElements;
using WAR2D.Art;

namespace WAR2D.UI
{
    /// <summary>The command card's icons (Figma: <c>Icons &amp; cursors – v0.8</c>).</summary>
    public enum IconKind { Move, AttackMove, Stop, Hold, Miner, Spawner, Wall }

    /// <summary>
    /// A 16×16 pixel icon from <see cref="PixelIcons"/>, drawn point-filtered at the element's size. It is
    /// tinted with the element's USS <c>color</c>, so button states recolour it as they did the v0.6 line icons.
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
            RegisterCallback<CustomStyleResolvedEvent>(_ => Tint());
            Apply();
        }

        private void Apply()
        {
            style.backgroundImage = new StyleBackground(PixelIcons.Texture(IdOf(icon)));
            Tint();
        }

        private void Tint() => style.unityBackgroundImageTintColor = resolvedStyle.color;
    }
}
