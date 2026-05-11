using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DEMOREALSENSE
{
    /// <summary>
    /// Conversion RGB8 → Bitmap — zéro allocation par frame.
    /// Buffer ThreadStatic réutilisé indéfiniment.
    /// </summary>
    public static class FrameBitmapConverter
    {
        [ThreadStatic]
        private static byte[]? _buf;

        private static byte[] PrepareSwap(byte[] rgb, int w, int h)
        {
            int total = w * h * 3;
            if (_buf == null || _buf.Length < total)
                _buf = new byte[total];

            for (int i = 0; i < total; i += 3)
            {
                _buf[i] = rgb[i + 2];
                _buf[i + 1] = rgb[i + 1];
                _buf[i + 2] = rgb[i];
            }
            return _buf;
        }

        /// <summary>Écrit dans un Bitmap existant — zéro allocation.</summary>
        public static void WriteRgbToBitmap(byte[] rgb, int w, int h, Bitmap target)
        {
            byte[] buf = PrepareSwap(rgb, w, h);
            int srcStride = w * 3;
            var rect = new Rectangle(0, 0, w, h);
            var data = target.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                if (data.Stride == srcStride)
                    Marshal.Copy(buf, 0, data.Scan0, srcStride * h);
                else
                    for (int y = 0; y < h; y++)
                        Marshal.Copy(buf, y * srcStride, data.Scan0 + y * data.Stride, srcStride);
            }
            finally { target.UnlockBits(data); }
        }

        /// <summary>Crée un nouveau Bitmap (pour photos, pas pour la boucle principale).</summary>
        public static Bitmap RgbToBitmap24bpp(byte[] rgb, int w, int h)
        {
            byte[] buf = PrepareSwap(rgb, w, h);
            int srcStride = w * 3;
            var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            var rect = new Rectangle(0, 0, w, h);
            var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, bmp.PixelFormat);
            try
            {
                if (data.Stride == srcStride)
                    Marshal.Copy(buf, 0, data.Scan0, srcStride * h);
                else
                    for (int y = 0; y < h; y++)
                        Marshal.Copy(buf, y * srcStride, data.Scan0 + y * data.Stride, srcStride);
            }
            finally { bmp.UnlockBits(data); }
            return bmp;
        }

        public static void DrawGreenBox(Bitmap bmp, int px, int py, int boxHalf = 12)
        {
            using var g = Graphics.FromImage(bmp);
            using var pen = new Pen(Color.Lime, 2);
            int x0 = px - boxHalf, y0 = py - boxHalf, size = boxHalf * 2;
            if (x0 < 0) x0 = 0;
            if (y0 < 0) y0 = 0;
            if (x0 + size >= bmp.Width) x0 = Math.Max(0, bmp.Width - size - 1);
            if (y0 + size >= bmp.Height) y0 = Math.Max(0, bmp.Height - size - 1);
            g.DrawRectangle(pen, x0, y0, size, size);
            g.FillEllipse(Brushes.Lime, px - 2, py - 2, 5, 5);
        }
    }
}