using UnityEngine;
using UnityEngine.UIElements;

namespace Tinyverse.UI
{
    public enum IconKind
    {
        None,
        Box, Sphere, Cylinder, Cone, Wedge, Plane,
        Place, Select, Move, Rotate, Scale,
        People, Build, Menu, More, Close, Undo, Redo, Copy, Chat, Grid, Duplicate, Trash, Lock,
    }

    /// <summary>
    /// A line icon drawn with Painter2D in a 16x16 design grid scaled to the element, stroked in the
    /// element's text colour, so it follows USS <c>color</c> and needs no image assets.
    /// </summary>
    public sealed class Icon : VisualElement
    {
        public const string UssClass = "tv-icon";
        const float K = 0.5523f; // bezier circle constant

        IconKind _kind;

        public IconKind Kind
        {
            get => _kind;
            set { _kind = value; MarkDirtyRepaint(); }
        }

        public Icon() : this(IconKind.None) { }

        public Icon(IconKind kind)
        {
            _kind = kind;
            AddToClassList(UssClass);
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
            RegisterCallback<CustomStyleResolvedEvent>(_ => MarkDirtyRepaint());
        }

        public static IconKind For(ShapeKind s) => s switch
        {
            ShapeKind.Box => IconKind.Box,
            ShapeKind.Sphere => IconKind.Sphere,
            ShapeKind.Cylinder => IconKind.Cylinder,
            ShapeKind.Cone => IconKind.Cone,
            ShapeKind.Wedge => IconKind.Wedge,
            _ => IconKind.Plane,
        };

        public static IconKind For(BuildTool t) => t switch
        {
            BuildTool.Place => IconKind.Place,
            BuildTool.Select => IconKind.Select,
            BuildTool.Move => IconKind.Move,
            BuildTool.Rotate => IconKind.Rotate,
            _ => IconKind.Scale,
        };

        void Draw(MeshGenerationContext ctx)
        {
            var r = contentRect;
            if (r.width < 1f || r.height < 1f || _kind == IconKind.None) return;
            float size = Mathf.Min(r.width, r.height);
            var p = new Pen(ctx.painter2D, new Vector2(r.x + (r.width - size) / 2f, r.y + (r.height - size) / 2f), size / 16f);
            var color = resolvedStyle.color;
            p.P.strokeColor = color;
            p.P.fillColor = color;
            p.P.lineWidth = Mathf.Max(1f, size / 12f);
            p.P.lineJoin = LineJoin.Round;
            p.P.lineCap = LineCap.Round;

            switch (_kind)
            {
                case IconKind.Box:
                case IconKind.Build:
                    p.Poly(true, 8, 1.5f, 14, 4.5f, 14, 11.5f, 8, 14.5f, 2, 11.5f, 2, 4.5f);
                    p.Poly(false, 2, 4.5f, 8, 7.5f, 14, 4.5f);
                    p.Poly(false, 8, 7.5f, 8, 14.5f);
                    break;
                case IconKind.Sphere:
                    p.Ellipse(8, 8, 6.5f, 6.5f);
                    p.Ellipse(8, 8, 6.5f, 2.2f);
                    break;
                case IconKind.Cylinder:
                    p.Ellipse(8, 3.8f, 5.5f, 2f);
                    p.Poly(false, 2.5f, 3.8f, 2.5f, 12.2f);
                    p.Poly(false, 13.5f, 3.8f, 13.5f, 12.2f);
                    p.HalfEllipse(8, 12.2f, 5.5f, 2f);
                    break;
                case IconKind.Cone:
                    p.Poly(false, 2.5f, 12, 8, 1.5f, 13.5f, 12);
                    p.Ellipse(8, 12, 5.5f, 2f);
                    break;
                case IconKind.Wedge:
                    p.Poly(true, 2, 13, 2, 6, 10, 13);
                    p.Poly(false, 2, 6, 7, 3, 15, 10, 10, 13);
                    break;
                case IconKind.Plane:
                    p.Poly(true, 5, 5, 15, 5, 11, 11, 1, 11);
                    break;
                case IconKind.Place:
                    p.Poly(false, 8, 2.5f, 8, 13.5f);
                    p.Poly(false, 2.5f, 8, 13.5f, 8);
                    break;
                case IconKind.Select:
                    p.Poly(true, 4, 2, 12.5f, 9.5f, 8.5f, 10, 10.5f, 14, 8.6f, 14.8f, 6.8f, 10.8f, 4, 13.5f);
                    break;
                case IconKind.Move:
                    p.Poly(false, 8, 1.5f, 8, 14.5f);
                    p.Poly(false, 1.5f, 8, 14.5f, 8);
                    p.Poly(false, 6, 3.5f, 8, 1.5f, 10, 3.5f);
                    p.Poly(false, 6, 12.5f, 8, 14.5f, 10, 12.5f);
                    p.Poly(false, 3.5f, 6, 1.5f, 8, 3.5f, 10);
                    p.Poly(false, 12.5f, 6, 14.5f, 8, 12.5f, 10);
                    break;
                case IconKind.Rotate:
                    p.ArcStroke(8, 8, 5.5f, -60f, 220f);
                    p.Poly(false, 10.5f, 1.8f, 11.2f, 3.4f, 9.4f, 4.6f);
                    break;
                case IconKind.Scale:
                    p.Poly(true, 2, 6, 10, 6, 10, 14, 2, 14);
                    p.Poly(false, 7, 9, 14, 2);
                    p.Poly(false, 9.5f, 2, 14, 2, 14, 6.5f);
                    break;
                case IconKind.People:
                    p.Ellipse(6, 5, 2.5f, 2.5f);
                    p.HalfEllipseTop(6, 14, 4.5f, 4.5f);
                    p.Ellipse(11.5f, 6, 2f, 2f);
                    p.Poly(false, 11, 10, 15, 13.5f);
                    break;
                case IconKind.Menu:
                    p.Poly(false, 2.5f, 4, 13.5f, 4);
                    p.Poly(false, 2.5f, 8, 13.5f, 8);
                    p.Poly(false, 2.5f, 12, 13.5f, 12);
                    break;
                case IconKind.More:
                    p.Dot(8, 3.5f, 1.3f);
                    p.Dot(8, 8, 1.3f);
                    p.Dot(8, 12.5f, 1.3f);
                    break;
                case IconKind.Close:
                    p.Poly(false, 3.5f, 3.5f, 12.5f, 12.5f);
                    p.Poly(false, 12.5f, 3.5f, 3.5f, 12.5f);
                    break;
                case IconKind.Undo:
                    p.Poly(false, 5, 2.5f, 2, 5.5f, 5, 8.5f);
                    p.Curve(2, 5.5f, 10, 5.5f, 14, 7.5f, 14, 10.5f, 14, 13.5f, 10, 13.5f, 7, 13.5f);
                    break;
                case IconKind.Redo:
                    p.Poly(false, 11, 2.5f, 14, 5.5f, 11, 8.5f);
                    p.Curve(14, 5.5f, 6, 5.5f, 2, 7.5f, 2, 10.5f, 2, 13.5f, 6, 13.5f, 9, 13.5f);
                    break;
                case IconKind.Copy:
                case IconKind.Duplicate:
                    p.Poly(true, 5.5f, 5.5f, 14, 5.5f, 14, 14, 5.5f, 14);
                    p.Poly(false, 2, 10.5f, 2, 2, 10.5f, 2);
                    break;
                case IconKind.Chat:
                    p.Poly(true, 2, 3, 14, 3, 14, 11, 7, 11, 4, 14, 4, 11, 2, 11);
                    break;
                case IconKind.Grid:
                    p.Poly(true, 2, 2, 14, 2, 14, 14, 2, 14);
                    p.Poly(false, 6, 2, 6, 14);
                    p.Poly(false, 10, 2, 10, 14);
                    p.Poly(false, 2, 6, 14, 6);
                    p.Poly(false, 2, 10, 14, 10);
                    break;
                case IconKind.Trash:
                    p.Poly(false, 2, 4, 14, 4);
                    p.Poly(false, 6, 4, 6.5f, 2, 9.5f, 2, 10, 4);
                    p.Poly(false, 3.5f, 4, 4.5f, 14.5f, 11.5f, 14.5f, 12.5f, 4);
                    break;
                case IconKind.Lock:
                    p.Poly(true, 3, 7, 13, 7, 13, 14.5f, 3, 14.5f);
                    p.ArcStroke(8, 7, 3.5f, 180f, 360f);
                    break;
            }
        }

        /// <summary>Draws in a 16x16 grid.</summary>
        readonly struct Pen
        {
            public readonly Painter2D P;
            readonly Vector2 _o;
            readonly float _s;

            public Pen(Painter2D p, Vector2 origin, float scale)
            {
                P = p;
                _o = origin;
                _s = scale;
            }

            Vector2 V(float x, float y) => _o + new Vector2(x, y) * _s;

            public void Poly(bool closed, params float[] xy)
            {
                P.BeginPath();
                P.MoveTo(V(xy[0], xy[1]));
                for (int i = 2; i + 1 < xy.Length; i += 2) P.LineTo(V(xy[i], xy[i + 1]));
                if (closed) P.ClosePath();
                P.Stroke();
            }

            public void Curve(float x0, float y0, params float[] c)
            {
                P.BeginPath();
                P.MoveTo(V(x0, y0));
                for (int i = 0; i + 5 < c.Length; i += 6)
                    P.BezierCurveTo(V(c[i], c[i + 1]), V(c[i + 2], c[i + 3]), V(c[i + 4], c[i + 5]));
                P.Stroke();
            }

            public void Ellipse(float cx, float cy, float rx, float ry)
            {
                P.BeginPath();
                P.MoveTo(V(cx + rx, cy));
                P.BezierCurveTo(V(cx + rx, cy + ry * K), V(cx + rx * K, cy + ry), V(cx, cy + ry));
                P.BezierCurveTo(V(cx - rx * K, cy + ry), V(cx - rx, cy + ry * K), V(cx - rx, cy));
                P.BezierCurveTo(V(cx - rx, cy - ry * K), V(cx - rx * K, cy - ry), V(cx, cy - ry));
                P.BezierCurveTo(V(cx + rx * K, cy - ry), V(cx + rx, cy - ry * K), V(cx + rx, cy));
                P.ClosePath();
                P.Stroke();
            }

            /// <summary>Lower half of an ellipse.</summary>
            public void HalfEllipse(float cx, float cy, float rx, float ry)
            {
                P.BeginPath();
                P.MoveTo(V(cx + rx, cy));
                P.BezierCurveTo(V(cx + rx, cy + ry * K), V(cx + rx * K, cy + ry), V(cx, cy + ry));
                P.BezierCurveTo(V(cx - rx * K, cy + ry), V(cx - rx, cy + ry * K), V(cx - rx, cy));
                P.Stroke();
            }

            /// <summary>Upper half of an ellipse.</summary>
            public void HalfEllipseTop(float cx, float cy, float rx, float ry)
            {
                P.BeginPath();
                P.MoveTo(V(cx - rx, cy));
                P.BezierCurveTo(V(cx - rx, cy - ry * K), V(cx - rx * K, cy - ry), V(cx, cy - ry));
                P.BezierCurveTo(V(cx + rx * K, cy - ry), V(cx + rx, cy - ry * K), V(cx + rx, cy));
                P.Stroke();
            }

            public void ArcStroke(float cx, float cy, float r, float fromDeg, float toDeg)
            {
                P.BeginPath();
                P.Arc(V(cx, cy), r * _s, Angle.Degrees(fromDeg), Angle.Degrees(toDeg), ArcDirection.Clockwise);
                P.Stroke();
            }

            public void Dot(float cx, float cy, float r)
            {
                P.BeginPath();
                P.Arc(V(cx, cy), r * _s, Angle.Degrees(0), Angle.Degrees(360), ArcDirection.Clockwise);
                P.ClosePath();
                P.Fill();
            }
        }
    }
}
