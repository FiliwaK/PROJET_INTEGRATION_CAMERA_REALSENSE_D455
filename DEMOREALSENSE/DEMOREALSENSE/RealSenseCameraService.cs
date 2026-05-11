using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Intel.RealSense;

namespace DEMOREALSENSE
{
    /// <summary>
    /// Service caméra RealSense.
    /// Auto-détecte USB2/USB3 et choisit la config compatible :
    ///   USB3 → 640×480 Color + 848×480 Depth @ 30fps
    ///   USB2 → 424×240 Color + 424×240 Depth @ 15fps (seul débit supporté)
    /// </summary>
    public sealed class RealSenseCameraService : IDisposable
    {
        private Pipeline? _pipe;

        public bool IsRunning { get; private set; }
        public float DepthUnits { get; private set; } = 0.001f;
        public bool IsUsb2 { get; private set; } = false;

        public int ColorW { get; private set; }
        public int ColorH { get; private set; }
        public int DepthW { get; private set; }
        public int DepthH { get; private set; }

        // Buffers pré-alloués — remplis à chaque frame, jamais recréés
        private byte[]? _rgbBuf;
        private byte[]? _tmpBuf;
        private byte[]? _yuyvBuf;
        private ushort[]? _depthBuf;
        private Format _colorFormat = Format.Rgb8;

        public void Start(int w = 640, int h = 480, int fps = 30)
        {
            Stop();

            // ── Détection USB speed ──────────────────────────────────────────
            using (var ctx = new Context())
            {
                var devices = ctx.QueryDevices();
                if (devices.Count == 0)
                    throw new InvalidOperationException("Aucune RealSense détectée (USB/driver).");

                try
                {
                    var dev = devices[0];
                    string usbType = dev.Info.GetInfo(CameraInfo.UsbTypeDescriptor);
                    IsUsb2 = !usbType.StartsWith("3");
                    Debug.WriteLine($"[Camera] USB speed : {usbType}  →  {(IsUsb2 ? "USB2 — config dégradée" : "USB3 OK")}");
                }
                catch { }
            }

            // ── Choix de config selon USB speed ─────────────────────────────
            // Débit Color RGB8 + Depth Z16 :
            //   640×480@30fps = 27.6 + 848×480@30fps = 23.6 → 51 MB/s → USB3 uniquement
            //   424×240@15fps =  4.6 + 424×240@15fps =  3.1 →  7.7 MB/s → USB2 OK

            // Configurations à essayer dans l'ordre.
            // USB2 : D455 requiert USB3 officiellement — on essaie tout ce qui est possible.
            //        YUYV (2 B/px) utilisé en priorité pour réduire le débit.
            //        La conversion YUYV→RGB se fait dans TryGetAlignedFrames.
            var attempts = IsUsb2
                ? new[] {
                    // YUYV color-only (format le plus léger, ~2.4 MB/s)
                    (cw: 424, ch: 240, cfps:  6, fmt: Format.Yuyv, dw:   0, dh:   0, depthOn: false),
                    (cw: 424, ch: 240, cfps: 15, fmt: Format.Yuyv, dw:   0, dh:   0, depthOn: false),
                    // RGB8 color-only
                    (cw: 424, ch: 240, cfps:  6, fmt: Format.Rgb8, dw:   0, dh:   0, depthOn: false),
                    (cw: 424, ch: 240, cfps: 15, fmt: Format.Rgb8, dw:   0, dh:   0, depthOn: false),
                    (cw: 640, ch: 480, cfps:  6, fmt: Format.Rgb8, dw:   0, dh:   0, depthOn: false),
                  }
                : new[] {
                    (cw: 640, ch: 480, cfps: 30, fmt: Format.Rgb8, dw: 848, dh: 480, depthOn: true),
                    (cw: 640, ch: 480, cfps: 15, fmt: Format.Rgb8, dw: 848, dh: 480, depthOn: true),
                    (cw: 640, ch: 480, cfps: 30, fmt: Format.Rgb8, dw:   0, dh:   0, depthOn: false),
                  };

            PipelineProfile? profile = null;
            int cw = 0, ch = 0, dw = 0, dh = 0;
            _colorFormat = Format.Rgb8;

            foreach (var a in attempts)
            {
                try
                {
                    _pipe?.Dispose();
                    _pipe = new Pipeline();
                    var cfg = new Config();
                    cfg.EnableStream(Intel.RealSense.Stream.Color, a.cw, a.ch, a.fmt, a.cfps);
                    if (a.depthOn)
                        cfg.EnableStream(Intel.RealSense.Stream.Depth, a.dw, a.dh, Format.Z16, a.cfps);

                    string depthStr = a.depthOn ? $" + Depth={a.dw}×{a.dh}" : " (Color seul)";
                    Debug.WriteLine($"[Camera] Tentative : Color={a.cw}×{a.ch}@{a.cfps}fps fmt={a.fmt}{depthStr}");

                    profile = _pipe.Start(cfg);
                    cw = a.cw; ch = a.ch; dw = a.dw; dh = a.dh;
                    _colorFormat = a.fmt;

                    // Warmup : laisser le temps au capteur de commencer à streamer
                    System.Threading.Thread.Sleep(500);
                    Debug.WriteLine($"[Camera] ✓ Config acceptée — fmt={_colorFormat}");
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Camera] ✗ Refusée : {ex.Message}");
                }
            }

            if (profile == null)
                throw new InvalidOperationException("Aucune config RealSense acceptée — vérifier USB/driver.");

            try
            {
                foreach (var s in profile.Device.Sensors)
                {
                    try
                    {
                        DepthUnits = s.Options[Option.DepthUnits].Value;
                        Debug.WriteLine($"[Camera] DepthUnits = {DepthUnits}");
                        break;
                    }
                    catch { }
                }
            }
            catch { }

            // Pré-alloue les buffers pour la résolution configurée
            _rgbBuf   = new byte[cw * ch * 3];
            _tmpBuf   = new byte[dw * dh * 2];
            _depthBuf = new ushort[dw * dh];

            IsRunning = true;
        }

        public void Stop()
        {
            IsRunning = false;
            try { _pipe?.Stop(); } catch { }
            _pipe?.Dispose();
            _pipe = null;
        }

        /// <summary>
        /// Récupère une frame color + depth dans les buffers pré-alloués.
        /// Depth optionnel : si absent, color seul est retourné (depthU16 vide).
        /// </summary>
        public bool TryGetAlignedFrames(uint timeoutMs, out byte[] rgb, out ushort[] depthU16)
        {
            rgb      = _rgbBuf   ?? Array.Empty<byte>();
            depthU16 = _depthBuf ?? Array.Empty<ushort>();

            if (!IsRunning || _pipe == null) return false;

            FrameSet? frames = null;
            try
            {
                frames = _pipe.WaitForFrames(timeoutMs);

                using var color = frames.ColorFrame;
                using var depth = frames.DepthFrame;

                if (color == null)
                {
                    Debug.WriteLine("[Camera] ColorFrame null — frame ignorée");
                    return false;
                }

                ColorW = color.Width;
                ColorH = color.Height;

                int rgbSize = ColorW * ColorH * 3;
                if (_rgbBuf == null || _rgbBuf.Length < rgbSize)
                    _rgbBuf = new byte[rgbSize];

                if (_colorFormat == Format.Yuyv)
                {
                    // YUYV → RGB8 : 4 octets YUYV = 2 pixels RGB
                    int yuyvSize = ColorW * ColorH * 2;
                    if (_yuyvBuf == null || _yuyvBuf.Length < yuyvSize)
                        _yuyvBuf = new byte[yuyvSize];
                    Marshal.Copy(color.Data, _yuyvBuf, 0, yuyvSize);
                    ConvertYuyvToRgb(_yuyvBuf, _rgbBuf, ColorW, ColorH);
                }
                else
                {
                    Marshal.Copy(color.Data, _rgbBuf, 0, rgbSize);
                }

                if (depth != null)
                {
                    DepthW = depth.Width;
                    DepthH = depth.Height;

                    int depthBytes = DepthW * DepthH * 2;
                    int depthPx   = DepthW * DepthH;

                    if (_tmpBuf   == null || _tmpBuf.Length   < depthBytes) _tmpBuf   = new byte[depthBytes];
                    if (_depthBuf == null || _depthBuf.Length < depthPx)    _depthBuf = new ushort[depthPx];

                    Marshal.Copy(depth.Data, _tmpBuf, 0, depthBytes);
                    Buffer.BlockCopy(_tmpBuf, 0, _depthBuf, 0, depthBytes);
                }

                rgb      = _rgbBuf;
                depthU16 = _depthBuf ?? Array.Empty<ushort>();
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Camera] {ex.Message}");
                return false;
            }
            finally
            {
                frames?.Dispose();
            }
        }

        // YUYV (YUY2) → RGB8 : chaque groupe de 4 octets produit 2 pixels
        private static void ConvertYuyvToRgb(byte[] yuyv, byte[] rgb, int w, int h)
        {
            int total = w * h;
            int si = 0, di = 0;
            for (int i = 0; i < total / 2; i++)
            {
                int y0 = yuyv[si];
                int u  = yuyv[si + 1] - 128;
                int y1 = yuyv[si + 2];
                int v  = yuyv[si + 3] - 128;
                si += 4;

                // pixel 0
                int r = y0 + (int)(1.402f * v);
                int g = y0 - (int)(0.344f * u) - (int)(0.714f * v);
                int b = y0 + (int)(1.772f * u);
                rgb[di]     = (byte)Math.Clamp(r, 0, 255);
                rgb[di + 1] = (byte)Math.Clamp(g, 0, 255);
                rgb[di + 2] = (byte)Math.Clamp(b, 0, 255);

                // pixel 1
                r = y1 + (int)(1.402f * v);
                g = y1 - (int)(0.344f * u) - (int)(0.714f * v);
                b = y1 + (int)(1.772f * u);
                rgb[di + 3] = (byte)Math.Clamp(r, 0, 255);
                rgb[di + 4] = (byte)Math.Clamp(g, 0, 255);
                rgb[di + 5] = (byte)Math.Clamp(b, 0, 255);
                di += 6;
            }
        }

        public void Dispose() => Stop();
    }
}
