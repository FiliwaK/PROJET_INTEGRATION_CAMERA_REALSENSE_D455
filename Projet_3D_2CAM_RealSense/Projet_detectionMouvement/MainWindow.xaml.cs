// ============================================================
//  MainWindow.xaml.cs  v7 — Dual cam integration + Parallax Fix + UI Subtile
// ============================================================

using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;

namespace Projet_detectionMouvement
{
    class TerrainCorner { public double x { get; set; } public double y { get; set; } public double z { get; set; } }
    class TerrainData
    {
        public List<TerrainCorner> corners { get; set; } = new();
        public List<TerrainCorner> net_corners { get; set; } = new();
    }

    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm = new();

        private const double COURT_W = 2.20;
        private const double COURT_H = 1.00;
        private const double KITCHEN_RATIO = 4.572 / 13.41;

        private readonly SphereVisual3D _ball = new() { Radius = 0.055 };
        private readonly TranslateTransform3D _ballTx = new();

        private LinesVisual3D? _impactOuter;
        private LinesVisual3D? _impactInner;

        private double _lastBounceX = 0;
        private double _lastBounceZ = 0;
        private bool _hasBounce = false;

        private const int TRAIL_N = 16;
        private readonly Queue<(double bx, double bz, bool isIn)> _trail = new();
        private readonly SphereVisual3D[] _dots = new SphereVisual3D[TRAIL_N];

        private static readonly string TERRAIN_JSON = FindTerrainJson();

        private static string FindTerrainJson()
        {
            var candidates = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    @"..\..\..\..\PythonDetection\terrain_corners.json"),
                @"C:\Users\533\Desktop\Projet_3D\PythonDetection\terrain_corners.json",
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "terrain_corners.json"),
            };
            foreach (var c in candidates)
                try { var f = Path.GetFullPath(c); if (File.Exists(f)) return f; } catch { }
            return string.Empty;
        }

        public MainWindow()
        {
            InitializeComponent();

            _ball.Transform = _ballTx;
            _ball.Visible = false;
            Viewport3D.Children.Add(_ball);

            for (int i = 0; i < TRAIL_N; i++)
            {
                _dots[i] = new SphereVisual3D
                {
                    Radius = 0.015,
                    Fill = new SolidColorBrush(Color.FromArgb(0, 255, 220, 0)),
                    Visible = false,
                };
                Viewport3D.Children.Add(_dots[i]);
            }

            DrawCourt();
            BuildImpactRing();

            StatusText.Text = !string.IsNullOrEmpty(TERRAIN_JSON)
                ? "✓ Calibration détectée — prêt"
                : "⚠ terrain_corners.json introuvable — recalibrer";

            _vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(_vm.StatusText))
                    StatusText.Text = _vm.StatusText;
                if (e.PropertyName == nameof(_vm.StatusColor))
                    StatusDot.Fill = new SolidColorBrush(
                        (Color)ColorConverter.ConvertFromString(_vm.StatusColor));
            };
            _vm.DataReceived += MoveBall;
            Closing += (_, _) => _vm.Stop();
            _vm.Start();
        }

        private void DrawCourt()
        {
            double hw = COURT_W / 2, hh = COURT_H / 2;
            var hg = new Point3D(-hw, 0, -hh); var hd = new Point3D(+hw, 0, -hh);
            var bd = new Point3D(+hw, 0, +hh); var bg = new Point3D(-hw, 0, +hh);
            double k1z = -hh + KITCHEN_RATIO * COURT_H, k2z = +hh - KITCHEN_RATIO * COURT_H;

            AddQuad(hg, hd, bd, bg, Color.FromArgb(255, 18, 72, 148));
            AddQuad(hg, hd, new Point3D(+hw, 0, k1z), new Point3D(-hw, 0, k1z), Color.FromArgb(55, 0, 0, 90));
            AddQuad(new Point3D(-hw, 0, k2z), new Point3D(+hw, 0, k2z), bd, bg, Color.FromArgb(55, 0, 0, 90));
            AddLine(hg, hd, Colors.White, 4); AddLine(bg, bd, Colors.White, 4);
            AddLine(hg, bg, Colors.White, 4); AddLine(hd, bd, Colors.White, 4);
            AddLine(new Point3D(-hw, 0, k1z), new Point3D(+hw, 0, k1z), Colors.White, 2.5);
            AddLine(new Point3D(-hw, 0, k2z), new Point3D(+hw, 0, k2z), Colors.White, 2.5);
            AddLine(new Point3D(0, 0, k1z), new Point3D(0, 0, k2z), Colors.White, 2);

            double netH = 0.065;
            AddLine(new Point3D(-hw, 0, 0), new Point3D(+hw, 0, 0), Color.FromRgb(255, 210, 0), 5.5);
            AddLine(new Point3D(-hw, 0, 0), new Point3D(-hw, netH, 0), Color.FromRgb(90, 90, 90), 4);
            AddLine(new Point3D(+hw, 0, 0), new Point3D(+hw, netH, 0), Color.FromRgb(90, 90, 90), 4);
            AddLine(new Point3D(-hw, netH, 0), new Point3D(+hw, netH, 0), Color.FromRgb(40, 40, 40), 5);
            for (int i = 1; i < 12; i++) { double t = (double)i / 12 * COURT_W - hw; AddLine(new Point3D(t, 0, 0), new Point3D(t, netH, 0), Color.FromArgb(60, 50, 50, 50), 1); }
            for (int j = 1; j < 4; j++) { double y = netH * j / 4; AddLine(new Point3D(-hw, y, 0), new Point3D(+hw, y, 0), Color.FromArgb(60, 50, 50, 50), 1); }
        }

        private void BuildImpactRing()
        {
            _impactOuter = new LinesVisual3D { Color = Color.FromArgb(220, 255, 60, 0), Thickness = 2.5 };
            _impactInner = new LinesVisual3D { Color = Color.FromArgb(150, 255, 180, 0), Thickness = 1.5 };
            Viewport3D.Children.Add(_impactOuter);
            Viewport3D.Children.Add(_impactInner);
        }

        private void SetImpactRing(double bx, double bz, bool visible)
        {
            if (_impactOuter == null || _impactInner == null) return;
            _impactOuter.Points.Clear(); _impactInner.Points.Clear();
            if (!visible) return;
            const int SEG = 36;
            for (int i = 0; i < SEG; i++)
            {
                double a1 = 2 * Math.PI * i / SEG, a2 = 2 * Math.PI * (i + 1) / SEG;
                _impactOuter.Points.Add(new Point3D(bx + Math.Cos(a1) * 0.11, 0.005, bz + Math.Sin(a1) * 0.11));
                _impactOuter.Points.Add(new Point3D(bx + Math.Cos(a2) * 0.11, 0.005, bz + Math.Sin(a2) * 0.11));
                _impactInner.Points.Add(new Point3D(bx + Math.Cos(a1) * 0.065, 0.005, bz + Math.Sin(a1) * 0.065));
                _impactInner.Points.Add(new Point3D(bx + Math.Cos(a2) * 0.065, 0.005, bz + Math.Sin(a2) * 0.065));
            }
        }

        private void UpdateTrail()
        {
            var pts = _trail.ToArray();
            for (int i = 0; i < TRAIL_N; i++)
            {
                int idx = pts.Length - 1 - i;
                if (idx < 0) { _dots[i].Visible = false; continue; }
                var (bx, bz, isIn) = pts[idx];
                double alpha = (double)(idx + 1) / pts.Length;
                byte a = (byte)(alpha * 210);
                _dots[i].Fill = new SolidColorBrush(isIn ? Color.FromArgb(a, 255, 220, 50) : Color.FromArgb(a, 255, 80, 0));
                _dots[i].Radius = 0.012 + alpha * 0.012;
                _dots[i].Center = new Point3D(bx, 0.008, bz);
                _dots[i].Visible = true;
            }
        }

        private void MoveBall(BallData data)
        {
            if (!data.ball_detected || data.z <= 0 || data.court_u < -0.5f)
            {
                _ball.Visible = false;
                VerdictText.Visibility = Visibility.Collapsed;
                PositionText.Text = "X:-.--  Z:-.--";
                _hasBounce = false;
                SetImpactRing(0, 0, false);
                return;
            }

            // ── CORRECTION DE PARALLAXE INTÉGRÉE ──────────────────────────────
            double u_val = data.court_u_geo >= 0f ? data.court_u_geo : data.court_u;
            double v_val = data.court_v_geo >= 0f ? data.court_v_geo : data.court_v;

            double bx = (u_val - 0.5) * COURT_W;
            double bz = (v_val - 0.5) * COURT_H;

            double by = _ball.Radius;
            if (data.height_m > 0 && !data.on_ground)
            {
                double heightScale = Math.Min(data.height_m / 0.5, 1.0) * 0.3;
                by = _ball.Radius + heightScale;
            }

            _ballTx.OffsetX = bx; _ballTx.OffsetY = by; _ballTx.OffsetZ = bz;
            _ball.Visible = true;

            bool isIn = data.is_in;

            // Couleur basique
            if (!isIn)
                _ball.Fill = new SolidColorBrush(Color.FromRgb(255, 55, 10));
            else if (!data.on_ground)
                _ball.Fill = new SolidColorBrush(Color.FromRgb(0, 220, 220));
            else
                _ball.Fill = new SolidColorBrush(Color.FromRgb(255, 220, 0));

            // ── Gestion du Rebond (Anneau Hawk-Eye) ───────────────────────────
            if (data.bounce && data.bounce_court_u >= 0f)
            {
                _lastBounceX = (data.bounce_court_u - 0.5) * COURT_W;
                _lastBounceZ = (data.bounce_court_v - 0.5) * COURT_H;
                _hasBounce = true;

                // Effet de flash blanc sur la balle lors du rebond
                _ball.Fill = new SolidColorBrush(Colors.White);
            }

            if (_hasBounce)
                SetImpactRing(_lastBounceX, _lastBounceZ, true);
            else if (!isIn)
                SetImpactRing(bx, bz, true);
            else
                SetImpactRing(0, 0, false);

            _trail.Enqueue((bx, bz, isIn));
            if (_trail.Count > TRAIL_N) _trail.Dequeue();
            UpdateTrail();

            // ── Verdict Visuel Subtil ─────────────────────────────────────────
            if (data.on_ground)
            {
                VerdictText.Text = isIn ? "IN" : "OUT";
                VerdictText.Foreground = isIn
                    ? new SolidColorBrush(Colors.LimeGreen)
                    : new SolidColorBrush(Colors.OrangeRed);
                VerdictText.FontSize = 60; // Plus petit/discret
                VerdictText.Opacity = 1.0;
                VerdictText.Visibility = Visibility.Visible;
            }
            else
            {
                VerdictText.Text = "BALL IN PLAY";
                VerdictText.Foreground = new SolidColorBrush(Color.FromArgb(180, 100, 180, 200));
                VerdictText.FontSize = 25;
                VerdictText.Opacity = 0.6;
                VerdictText.Visibility = Visibility.Visible;
            }

            double h_display = data.height_m >= 0 ? data.height_m : 0;
            PositionText.Text =
                $"X:{data.x:+0.00;-0.00}m  " +
                $"Z:{data.z:0.00}m  " +
                $"H:{h_display * 100:0.0}cm  " +
                $"court=({u_val:0.00},{v_val:0.00})  " +
                $"| {(isIn ? "✓ IN" : "✗ OUT")}" +
                $"{(data.on_ground ? "" : " [VOL]")}" +
                $"{(data.bounce ? " [REBOND]" : "")}";
        }

        private void AddQuad(Point3D a, Point3D b, Point3D c, Point3D d, Color color)
        {
            var mesh = new MeshGeometry3D();
            mesh.Positions.Add(a); mesh.Positions.Add(b); mesh.Positions.Add(c); mesh.Positions.Add(d);
            mesh.TriangleIndices.Add(0); mesh.TriangleIndices.Add(1); mesh.TriangleIndices.Add(2);
            mesh.TriangleIndices.Add(0); mesh.TriangleIndices.Add(2); mesh.TriangleIndices.Add(3);
            for (int i = 0; i < 4; i++) mesh.Normals.Add(new Vector3D(0, 1, 0));
            var brush = new SolidColorBrush(color);
            var mat = new MaterialGroup();
            mat.Children.Add(new DiffuseMaterial(brush));
            mat.Children.Add(new EmissiveMaterial(new SolidColorBrush(Color.FromArgb(25, color.R, color.G, color.B))));
            Viewport3D.Children.Add(new ModelVisual3D { Content = new GeometryModel3D(mesh, mat) { BackMaterial = mat } });
        }

        private void AddLine(Point3D a, Point3D b, Color color, double thickness)
        {
            var ln = new LinesVisual3D { Color = color, Thickness = thickness };
            ln.Points.Add(a); ln.Points.Add(b); Viewport3D.Children.Add(ln);
        }
    }
}