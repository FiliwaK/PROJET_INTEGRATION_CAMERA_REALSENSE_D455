using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;

namespace DEMOREALSENSE
{
    /// <summary>
    /// Orchestre le pipeline complet : capture caméra → détection balle → jugement IN/OUT → rendu overlay.
    /// Tourne sur un thread dédié (appelé depuis CameraView.Loop).
    /// </summary>
    public sealed class CameraPipeline
    {
        // ── Dépendances injectées ─────────────────────────────────────────
        private readonly RealSenseCameraService _camera;
        private readonly TemplateTracker        _manualTracker;
        private readonly ClickLineDetector      _lineDetector;
        private readonly object                 _lineLock;
        private readonly TemplateTracker        _autoTracker;
        private readonly AutoTemplateFollower   _autoFollower;
        private readonly ImpactDetector         _impact;
        private readonly GroundEstimator        _ground;
        private readonly InOutLatch             _latch;

        // ── Stratégie de détection (null = mode algo) ─────────────────────
        private IDetectionStrategy? _strategy;

        // ── Paramètres publics ────────────────────────────────────────────
        public bool  AutoEnabled        { get; set; } = true;
        public bool  FlipInOutSide      { get; set; } = false;
        public float LineRealWidthMeters { get; set; } = 0.025f;
        public float LineWidthPx        { get; set; } = 6f;
        public int   OutHoldMs          { get; set; } = 5000;

        // ── État interne ──────────────────────────────────────────────────
        private PointF?    _impactMark      = null;
        private long       _impactMarkTicks = 0;
        private InOutSide  _impactSide      = InOutSide.Unknown;
        private const int  ImpactMarkMs     = 4000;

        private bool      _verdictHeld      = false;
        private long      _verdictHeldTicks = 0;
        private InOutSide _heldVerdict      = InOutSide.Unknown;

        private float _computedLineWidthPx = -1f;

        // Maintient la dernière position balle connue pour alimenter ImpactDetector
        // même quand le tracker perd la balle 1-2 frames près du sol.
        private float _lastBallX  = -1f, _lastBallContactY = -1f;
        private int   _ballLostFrames = 0;
        private const int MaxLostFrames = 3;

        // Lissage EMA du Y de contact pour filtrer le bruit de détection
        private float _smoothBallY = -1f;
        private const float SmoothAlpha = 0.7f;  // léger anti-spike seulement

        // Ancrage monde : maintient la ligne fixe dans la scène physique
        private readonly WorldLineAnchor _lineAnchor  = new WorldLineAnchor();
        private bool                     _prevHadLine = false;

        private readonly Stopwatch _sw = new Stopwatch();

        // Double-buffer Bitmaps — internes au thread pipeline uniquement
        private Bitmap? _bmpA, _bmpB;
        private bool    _writingToA = true;
        private int     _poolW, _poolH;

        // Verrou pour protéger l'accès à _strategy contre le thread UI (SwitchDetectionMode)
        private readonly object _strategyLock = new();

        // ── Construction ──────────────────────────────────────────────────

        public CameraPipeline(
            RealSenseCameraService camera,
            TemplateTracker        manualTracker,
            ClickLineDetector      lineDetector,
            object                 lineLock,
            TemplateTracker        autoTracker,
            AutoTemplateFollower   autoFollower,
            ImpactDetector         impact,
            GroundEstimator        ground,
            InOutLatch             latch)
        {
            _camera        = camera;
            _manualTracker = manualTracker;
            _lineDetector  = lineDetector;
            _lineLock      = lineLock;
            _autoTracker   = autoTracker;
            _autoFollower  = autoFollower;
            _impact        = impact;
            _ground        = ground;
            _latch         = latch;
        }

        // ── Gestion de la stratégie ───────────────────────────────────────

        /// <summary>
        /// Bascule entre mode IA (strategy != null) et mode algo (strategy == null).
        /// Remet à zéro tous les états liés à la ligne et à la balle.
        /// </summary>
        public void SetDetectionStrategy(IDetectionStrategy? strategy)
        {
            // Attend la fin de tout Detect() en cours avant de changer la stratégie
            lock (_strategyLock)
            {
                _strategy?.Reset();
                _strategy = strategy;
                _strategy?.Reset();
            }
            if (strategy == null)
                lock (_lineLock) _lineDetector.Clear();
            ResetLineRelatedStates();
        }

        // ── Reset ─────────────────────────────────────────────────────────

        /// <summary>Remet à zéro uniquement les états liés à la ligne (pas le tracker balle).</summary>
        public void ResetLineRelatedStates()
        {
            _impact.Reset();
            _latch.Reset();
            _impactMark = null; _impactMarkTicks = 0; _impactSide = InOutSide.Unknown;
            _verdictHeld = false; _verdictHeldTicks = 0; _heldVerdict = InOutSide.Unknown;
            _computedLineWidthPx = -1f;
            _lastBallX = -1f; _lastBallContactY = -1f; _ballLostFrames = 0;
            _smoothBallY = -1f;
            _lineAnchor.Reset();
            _prevHadLine = false;
        }

        /// <summary>Remet à zéro tous les états (ligne + trackers balle).</summary>
        public void ResetAllStates()
        {
            _autoTracker.Stop();
            _autoFollower.Reset();
            _strategy?.Reset();
            ResetLineRelatedStates();
        }

        // ── Boucle principale ─────────────────────────────────────────────

        public FrameResult ProcessOneFrame(OverlayRenderer overlays)
        {
            var res = new FrameResult();
            _sw.Restart();
            long nowTicks = DateTime.UtcNow.Ticks;
            res.NowTicks = nowTicks;

            // WaitForFrames bloque jusqu'à réception → pas de spin CPU.
            if (!_camera.TryGetAlignedFrames(5000, out var rgb, out var depthU16))
            {
                res.HasFrame = false;
                res.FrameMs  = _sw.Elapsed.TotalMilliseconds;
                return res;
            }

            res.HasFrame   = true;
            res.DepthUnits = _camera.DepthUnits;

            int w = _camera.ColorW, h = _camera.ColorH;

            // ── Tracker manuel ────────────────────────────────────────────
            res.ManualTrackingOk = true;
            if (_manualTracker.IsTracking)
            {
                bool ok = _manualTracker.TryUpdate(rgb, w, h);
                res.ManualTrackingOk = ok;
                if (!ok) _manualTracker.Stop();
            }

            // ── Conversion RGB → Bitmap ───────────────────────────────────
            var bmp = GetWriteBitmap(w, h);
            FrameBitmapConverter.WriteRgbToBitmap(rgb, w, h, bmp);

            if (_manualTracker.IsTracking && _manualTracker.X >= 0 && _manualTracker.Y >= 0)
                overlays.DrawManualBox(bmp, _manualTracker.X, _manualTracker.Y);

            // ── Détection balle (IA ou algo) ──────────────────────────────
            bool autoOk = false;
            int  ax = -1, ay = -1, iaRadius = 8;

            lock (_strategyLock)
            {
                if (_strategy != null)
                {
                    var det = _strategy.Detect(rgb, bmp, w, h);
                    if (det.BallCenter.HasValue)
                    {
                        autoOk = true;
                        ax = (int)det.BallCenter.Value.X;
                        ay = (int)det.BallCenter.Value.Y;
                        iaRadius = Math.Max(8, det.BallRadius);
                        overlays.DrawIaCircle(bmp, ax, ay, 12);

                        if (det.HasIaLine)
                            lock (_lineLock)
                                _lineDetector.SetLineModel(det.IaLineModel!.Value);
                    }
                }
                else if (AutoEnabled)
                {
                    autoOk = _autoFollower.TryUpdate(rgb, w, h, bmp, out ax, out ay);
                    if (autoOk && ax >= 0 && ay >= 0)
                        overlays.DrawAutoCircle(bmp, ax, ay);
                }
            }

            // ── Position balle consolidée ─────────────────────────────────
            bool haveBall   = false;
            int  ballX = -1, ballY = -1, ballRadius = 8;

            if (autoOk && ax >= 0 && ay >= 0)
            {
                haveBall   = true;
                ballX      = ax;
                ballY      = ay;
                ballRadius = _strategy != null
                    ? iaRadius
                    : Math.Max(4, _autoFollower.LastRadius);
            }
            else if (_manualTracker.IsTracking && _manualTracker.X >= 0 && _manualTracker.Y >= 0)
            {
                haveBall = true;
                ballX    = _manualTracker.X;
                ballY    = _manualTracker.Y;
            }

            int contactY = haveBall ? (ballY + ballRadius) : ballY;

            // ── Profondeur balle ──────────────────────────────────────────
            ushort ballRaw = 0;
            if (haveBall && depthU16.Length > 0)
                ballRaw = DistanceCalculator.MedianDepthRaw(
                    depthU16, _camera.DepthW, _camera.DepthH, ballX, ballY, radius: 2);
            res.RawDepth = ballRaw;

            // Calcul de la largeur visuelle de la ligne d'après la profondeur de la balle.
            // Effectué une seule fois et plafonné à 6px (la balle peut être plus proche que la ligne).
            if (_computedLineWidthPx < 0 && ballRaw != 0)
            {
                float ballDepthM = ballRaw * _camera.DepthUnits;
                if (ballDepthM > 0.1f)
                {
                    const float hFovRad = 69f * MathF.PI / 180f;
                    float pxPerMeter = w / (2f * ballDepthM * MathF.Tan(hFovRad / 2f));
                    _computedLineWidthPx = Math.Clamp(LineRealWidthMeters * pxPerMeter, 3f, 6f);
                    LineWidthPx = _computedLineWidthPx;
                }
            }

            // ── Jugement IN / OUT ─────────────────────────────────────────
            bool      hasLine  = false;
            InOutJudge.Zone zoneNow = InOutJudge.Zone.In;

            if (haveBall)
            {
                hasLine = InOutJudge.TryGetZone(
                    _lineDetector, _lineLock,
                    new PointF(ballX, contactY),
                    out zoneNow,
                    lineWidthPx: LineWidthPx);

                if (hasLine && FlipInOutSide)
                    zoneNow = zoneNow == InOutJudge.Zone.Out ? InOutJudge.Zone.In
                            : zoneNow == InOutJudge.Zone.In  ? InOutJudge.Zone.Out
                            : InOutJudge.Zone.OnLine;

                if (hasLine) _latch.Update(zoneNow != InOutJudge.Zone.Out, nowTicks);
            }
            res.Latch = _latch;

            // ── Détection de rebond (tous modes) ─────────────────────────
            bool impactFired = false;
            if (haveBall)
            {
                // Lissage EMA du Y pour filtrer le bruit de détection
                if (_smoothBallY < 0f) _smoothBallY = contactY;
                else _smoothBallY = _smoothBallY * (1f - SmoothAlpha) + contactY * SmoothAlpha;

                _lastBallX = ballX; _lastBallContactY = _smoothBallY;
                _ballLostFrames = 0;
                impactFired = _impact.UpdateBounce(ballX, _smoothBallY, nowTicks);
                if (impactFired)
                {
                    float markX = _impact.LastBounceX;
                    float markY = _impact.LastBounceY;

                    if (_ground.TryGetGroundY(_lineDetector, _lineLock, (int)markX, out float lineY))
                        markY = lineY;

                    _impactMark      = new PointF(markX, markY);
                    _impactMarkTicks = nowTicks;
                    _impactSide      = hasLine
                        ? (zoneNow == InOutJudge.Zone.Out ? InOutSide.Out : InOutSide.In)
                        : InOutSide.Unknown;
                }
            }
            else if (_lastBallX >= 0 && _ballLostFrames < MaxLostFrames)
            {
                // Balle temporairement perdue : rejoue la dernière position connue
                _ballLostFrames++;
                _impact.UpdateBounce(_lastBallX, _lastBallContactY, nowTicks);
            }
            else
            {
                _smoothBallY = -1f;
            }

            // ── Verdict maintenu post-rebond ──────────────────────────────
            if (haveBall && hasLine)
            {
                if (_verdictHeld &&
                    (nowTicks - _verdictHeldTicks) >= OutHoldMs * TimeSpan.TicksPerMillisecond)
                    _verdictHeld = false;

                if (impactFired && zoneNow == InOutJudge.Zone.Out)
                { _verdictHeld = true; _verdictHeldTicks = nowTicks; _heldVerdict = InOutSide.Out; }
                else if (impactFired && zoneNow == InOutJudge.Zone.In)
                    _verdictHeld = false;

                // Mode IA : hold OUT 5 s dès que la balle est détectée OUT (sans attendre un impact)
                if (_strategy != null && zoneNow == InOutJudge.Zone.Out)
                { _verdictHeld = true; _verdictHeldTicks = nowTicks; _heldVerdict = InOutSide.Out; }

                res.LiveSide       = _verdictHeld
                    ? InOutSide.Out
                    : (zoneNow == InOutJudge.Zone.Out ? InOutSide.Out : InOutSide.In);
                res.VerdictHeld       = _verdictHeld;
                res.VerdictHeldTicks  = _verdictHeldTicks;
            }

            // ── Ancrage monde — fixe la ligne dans la scène ───────────────
            bool hasLineCurrent;
            lock (_lineLock) hasLineCurrent = _lineDetector.HasLine;

            if (hasLineCurrent && !_prevHadLine && !_lineAnchor.IsAnchored)
            {
                var imgBounds = new System.Drawing.RectangleF(0, 0, w - 1, h - 1);
                PointF la = default, lb = default;
                bool gotSeg;
                lock (_lineLock)
                    gotSeg = _lineDetector.TryGetSegmentWithin(imgBounds, out la, out lb);
                if (gotSeg) _lineAnchor.Anchor(rgb, w, h, la, lb);
            }
            _prevHadLine = hasLineCurrent;

            // ── Rendu ligne ───────────────────────────────────────────────
            if (_lineAnchor.IsAnchored)
            {
                var (ta, tb) = _lineAnchor.Track(rgb, w, h);
                overlays.DrawLineOverlay(bmp, ta, tb, LineWidthPx);
            }
            else
            {
                overlays.DrawLineOverlay(bmp, _lineDetector, _lineLock, LineWidthPx);
            }

            DrawImpactIfAlive(bmp, nowTicks);

            // Clone pour l'UI : les bitmaps du pool (_bmpA/_bmpB) restent
            // exclusivement sur le thread pipeline — aucun LockBits concurrent possible.
            res.BitmapToShow = (Bitmap)bmp.Clone();
            _sw.Stop();
            res.FrameMs = _sw.Elapsed.TotalMilliseconds;
            return res;
        }

        // ── Helpers privés ────────────────────────────────────────────────

        private Bitmap GetWriteBitmap(int w, int h)
        {
            if (_bmpA == null || _poolW != w || _poolH != h)
            {
                _bmpA?.Dispose(); _bmpB?.Dispose();
                _bmpA = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                _bmpB = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                _poolW = w; _poolH = h;
                _writingToA = true;
            }
            Bitmap write = _writingToA ? _bmpA! : _bmpB!;
            _writingToA = !_writingToA;
            return write;
        }

        /// <summary>
        /// Affiche la croix de rebond pendant ImpactMarkMs ms.
        /// Couleur : rouge = OUT, vert = IN, blanc = pas de ligne.
        /// La croix reste fixe à la position exacte du rebond.
        /// </summary>
        private void DrawImpactIfAlive(Bitmap bmp, long nowTicks)
        {
            if (!_impactMark.HasValue) return;
            long elapsed = nowTicks - _impactMarkTicks;
            if (elapsed > ImpactMarkMs * TimeSpan.TicksPerMillisecond) { _impactMark = null; return; }

            var   p        = _impactMark.Value;
            float progress = 1f - (float)(elapsed / (double)(ImpactMarkMs * TimeSpan.TicksPerMillisecond));
            float alpha    = 0.45f + progress * 0.55f; // fade-out progressif
            float size     = 10f + progress * 4f;      // 14px au début → 10px à la fin

            // Couleur selon le verdict au moment du rebond
            Color markColor = _impactSide == InOutSide.Out ? Color.Red
                            : _impactSide == InOutSide.In  ? Color.LimeGreen
                            : Color.White;

            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            // Fond sombre derrière la croix pour contraste sur tout fond
            float bg = size + 4f;
            using var bgBrush = new System.Drawing.SolidBrush(Color.FromArgb((int)(120 * alpha), Color.Black));
            g.FillEllipse(bgBrush, p.X - bg, p.Y - bg, bg * 2, bg * 2);

            // Croix principale
            using var pen = new Pen(Color.FromArgb((int)(255 * alpha), markColor), 2f);
            g.DrawLine(pen, p.X - size, p.Y, p.X + size, p.Y);
            g.DrawLine(pen, p.X, p.Y - size, p.X, p.Y + size);

            // Cercle extérieur
            float r = size * 1.1f;
            using var penCircle = new Pen(Color.FromArgb((int)(200 * alpha), markColor), 1.5f);
            g.DrawEllipse(penCircle, p.X - r, p.Y - r, r * 2, r * 2);

            // Point central plein
            using var dotBrush = new System.Drawing.SolidBrush(Color.FromArgb((int)(255 * alpha), markColor));
            g.FillEllipse(dotBrush, p.X - 2.5f, p.Y - 2.5f, 5, 5);
        }
    }
}
