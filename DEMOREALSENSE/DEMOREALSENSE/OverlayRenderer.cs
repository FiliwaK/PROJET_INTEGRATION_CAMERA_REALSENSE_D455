using System.Drawing;
using System.Drawing.Drawing2D;

namespace DEMOREALSENSE
{
    public sealed class OverlayRenderer
    {
        public int ManualBoxHalf { get; set; } = 12;

        public void DrawManualBox(Bitmap bmp, int x, int y)
            => FrameBitmapConverter.DrawGreenBox(bmp, x, y, ManualBoxHalf);

        public void DrawAutoCircle(Bitmap bmp, int x, int y, int radiusPx = 12)
        {
            using var g = Graphics.FromImage(bmp);
            using var pen = new Pen(Color.DeepSkyBlue, 2f);
            g.DrawEllipse(pen, x - radiusPx, y - radiusPx, radiusPx * 2, radiusPx * 2);
        }

        public void DrawIaCircle(Bitmap bmp, int x, int y, int radiusPx = 16)
        {
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Color.Magenta, 2.5f);
            g.DrawRectangle(pen, x - radiusPx, y - radiusPx, radiusPx * 2, radiusPx * 2);
            using var penC = new Pen(Color.Magenta, 1.5f);
            g.DrawLine(penC, x - 4, y, x + 4, y);
            g.DrawLine(penC, x, y - 4, x, y + 4);
        }

        /// <summary>
        /// Dessine la ligne ancrée dans le monde (endpoints calculés par WorldLineAnchor).
        /// </summary>
        public void DrawLineOverlay(Bitmap bmp, PointF a, PointF b, float lineWidthPx = 6f)
            => DrawLinePerspective(bmp, a, b, lineWidthPx);

        /// <summary>
        /// Dessine la ligne détectée (mode sans ancrage ou avec points Ctrl+Click).
        /// </summary>
        public void DrawLineOverlay(Bitmap bmp, ClickLineDetector lineDetector, object lineLock,
                                    float lineWidthPx = 6f)
        {
            bool hasLine;
            lock (lineLock)
            {
                hasLine = lineDetector.HasLine;
                if (!hasLine && lineDetector.Samples.Count == 0) return;
            }

            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            lock (lineLock)
            {
                foreach (var p in lineDetector.Samples)
                    g.FillEllipse(Brushes.Lime, p.X - 3, p.Y - 3, 6, 6);
            }

            if (!hasLine) return;

            var bounds = new RectangleF(0, 0, bmp.Width - 1, bmp.Height - 1);
            if (!lineDetector.TryGetSegmentWithin(bounds, out var a, out var b)) return;

            DrawLinePerspective(bmp, a, b, lineWidthPx);
        }

        /// <summary>
        /// Rendu perspective commun : trapèze large en bas (proche), étroit en haut (loin).
        /// La largeur est normalisée au centre de la ligne pour correspondre à lineWidthPx
        /// à la profondeur réelle de la ligne.
        /// </summary>
        private static void DrawLinePerspective(Bitmap bmp, PointF a, PointF b, float lineWidthPx)
        {
            float baseHalfW = System.Math.Max(3f, lineWidthPx) / 2f;

            float dx = b.X - a.X, dy = b.Y - a.Y;
            float len = System.MathF.Sqrt(dx * dx + dy * dy);
            if (len < 1f) return;

            // Normale perpendiculaire à la ligne
            float nx = -dy / len;
            float ny =  dx / len;

            // ── Perspective normalisée au centre de la ligne ───────────────
            // À y = centerY  → scale = 1.0  (largeur = lineWidthPx)
            // À y < centerY  → scale < 1.0  (plus loin, plus étroit)
            // À y > centerY  → scale > 1.0  (plus proche, plus large)
            float centerY = (a.Y + b.Y) * 0.5f;
            float imgH    = bmp.Height;

            // Facteur perspective : basé sur distance à l'horizon estimé
            float vanishY = imgH * 0.15f;  // horizon à 15% du haut (effet moins agressif)
            float refDist = centerY - vanishY;
            if (refDist < 1f) refDist = 1f;

            float PerspScale(float y)
            {
                float d = y - vanishY;
                return System.Math.Clamp(d / refDist, 0.2f, 2.0f);  // max 2× (était 4×)
            }

            float hwA = baseHalfW * PerspScale(a.Y);
            float hwB = baseHalfW * PerspScale(b.Y);

            // Coins du trapèze
            var p0 = new PointF(a.X + nx * hwA, a.Y + ny * hwA);
            var p1 = new PointF(b.X + nx * hwB, b.Y + ny * hwB);
            var p2 = new PointF(b.X - nx * hwB, b.Y - ny * hwB);
            var p3 = new PointF(a.X - nx * hwA, a.Y - ny * hwA);

            var trapeze = new PointF[] { p0, p1, p2, p3 };

            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Remplissage blanc semi-transparent (couleur bande de court)
            using var fill = new SolidBrush(Color.FromArgb(120, 255, 255, 255));
            g.FillPolygon(fill, trapeze);

            // Bordures
            using var penIn  = new Pen(Color.White, 2f);
            using var penOut = new Pen(Color.OrangeRed, 2f);
            g.DrawLine(penIn,  p0, p1);
            g.DrawLine(penOut, p3, p2);

            using var penCap = new Pen(Color.FromArgb(180, 255, 255, 255), 1.5f);
            g.DrawLine(penCap, p0, p3);
            g.DrawLine(penCap, p1, p2);

            // Axe central pointillé jaune
            using var penAxis = new Pen(Color.Yellow, 1.2f) { DashStyle = DashStyle.Dash };
            g.DrawLine(penAxis, a, b);
        }

    }
}