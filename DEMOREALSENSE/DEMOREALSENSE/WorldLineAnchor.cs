using System;
using System.Collections.Generic;
using System.Drawing;

namespace DEMOREALSENSE
{
    /// <summary>
    /// Ancre la ligne détectée dans le monde réel.
    ///
    /// Au moment où la ligne est placée, on mémorise :
    ///   - les endpoints 2D de la ligne
    ///   - une grille 5×5 de patches NCC couvrant toute l'image
    ///
    /// À chaque frame, on retrouve les patches dans la nouvelle image (SSD),
    /// on calcule la transformation affine (6 paramètres) entre les positions
    /// de référence et les positions courantes, et on l'applique à la ligne.
    ///
    /// Résultat : la ligne "colle" à la scène physique même si la caméra bouge.
    /// </summary>
    public sealed class WorldLineAnchor
    {
        private byte[]? _refGray;
        private byte[]? _curGray;
        private int _refW, _refH;

        private PointF[]? _keyPts;   // positions de référence des points de tracking
        private byte[][]? _patches;  // patches NCC 11×11 extraits à la référence

        private PointF _lineA, _lineB;  // endpoints mémorisés lors de l'ancrage
        private PointF _smoothedA, _smoothedB;  // EMA lissée pour supprimer les tremblements

        private const int PatchR  = 5;   // rayon patch → 11×11 px
        private const int SearchR = 25;  // fenêtre de recherche ±25 px (réduit pour moins de faux matches)
        private const int GridN   = 5;   // grille GridN×GridN = 25 points
        private const float SmoothAlpha = 0.05f;  // EMA : très faible = ligne très stable

        public bool IsAnchored => _refGray != null;

        // ── API publique ─────────────────────────────────────────────────────

        /// <summary>Initialise l'ancre. Appeler une seule fois quand la ligne apparaît.</summary>
        public void Anchor(byte[] rgb, int w, int h, PointF lineA, PointF lineB)
        {
            _lineA    = lineA;
            _lineB    = lineB;
            _smoothedA = lineA;
            _smoothedB = lineB;
            _refW  = w;
            _refH  = h;

            // Convertir en niveaux de gris
            EnsureBuffer(ref _refGray, w * h);
            ToGray(rgb, _refGray, w, h);

            // Grille uniforme de points de tracking
            int margin = PatchR + 2;
            _keyPts  = new PointF[GridN * GridN];
            _patches = new byte[GridN * GridN][];
            int idx = 0;
            for (int gy = 0; gy < GridN; gy++)
            for (int gx = 0; gx < GridN; gx++)
            {
                int px = margin + (w - 2 * margin) * gx / (GridN - 1);
                int py = margin + (h - 2 * margin) * gy / (GridN - 1);
                _keyPts[idx]  = new PointF(px, py);
                _patches[idx] = ExtractPatch(_refGray, w, h, px, py);
                idx++;
            }
        }

        /// <summary>Réinitialise l'ancre (ligne effacée).</summary>
        public void Reset()
        {
            _refGray   = null;
            _keyPts    = null;
            _patches   = null;
            _smoothedA = default;
            _smoothedB = default;
        }

        /// <summary>
        /// Retourne les endpoints courants de la ligne en tenant compte
        /// du mouvement de caméra estimé depuis l'ancrage.
        /// </summary>
        public (PointF a, PointF b) Track(byte[] rgb, int w, int h)
        {
            // Copies locales : évite la NullReferenceException si Reset() est appelé
            // depuis le thread UI pendant que Track() tourne sur le thread pipeline.
            var refGray = _refGray;
            var keyPts  = _keyPts;
            var patches = _patches;

            if (refGray == null || keyPts == null || patches == null)
                return (_smoothedA, _smoothedB);

            EnsureBuffer(ref _curGray, w * h);
            ToGray(rgb, _curGray, w, h);

            var srcPts = new List<PointF>(keyPts.Length);
            var dstPts = new List<PointF>(keyPts.Length);

            for (int i = 0; i < keyPts.Length; i++)
            {
                var found = FindBestMatch(_curGray, w, h, patches[i], keyPts[i]);
                if (found.HasValue)
                {
                    srcPts.Add(keyPts[i]);
                    dstPts.Add(found.Value);
                }
            }

            // Trop peu de matches ou transformation aberrante → garder la position lissée courante
            if (srcPts.Count < 4)
                return (_smoothedA, _smoothedB);

            if (!TryComputeAffine(srcPts, dstPts,
                    out float a, out float b, out float c,
                    out float d, out float e, out float f))
                return (_smoothedA, _smoothedB);

            // Sanity check : rejeter les transformations aberrantes
            if (Math.Abs(a - 1f) > 0.3f || Math.Abs(e - 1f) > 0.3f ||
                Math.Abs(c) > 60f         || Math.Abs(f) > 60f)
                return (_smoothedA, _smoothedB);

            PointF Apply(PointF p) => new PointF(a * p.X + b * p.Y + c,
                                                  d * p.X + e * p.Y + f);
            var rawA = Apply(_lineA);
            var rawB = Apply(_lineB);

            // EMA : amortit les micro-variations du SSD frame-à-frame
            _smoothedA = new PointF(
                _smoothedA.X + (rawA.X - _smoothedA.X) * SmoothAlpha,
                _smoothedA.Y + (rawA.Y - _smoothedA.Y) * SmoothAlpha);
            _smoothedB = new PointF(
                _smoothedB.X + (rawB.X - _smoothedB.X) * SmoothAlpha,
                _smoothedB.Y + (rawB.Y - _smoothedB.Y) * SmoothAlpha);

            return (_smoothedA, _smoothedB);
        }

        // ── Helpers internes ─────────────────────────────────────────────────

        private static void EnsureBuffer(ref byte[]? buf, int size)
        {
            if (buf == null || buf.Length < size) buf = new byte[size];
        }

        // BT.601 : Y = 0.299*R + 0.587*G + 0.114*B  (entier approché)
        private static void ToGray(byte[] rgb, byte[] gray, int w, int h)
        {
            int n = w * h;
            for (int i = 0, j = 0; i < n; i++, j += 3)
                gray[i] = (byte)((rgb[j] * 77 + rgb[j + 1] * 150 + rgb[j + 2] * 29) >> 8);
        }

        private static byte[] ExtractPatch(byte[] gray, int w, int h, int cx, int cy)
        {
            int side = 2 * PatchR + 1;
            var patch = new byte[side * side];
            int idx = 0;
            for (int dy = -PatchR; dy <= PatchR; dy++)
            for (int dx = -PatchR; dx <= PatchR; dx++)
            {
                int x = Math.Clamp(cx + dx, 0, w - 1);
                int y = Math.Clamp(cy + dy, 0, h - 1);
                patch[idx++] = gray[y * w + x];
            }
            return patch;
        }

        // Recherche grossière (pas=2) puis raffinement (pas=1, ±2 autour du meilleur)
        private static PointF? FindBestMatch(byte[] gray, int w, int h,
                                              byte[] refPatch, PointF refPos)
        {
            int cx = (int)refPos.X, cy = (int)refPos.Y;
            float bestSSD = float.MaxValue;
            int bestX = cx, bestY = cy;

            for (int dy = -SearchR; dy <= SearchR; dy += 2)
            for (int dx = -SearchR; dx <= SearchR; dx += 2)
            {
                int nx = cx + dx, ny = cy + dy;
                if (nx < PatchR || nx >= w - PatchR || ny < PatchR || ny >= h - PatchR) continue;
                float ssd = SSD(gray, w, refPatch, nx, ny);
                if (ssd < bestSSD) { bestSSD = ssd; bestX = nx; bestY = ny; }
            }

            for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
            {
                int nx = bestX + dx, ny = bestY + dy;
                if (nx < PatchR || nx >= w - PatchR || ny < PatchR || ny >= h - PatchR) continue;
                float ssd = SSD(gray, w, refPatch, nx, ny);
                if (ssd < bestSSD) { bestSSD = ssd; bestX = nx; bestY = ny; }
            }

            int side = 2 * PatchR + 1;
            if (bestSSD > 25f * 25f * side * side) return null;  // trop de bruit → rejeter

            return new PointF(bestX, bestY);
        }

        private static float SSD(byte[] gray, int w, byte[] patch, int cx, int cy)
        {
            float sum = 0;
            int pIdx = 0;
            for (int dy = -PatchR; dy <= PatchR; dy++)
            {
                int row = (cy + dy) * w + cx - PatchR;
                for (int dx = 0; dx < 2 * PatchR + 1; dx++)
                {
                    float diff = gray[row + dx] - patch[pIdx++];
                    sum += diff * diff;
                }
            }
            return sum;
        }

        /// <summary>
        /// Résoud la transformation affine 2D par moindres carrés.
        ///   x' = a*x + b*y + c
        ///   y' = d*x + e*y + f
        /// Via les équations normales (système 3×3 symétrique, inversé analytiquement).
        /// </summary>
        private static bool TryComputeAffine(List<PointF> src, List<PointF> dst,
            out float a, out float b, out float c,
            out float d, out float e, out float f)
        {
            a = 1; b = 0; c = 0; d = 0; e = 1; f = 0;
            int n = src.Count;
            if (n < 3) return false;

            double s_xx = 0, s_xy = 0, s_xo = 0, s_yy = 0, s_yo = 0;
            double rxx = 0, ryx = 0, rx = 0;
            double rxy_ = 0, ryy = 0, ry = 0;

            for (int i = 0; i < n; i++)
            {
                double x  = src[i].X, y  = src[i].Y;
                double xp = dst[i].X, yp = dst[i].Y;
                s_xx += x * x; s_xy += x * y; s_xo += x;
                s_yy += y * y; s_yo += y;
                rxx  += x * xp; ryx  += y * xp; rx  += xp;
                rxy_ += x * yp; ryy  += y * yp; ry  += yp;
            }
            double s_oo = n;

            double det = s_xx * (s_yy * s_oo - s_yo * s_yo)
                       - s_xy * (s_xy * s_oo - s_yo * s_xo)
                       + s_xo * (s_xy * s_yo - s_yy * s_xo);
            if (Math.Abs(det) < 1e-8) return false;

            double inv = 1.0 / det;
            double C00 =  s_yy * s_oo - s_yo * s_yo;
            double C01 = -(s_xy * s_oo - s_yo * s_xo);
            double C02 =  s_xy * s_yo  - s_yy * s_xo;
            double C11 =  s_xx * s_oo  - s_xo * s_xo;
            double C12 = -(s_xx * s_yo - s_xy * s_xo);
            double C22 =  s_xx * s_yy  - s_xy * s_xy;

            a = (float)((C00 * rxx  + C01 * ryx + C02 * rx)  * inv);
            b = (float)((C01 * rxx  + C11 * ryx + C12 * rx)  * inv);
            c = (float)((C02 * rxx  + C12 * ryx + C22 * rx)  * inv);
            d = (float)((C00 * rxy_ + C01 * ryy + C02 * ry)  * inv);
            e = (float)((C01 * rxy_ + C11 * ryy + C12 * ry)  * inv);
            f = (float)((C02 * rxy_ + C12 * ryy + C22 * ry)  * inv);

            return true;
        }
    }
}
