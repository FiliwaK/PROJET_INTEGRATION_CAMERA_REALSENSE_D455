using System;
using System.Drawing;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace DEMOREALSENSE
{
    /// <summary>
    /// YoloDetectionStrategy v5.
    ///
    /// BUGS corrigés vs v4 :
    ///   - Ping-pong inversé : le thread lisait toujours le mauvais buffer (données nulles).
    ///     → Remplacé par un buffer unique + flag _ballBusy.
    ///     Safe car : main écrit seulement quand _ballBusy=false (thread en attente),
    ///     thread lit seulement après le signal (main a fini d'écrire).
    ///
    ///   - ORT utilisait TOUS les cœurs CPU → main thread étranglé → 1260 ms/frame.
    ///     → Limité à 2 threads ORT : laisse des cœurs libres pour la caméra + UI.
    ///
    /// POUR PASSER À ~15-30ms/frame (recommandé) :
    ///   Dans Visual Studio → Gérer les paquets NuGet :
    ///     1. Désinstaller  Microsoft.ML.OnnxRuntime
    ///     2. Installer     Microsoft.ML.OnnxRuntime.DirectML  (version >= 1.17)
    ///   → Active automatiquement Intel UHD 770 ou Quadro K620 via DirectML.
    ///   → Aucun changement de code requis.
    /// </summary>
    public sealed class YoloDetectionStrategy : IDetectionStrategy, IDisposable
    {
        // ── Paramètres ──────────────────────────────────────────────────
        public float BallConfThresh { get; set; } = 0.30f;
        public float LineConfThresh { get; set; } = 0.25f;
        public int LineEveryNFrames { get; set; } = 8;
        public int ImgSize { get; set; } = 640;

        // ── Sessions ONNX ────────────────────────────────────────────────
        private readonly InferenceSession _ballSession;
        private readonly InferenceSession _lineSession;
        private readonly string _ballInputName;
        private readonly string _lineInputName;

        // ── Buffers pré-alloués — zéro allocation par frame ─────────────
        // Buffer unique par modèle : safe car le thread n'y accède que
        // quand _ballBusy/_lineBusy = true (main a fini d'écrire).
        private const int MaxW = 1280;
        private const int MaxH = 960;
        private const int MaxSz = 640;
        private const int TensorSz = 3 * MaxSz * MaxSz;

        private readonly byte[] _ballRgbBuf = new byte[MaxW * MaxH * 3];
        private readonly float[] _ballTensorBuf = new float[TensorSz];
        private volatile int _ballW = 640, _ballH = 480;

        private readonly byte[] _lineRgbBuf = new byte[MaxW * MaxH * 3];
        private readonly float[] _lineTensorBuf = new float[TensorSz];
        private volatile int _lineW = 640, _lineH = 480;

        // ── Threads dédiés ───────────────────────────────────────────────
        private readonly Thread _ballThread;
        private readonly Thread _lineThread;
        private readonly AutoResetEvent _ballEvent = new AutoResetEvent(false);
        private readonly AutoResetEvent _lineEvent = new AutoResetEvent(false);

        // volatile : lecture sans lock acceptable (un seul bit, cohérence suffisante)
        private volatile bool _ballBusy = false;
        private volatile bool _lineBusy = false;
        private volatile bool _disposing = false;

        // ── Caches résultats ─────────────────────────────────────────────
        private readonly object _ballLock = new();
        private (float cx, float cy, float bw, float bh, float conf)? _cachedBall = null;

        private readonly object _lineLock = new();
        private ClickLineDetector.LineModel? _cachedLine = null;
        private volatile bool _lineLocked = false;

        private int _frameCount = 0;

        // ────────────────────────────────────────────────────────────────

        public YoloDetectionStrategy(string ballOnnxPath, string lineOnnxPath)
        {
            string provider;
            (_ballSession, provider) = CreateSession(ballOnnxPath);
            (_lineSession, _) = CreateSession(lineOnnxPath);
            _ballInputName = _ballSession.InputNames[0];
            _lineInputName = _lineSession.InputNames[0];
            System.Diagnostics.Debug.WriteLine($"[YOLO] Provider : {provider}");

            _ballThread = new Thread(BallLoop)
            { IsBackground = true, Name = "YOLO-Ball", Priority = ThreadPriority.BelowNormal };
            _lineThread = new Thread(LineLoop)
            { IsBackground = true, Name = "YOLO-Line", Priority = ThreadPriority.Lowest };
            _ballThread.Start();
            _lineThread.Start();
        }

        // ── Cascade GPU → CPU (2 threads max pour ne pas étouffer le main) ──

        private static (InferenceSession, string) CreateSession(string path)
        {
            string name = System.IO.Path.GetFileName(path);

            // 1. DirectML — Intel UHD 770 ou Quadro K620 via DirectX 12
            //    Nécessite Microsoft.ML.OnnxRuntime.DirectML (installé via NuGet)
            try
            {
                var o = new SessionOptions();
                o.AppendExecutionProvider_DML(0);
                o.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                o.InterOpNumThreads = 1;
                o.IntraOpNumThreads = 1;
                o.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                o.EnableMemoryPattern = false;
                var s = new InferenceSession(path, o);
                System.Diagnostics.Debug.WriteLine($"[YOLO] ✓ DirectML OK — {name}");
                return (s, "DirectML");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[YOLO] ✗ DirectML échoué ({name}): {ex.Message}");
            }

            // 2. CPU — limité à 2 threads pour laisser des cœurs au pipeline caméra
            {
                var o = new SessionOptions();
                o.IntraOpNumThreads = 2;
                o.InterOpNumThreads = 1;
                o.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                o.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                System.Diagnostics.Debug.WriteLine($"[YOLO] ⚠ CPU fallback — {name}");
                return (new InferenceSession(path, o), "CPU (2 threads)");
            }
        }

        // ── Thread BALL ──────────────────────────────────────────────────
        // _ballSession est appelé UNIQUEMENT ici → zéro accès concurrent.

        private void BallLoop()
        {
            // Warmup : force la compilation JIT du graphe GPU avant la 1ère frame.
            try
            {
                RunBallSession(_ballTensorBuf, ImgSize);
                System.Diagnostics.Debug.WriteLine("[YOLO] Ball warmup OK");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[YOLO] Ball warmup error: {ex.Message}");
            }

            while (!_disposing)
            {
                if (!_ballEvent.WaitOne(200)) continue;
                if (_disposing) break;

                try
                {
                    // _ballRgbBuf a été rempli par main AVANT de setter _ballBusy=true
                    // et d'appeler _ballEvent.Set() — donc lecture safe ici.
                    int w = _ballW, h = _ballH, sz = ImgSize;
                    BuildTensor(_ballRgbBuf, w, h, _ballTensorBuf, sz);
                    var box = RunBallSession(_ballTensorBuf, sz);
                    lock (_ballLock) _cachedBall = box;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[YOLO Ball] {ex.Message}");
                }
                finally
                {
                    _ballBusy = false; // libère le slot pour la prochaine frame
                }
            }
        }

        // ── Thread LINE ──────────────────────────────────────────────────
        // _lineSession est appelé UNIQUEMENT ici.

        private void LineLoop()
        {
            try
            {
                RunLineSession(_lineTensorBuf, ImgSize, MaxW, MaxH);
                System.Diagnostics.Debug.WriteLine("[YOLO] Line warmup OK");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[YOLO] Line warmup error: {ex.Message}");
            }

            while (!_disposing)
            {
                if (!_lineEvent.WaitOne(200)) continue;
                if (_disposing) break;

                try
                {
                    int w = _lineW, h = _lineH, sz = ImgSize;
                    BuildTensor(_lineRgbBuf, w, h, _lineTensorBuf, sz);
                    var line = RunLineSession(_lineTensorBuf, sz, w, h);
                    if (line.HasValue)
                        lock (_lineLock) { _cachedLine = line; _lineLocked = true; }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[YOLO Line] {ex.Message}");
                }
                finally
                {
                    _lineBusy = false;
                }
            }
        }

        // ── Detect() — retourne TOUJOURS instantanément ──────────────────

        public void Reset()
        {
            lock (_lineLock) { _cachedLine = null; _lineLocked = false; }
            lock (_ballLock) { _cachedBall = null; }
            _frameCount = 0;
        }

        public DetectionResult Detect(byte[] rgb, Bitmap bmp, int w, int h)
        {
            var result = new DetectionResult { Mode = DetectionMode.Yolo };
            _frameCount++;

            // ── Soumet frame au thread BALL si disponible ─────────────────
            // _ballBusy = false signifie que le thread est en WaitOne
            // → il ne lit pas _ballRgbBuf → safe d'écrire dedans.
            if (!_ballBusy)
            {
                int len = Math.Min(rgb.Length, _ballRgbBuf.Length);
                Buffer.BlockCopy(rgb, 0, _ballRgbBuf, 0, len);
                _ballW = w; _ballH = h;
                // Memory barrier : garantit que l'écriture dans _ballRgbBuf
                // est visible par le thread avant qu'il lise après WaitOne.
                Thread.MemoryBarrier();
                _ballBusy = true;
                _ballEvent.Set(); // AutoResetEvent inclut une barrière mémoire complète
            }

            // Retourne le cache (frame précédente) — 0 ms de blocage
            lock (_ballLock)
            {
                if (_cachedBall.HasValue)
                {
                    var b = _cachedBall.Value;
                    float sx = w / (float)ImgSize, sy = h / (float)ImgSize;
                    result.BallCenter = new System.Drawing.PointF(b.cx * sx, b.cy * sy);
                    result.BallRadius = Math.Max(4, (int)(Math.Max(b.bw, b.bh) * Math.Max(sx, sy) / 2f));
                    result.BallConfidence = b.conf;
                }
            }

            // ── Soumet frame au thread LINE si disponible ─────────────────
            if (!_lineLocked && !_lineBusy && _frameCount % LineEveryNFrames == 0)
            {
                int len = Math.Min(rgb.Length, _lineRgbBuf.Length);
                Buffer.BlockCopy(rgb, 0, _lineRgbBuf, 0, len);
                _lineW = w; _lineH = h;
                Thread.MemoryBarrier();
                _lineBusy = true;
                _lineEvent.Set();
            }

            lock (_lineLock) result.IaLineModel = _cachedLine;
            return result;
        }

        // ── Inférence balle (thread BALL uniquement) ─────────────────────

        private (float cx, float cy, float bw, float bh, float conf)? RunBallSession(
            float[] tensor, int sz)
        {
            var t = new DenseTensor<float>(tensor, new[] { 1, 3, sz, sz });
            using var outs = _ballSession.Run(new[] { NamedOnnxValue.CreateFromTensor(_ballInputName, t) });
            var raw = outs[0].AsTensor<float>();
            int n = raw.Dimensions[2];
            float best = BallConfThresh;
            (float cx, float cy, float bw, float bh, float conf)? res = null;
            for (int i = 0; i < n; i++)
            {
                float c = raw[0, 4, i];
                if (c > best) { best = c; res = (raw[0, 0, i], raw[0, 1, i], raw[0, 2, i], raw[0, 3, i], c); }
            }
            return res;
        }

        // ── Inférence ligne (thread LINE uniquement) ─────────────────────

        private ClickLineDetector.LineModel? RunLineSession(
            float[] tensor, int sz, int origW, int origH)
        {
            var t = new DenseTensor<float>(tensor, new[] { 1, 3, sz, sz });
            using var outs = _lineSession.Run(new[] { NamedOnnxValue.CreateFromTensor(_lineInputName, t) });
            var boxes = outs[0].AsTensor<float>();
            int n = boxes.Dimensions[2];
            float best = LineConfThresh; int bi = -1;
            for (int i = 0; i < n; i++) { float c = boxes[0, 4, i]; if (c > best) { best = c; bi = i; } }
            if (bi < 0) return null;

            float sx = origW / (float)sz, sy = origH / (float)sz;
            float cx = boxes[0, 0, bi] * sx, cy = boxes[0, 1, bi] * sy;
            float bw = boxes[0, 2, bi] * sx, bh = boxes[0, 3, bi] * sy;

            float topX = cx, topY = cy - bh / 2f, botX = cx, botY = cy + bh / 2f;
            if (bw > bh * 0.7f) { topX = cx - bw / 2f; topY = cy; botX = cx + bw / 2f; botY = cy; }

            float dx = botX - topX, dy = botY - topY;
            float len = MathF.Sqrt(dx * dx + dy * dy);
            if (len < 5f) return null;

            return new ClickLineDetector.LineModel(
                new System.Drawing.PointF(cx, cy),
                new System.Drawing.PointF(dx / len, dy / len));
        }

        // ── Construction tensor RGB → CHW float32 ────────────────────────

        private static void BuildTensor(byte[] rgb, int w, int h, float[] t, int sz)
        {
            float sx = w / (float)sz, sy = h / (float)sz;
            int pG = sz * sz, pB = 2 * sz * sz;
            for (int ty = 0; ty < sz; ty++)
            {
                int srcRow = (int)(ty * sy) * w * 3;
                int oR = ty * sz, oG = pG + ty * sz, oB = pB + ty * sz;
                for (int tx = 0; tx < sz; tx++)
                {
                    int si = srcRow + (int)(tx * sx) * 3;
                    t[oR + tx] = rgb[si] / 255f;
                    t[oG + tx] = rgb[si + 1] / 255f;
                    t[oB + tx] = rgb[si + 2] / 255f;
                }
            }
        }

        // ── Dispose ──────────────────────────────────────────────────────

        public void Dispose()
        {
            _disposing = true;
            _ballEvent.Set(); _lineEvent.Set();
            _ballThread.Join(1000); _lineThread.Join(1000);
            _ballEvent.Dispose(); _lineEvent.Dispose();
            _ballSession?.Dispose(); _lineSession?.Dispose();
        }
    }
}