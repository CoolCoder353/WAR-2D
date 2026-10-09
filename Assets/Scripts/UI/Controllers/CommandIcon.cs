using UnityEngine;
using UnityEngine.UIElements;

namespace WAR2D.UI
{
    /// <summary>The command card's icons (Figma: Components, icons Move … Wall).</summary>
    public enum IconKind { Move, AttackMove, Stop, Hold, Miner, Spawner, Wall }

    /// <summary>
    /// A 24-unit line icon drawn with <see cref="Painter2D"/> from the approved Figma vectors (2-unit
    /// round strokes), scaled to the element's size. Stroke colour is the element's USS <c>color</c>, so
    /// button states recolour it.
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
            set { icon = value; MarkDirtyRepaint(); }
        }

        public CommandIcon()
        {
            AddToClassList("command-icon");
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
            RegisterCallback<CustomStyleResolvedEvent>(_ => MarkDirtyRepaint());
        }

        private void Draw(MeshGenerationContext context)
        {
            float s = Mathf.Min(contentRect.width, contentRect.height) / 24f;
            if (s <= 0f) return;
            Painter2D p = context.painter2D;
            p.strokeColor = resolvedStyle.color;
            p.lineWidth = 2f * s;
            p.lineCap = LineCap.Round;
            p.lineJoin = LineJoin.Round;
            Vector2 V(float x, float y) => new Vector2(x * s, y * s);

            void Line(params float[] xy)
            {
                p.BeginPath();
                p.MoveTo(V(xy[0], xy[1]));
                for (int i = 2; i < xy.Length; i += 2) p.LineTo(V(xy[i], xy[i + 1]));
                p.Stroke();
            }

            switch (icon)
            {
                case IconKind.Move:
                    Line(5, 12, 19, 12);
                    Line(13, 18, 19, 12, 13, 6);
                    break;
                case IconKind.AttackMove:
                    p.BeginPath();
                    p.Arc(V(12, 12), 7f * s, 0f, 360f);
                    p.Stroke();
                    Line(12, 2, 12, 7);
                    Line(12, 17, 12, 22);
                    Line(2, 12, 7, 12);
                    Line(17, 12, 22, 12);
                    break;
                case IconKind.Stop:
                    p.BeginPath();
                    p.MoveTo(V(7, 6));
                    p.ArcTo(V(18, 6), V(18, 18), s);
                    p.ArcTo(V(18, 18), V(6, 18), s);
                    p.ArcTo(V(6, 18), V(6, 6), s);
                    p.ArcTo(V(6, 6), V(18, 6), s);
                    p.ClosePath();
                    p.Stroke();
                    break;
                case IconKind.Hold:
                    p.BeginPath();
                    p.MoveTo(V(12, 3));
                    p.LineTo(V(19, 6));
                    p.LineTo(V(19, 11));
                    p.BezierCurveTo(V(19, 16), V(16, 19), V(12, 21));
                    p.BezierCurveTo(V(8, 19), V(5, 16), V(5, 11));
                    p.LineTo(V(5, 6));
                    p.ClosePath();
                    p.Stroke();
                    break;
                case IconKind.Miner:
                    Line(3, 9, 12, 20, 21, 9, 18, 4, 6, 4, 3, 9, 21, 9);
                    break;
                case IconKind.Spawner:
                    Line(3, 21, 3, 10, 8, 13, 8, 10, 13, 13, 13, 6, 21, 10, 21, 21, 3, 21);
                    break;
                case IconKind.Wall:
                    Line(3, 5, 21, 5, 21, 19, 3, 19, 3, 5);
                    Line(3, 12, 21, 12);
                    Line(9, 5, 9, 12);
                    Line(15, 12, 15, 19);
                    break;
            }
        }
    }
}
