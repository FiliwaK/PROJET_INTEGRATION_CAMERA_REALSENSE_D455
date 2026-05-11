// ============================================================
//  MainWindow.xaml.cs  —  Logique principale de l'interface
//
//  CE FICHIER GÈRE :
//    1. Le dessin sur le canvas (trait suivi par l'index)
//    2. La sélection d'outil via le geste "two_fingers" (index + majeur levés)
//    3. La sauvegarde du dessin quand les 2 mains restent ouvertes 4s
//    4. L'affichage de la palette en haut (organisée par catégories)
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Projet_detectionMouvement
{
    public partial class MainWindow : Window
    {
        // ════════════════════════════════════════════════════════════════════════
        //  CONSTANTES
        // ════════════════════════════════════════════════════════════════════════

        private const int CW = 640;
        private const int CH = 480;
        private const double DWELL_SECONDS = 1.5;
        private const double SAVE_HOLD_SECONDS = 4.0;
        private const double PALETTE_H = 52.0;

        private static readonly Color BgColor = Color.FromRgb(0x0d, 0x0d, 0x1a);

        // ════════════════════════════════════════════════════════════════════════
        //  ÉNUMÉRATION : type de pinceau
        // ════════════════════════════════════════════════════════════════════════
        private enum BrushTool
        {
            Crayon,
            Pinceau,
            Marqueur
        }

        // ════════════════════════════════════════════════════════════════════════
        //  CLASSE : PaletteItem
        // ════════════════════════════════════════════════════════════════════════
        private class PaletteItem
        {
            public string Name { get; init; } = "";
            public string Label { get; init; } = "";
            public string IconPath { get; init; } = "";
            public Color FillColor { get; init; }
            public Rect Bounds { get; init; }
            public string Category { get; init; } = "";
            public Action OnSelect { get; init; } = () => { };
        }

        // ════════════════════════════════════════════════════════════════════════
        //  CONNEXIONS SQUELETTE MAIN MediaPipe
        // ════════════════════════════════════════════════════════════════════════
        private static readonly (int A, int B)[] HandConnections =
        {
            (0,1),(1,2),(2,3),(3,4),
            (0,5),(5,6),(6,7),(7,8),
            (0,9),(9,10),(10,11),(11,12),
            (0,13),(13,14),(14,15),(15,16),
            (0,17),(17,18),(18,19),(19,20),
            (5,9),(9,13),(13,17),
        };

        // ════════════════════════════════════════════════════════════════════════
        //  DÉPENDANCES ET ÉTATS
        // ════════════════════════════════════════════════════════════════════════

        private readonly MainViewModel _vm = new MainViewModel();

        private BrushTool _tool = BrushTool.Pinceau;
        private Color _drawColor = Colors.Red;
        private double _sizeMultiplier = 1.0;
        private bool _isEraser = false;
        private string _selectedItem = "pinceau";

        private bool _showFinal = false;
        private bool _alreadySaved = false;
        private string? _lastSavedPath = null;
        private DateTime _saveHoldStart = DateTime.MinValue;

        private readonly Polyline?[] _currentStrokes = { null, null };
        private readonly bool[] _wasActive = { false, false };
        private readonly Point[] _lastDrawPoint = { default, default };

        private string? _dwellItem = null;
        private DateTime _dwellStart = DateTime.MinValue;

        private const double FIST_SELECT_SECONDS = 3.0;
        private DateTime _fistHoldStart = DateTime.MinValue;
        private Polyline? _selectedStroke = null;
        private Point _fistAnchorPos = default;
        private bool _isDragging = false;

        private List<PaletteItem> _palette = new();

        // ════════════════════════════════════════════════════════════════════════
        //  CONSTRUCTEUR
        // ════════════════════════════════════════════════════════════════════════
        public MainWindow()
        {
            InitializeComponent();
            InitPalette();

            _vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(_vm.StatusText))
                    StatusText.Text = _vm.StatusText;
                if (e.PropertyName == nameof(_vm.StatusColor))
                    StatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_vm.StatusColor));
            };

            _vm.DataReceived += DrawFrame;
            Closing += (_, _) => _vm.Stop();
            _vm.Start();
        }

        // ════════════════════════════════════════════════════════════════════════
        //  INITIALISATION DE LA PALETTE 
        // ════════════════════════════════════════════════════════════════════════
        private void InitPalette()
        {
            const double iy = 16.0;
            const double ih = 32.0;
            Rect R(double x, double w) => new Rect(x, iy, w, ih);

            _palette = new List<PaletteItem>
            {
                // ── PINCEAUX (Avec images) ──────────────────────────────
                new() { Name="crayon",   Label="Crayon", IconPath="Icons/crayon.png", Category="PINCEAUX",
                        FillColor=Color.FromRgb(200,180,120), Bounds=R(4, 54),
                        OnSelect=()=>{ _tool=BrushTool.Crayon;   _isEraser=false; _selectedItem="crayon"; } },

                new() { Name="pinceau",  Label="Pinceau", IconPath="Icons/pinceau.png", Category="PINCEAUX",
                        FillColor=Color.FromRgb(100,160,220), Bounds=R(61, 54),
                        OnSelect=()=>{ _tool=BrushTool.Pinceau;  _isEraser=false; _selectedItem="pinceau"; } },

                new() { Name="marqueur", Label="Feutre", IconPath="Icons/marqueur.png", Category="PINCEAUX",
                        FillColor=Color.FromRgb(80,200,130), Bounds=R(118, 54),
                        OnSelect=()=>{ _tool=BrushTool.Marqueur; _isEraser=false; _selectedItem="marqueur"; } },

                // ── TAILLE (Géré visuellement par des points) ───
                new() { Name="size_s", Label="", Category="TAILLE",
                        FillColor=Color.FromRgb(70,70,110), Bounds=R(181, 46),
                        OnSelect=()=>{ _sizeMultiplier=0.5; _isEraser=false; _selectedItem="size_s"; } },

                new() { Name="size_l", Label="", Category="TAILLE",
                        FillColor=Color.FromRgb(70,70,110), Bounds=R(230, 46),
                        OnSelect=()=>{ _sizeMultiplier=2.0; _isEraser=false; _selectedItem="size_l"; } },

                // ── COULEURS (Sans texte) ───────────────────────────────────────
                new() { Name="red",    Label="", Category="COULEURS",
                        FillColor=Colors.Red, Bounds=R(281, 36),
                        OnSelect=()=>{ _drawColor=Colors.Red;        _isEraser=false; _selectedItem="red"; } },

                new() { Name="green",  Label="", Category="COULEURS",
                        FillColor=Colors.LimeGreen, Bounds=R(320, 36),
                        OnSelect=()=>{ _drawColor=Colors.LimeGreen;  _isEraser=false; _selectedItem="green"; } },

                new() { Name="blue",   Label="", Category="COULEURS",
                        FillColor=Colors.DodgerBlue, Bounds=R(359, 36),
                        OnSelect=()=>{ _drawColor=Colors.DodgerBlue; _isEraser=false; _selectedItem="blue"; } },

                new() { Name="yellow", Label="", Category="COULEURS",
                        FillColor=Colors.Yellow, Bounds=R(398, 36),
                        OnSelect=()=>{ _drawColor=Colors.Yellow;     _isEraser=false; _selectedItem="yellow"; } },

                new() { Name="white",  Label="", Category="COULEURS",
                        FillColor=Colors.White, Bounds=R(437, 36),
                        OnSelect=()=>{ _drawColor=Colors.White;      _isEraser=false; _selectedItem="white"; } },

                new() { Name="orange", Label="", Category="COULEURS",
                        FillColor=Colors.Orange, Bounds=R(476, 36),
                        OnSelect=()=>{ _drawColor=Colors.Orange;     _isEraser=false; _selectedItem="orange"; } },

                // ── ACTIONS (Avec images) ─────────────────────────────────
                new() { Name="eraser", Label="Gomme", IconPath="Icons/gomme.png", Category="ACTIONS",
                        FillColor=Color.FromRgb(90,90,90), Bounds=R(519, 52),
                        OnSelect=()=>{ _isEraser=true; _selectedItem="eraser"; } },

                new() { Name="clear",  Label="Effacer", IconPath="Icons/effacer.png", Category="ACTIONS",
                        FillColor=Color.FromRgb(160,40,40), Bounds=R(574, 56),
                        OnSelect=()=>{
                            DrawingCanvas.Children.Clear();
                            _currentStrokes[0] = null;
                            _currentStrokes[1] = null;
                            _alreadySaved      = false;
                            _lastSavedPath     = null;
                            _isDragging        = false;
                            _selectedStroke    = null;
                            _selectedItem      = "clear";
                        }},
            };
        }

        // ════════════════════════════════════════════════════════════════════════
        //  BOUCLE PRINCIPALE (~30 fps)
        // ════════════════════════════════════════════════════════════════════════
        private void DrawFrame(HandData data)
        {
            bool bothOpen = data.gestures.Count >= 2
                         && data.gestures[0] == "open"
                         && data.gestures[1] == "open";

            if (bothOpen)
            {
                if (_saveHoldStart == DateTime.MinValue)
                    _saveHoldStart = DateTime.Now;

                double heldSec = (DateTime.Now - _saveHoldStart).TotalSeconds;

                if (heldSec >= SAVE_HOLD_SECONDS && !_showFinal)
                {
                    _showFinal = true;
                    if (!_alreadySaved)
                    {
                        SaveDrawing();
                        _alreadySaved = true;
                    }
                }
            }
            else
            {
                if (_showFinal)
                {
                    _showFinal = false;
                    _alreadySaved = false;
                }
                _saveHoldStart = DateTime.MinValue;
            }

            if (!_showFinal)
            {
                ProcessDrawing(data);
                ProcessDwellSelection(data);
                ProcessFistDrag(data);
            }

            ToolText.Text = _isEraser
                ? "Gomme active"
                : $"{_tool}  ●  {GetCurrentColor()}  ×{_sizeMultiplier:0.0}";

            OverlayCanvas.Children.Clear();
            DrawPalette();
            DrawHandLandmarks(data);
            DrawCursors(data);
            if (_isDragging) DrawSelectionHighlight();

            if (_showFinal)
                DrawFinalOverlay();
            else if (bothOpen && _saveHoldStart != DateTime.MinValue)
                DrawSaveProgress((DateTime.Now - _saveHoldStart).TotalSeconds);
        }

        // ════════════════════════════════════════════════════════════════════════
        //  DESSIN ET GOMME 
        // ════════════════════════════════════════════════════════════════════════
        private void ProcessDrawing(HandData data)
        {
            if (data.hands == null) return;
            int count = Math.Min(data.hands.Count, 2);

            for (int i = 0; i < count; i++)
            {
                string gesture = i < data.gestures.Count ? data.gestures[i] : "other";
                bool isActive = gesture == "pointing";

                if (isActive)
                {
                    var tip = data.hands[i][8];
                    var newPt = new Point(tip.x * CW, tip.y * CH);

                    // Appel de la NOUVELLE gomme progressive
                    if (_isEraser)
                    {
                        EraseStrokesProgressively(newPt);
                    }
                    else
                    {
                        // Mode dessin classique
                        if (!_wasActive[i])
                        {
                            var stroke = CreateStroke();
                            DrawingCanvas.Children.Add(stroke);
                            _currentStrokes[i] = stroke;
                            _currentStrokes[i].Points.Add(newPt);
                            _lastDrawPoint[i] = newPt;
                        }
                        else
                        {
                            var last = _lastDrawPoint[i];
                            double dx = newPt.X - last.X;
                            double dy = newPt.Y - last.Y;
                            double thr = GetDrawThreshold();

                            if (dx * dx + dy * dy >= thr * thr)
                            {
                                _currentStrokes[i]?.Points.Add(newPt);
                                _lastDrawPoint[i] = newPt;
                            }
                        }
                    }
                }
                else
                {
                    _currentStrokes[i] = null;
                    _lastDrawPoint[i] = default;
                }
                _wasActive[i] = isActive;
            }

            for (int i = count; i < 2; i++)
            {
                _currentStrokes[i] = null;
                _wasActive[i] = false;
                _lastDrawPoint[i] = default;
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        //  GOMME PROGRESSIVE ET RÉELLE (Coupe les traits en temps réel)
        // ════════════════════════════════════════════════════════════════════════
        private void EraseStrokesProgressively(Point pos)
        {
            double eraseRadius = 25.0; // Tolérance/Taille de la gomme en pixels
            double eraseRadiusSq = eraseRadius * eraseRadius;

            var strokesToRemove = new List<Polyline>();
            var strokesToAdd = new List<Polyline>();

            foreach (var child in DrawingCanvas.Children)
            {
                if (child is Polyline pl && pl.Points.Count > 1)
                {
                    bool wasModified = false;
                    var currentSegmentPoints = new PointCollection();
                    var newPolylines = new List<Polyline>();

                    currentSegmentPoints.Add(pl.Points[0]);

                    // Parcourt les segments du trait pour voir si la gomme les touche
                    for (int j = 0; j < pl.Points.Count - 1; j++)
                    {
                        Point p1 = pl.Points[j];
                        Point p2 = pl.Points[j + 1];

                        if (SegmentIntersectsCircle(p1, p2, pos, eraseRadiusSq))
                        {
                            wasModified = true;
                            // La gomme touche ce segment ! On coupe le trait ici.
                            // On sauvegarde le morceau de trait précédent s'il est valide
                            if (currentSegmentPoints.Count > 1)
                            {
                                newPolylines.Add(ClonePolyline(pl, currentSegmentPoints));
                            }
                            // On repart à zéro pour le morceau de trait suivant
                            currentSegmentPoints = new PointCollection();
                        }
                        else
                        {
                            // La gomme ne touche pas, le morceau de trait continue normalement
                            if (currentSegmentPoints.Count == 0)
                                currentSegmentPoints.Add(p1); // Reprise après une coupure
                            currentSegmentPoints.Add(p2);
                        }
                    }

                    // Si on a coupé le trait original, on le remplace par ses morceaux
                    if (wasModified)
                    {
                        strokesToRemove.Add(pl);
                        if (currentSegmentPoints.Count > 1)
                        {
                            newPolylines.Add(ClonePolyline(pl, currentSegmentPoints));
                        }
                        strokesToAdd.AddRange(newPolylines);
                    }
                }
            }

            // Application physique des coupures dans le Canvas
            foreach (var stroke in strokesToRemove)
            {
                DrawingCanvas.Children.Remove(stroke);
                // Sécurité si on était en train de le déplacer
                if (_selectedStroke == stroke)
                {
                    _selectedStroke = null;
                    _isDragging = false;
                }
            }
            foreach (var stroke in strokesToAdd)
            {
                DrawingCanvas.Children.Add(stroke);
            }
        }

        // Helper : Copie un trait pour créer les "morceaux" après passage de la gomme
        private Polyline ClonePolyline(Polyline original, PointCollection points)
        {
            return new Polyline
            {
                Stroke = original.Stroke,
                StrokeThickness = original.StrokeThickness,
                StrokeLineJoin = original.StrokeLineJoin,
                StrokeStartLineCap = original.StrokeStartLineCap,
                StrokeEndLineCap = original.StrokeEndLineCap,
                Points = points
            };
        }

        // Helper : Mathématiques pour savoir si un segment de ligne croise le cercle de la gomme
        private bool SegmentIntersectsCircle(Point p1, Point p2, Point center, double radiusSq)
        {
            double l2 = (p1.X - p2.X) * (p1.X - p2.X) + (p1.Y - p2.Y) * (p1.Y - p2.Y);
            if (l2 == 0) return ((p1.X - center.X) * (p1.X - center.X) + (p1.Y - center.Y) * (p1.Y - center.Y)) <= radiusSq;

            double t = ((center.X - p1.X) * (p2.X - p1.X) + (center.Y - p1.Y) * (p2.Y - p1.Y)) / l2;
            t = Math.Max(0, Math.Min(1, t));

            double projX = p1.X + t * (p2.X - p1.X);
            double projY = p1.Y + t * (p2.Y - p1.Y);

            return ((center.X - projX) * (center.X - projX) + (center.Y - projY) * (center.Y - projY)) <= radiusSq;
        }

        private Polyline CreateStroke()
        {
            double thickness = _tool switch
            {
                BrushTool.Crayon => 3.0 * _sizeMultiplier,
                BrushTool.Marqueur => 20.0 * _sizeMultiplier,
                _ => 8.0 * _sizeMultiplier
            };

            PenLineCap cap = _tool switch
            {
                BrushTool.Crayon => PenLineCap.Flat,
                BrushTool.Marqueur => PenLineCap.Square,
                _ => PenLineCap.Round
            };

            PenLineJoin join = _tool == BrushTool.Pinceau ? PenLineJoin.Round : PenLineJoin.Miter;

            return new Polyline
            {
                Stroke = new SolidColorBrush(_drawColor),
                StrokeThickness = Math.Max(thickness, 1.0),
                StrokeLineJoin = join,
                StrokeStartLineCap = cap,
                StrokeEndLineCap = cap
            };
        }

        // ════════════════════════════════════════════════════════════════════════
        //  SÉLECTION PAR DWELL — geste "two_fingers" (index + majeur levés), durée 1.5s
        // ════════════════════════════════════════════════════════════════════════
        private void ProcessDwellSelection(HandData data)
        {
            if (data.hands == null) return;
            bool hoverFound = false;

            for (int i = 0; i < Math.Min(data.hands.Count, 2); i++)
            {
                string gesture = i < data.gestures.Count ? data.gestures[i] : "other";
                if (gesture != "two_fingers") continue;

                var t1 = data.hands[i][8];
                var t2 = data.hands[i][12];
                double cx = (t1.x + t2.x) / 2.0 * CW;
                double cy = (t1.y + t2.y) / 2.0 * CH;

                foreach (var item in _palette)
                {
                    if (!item.Bounds.Contains(cx, cy)) continue;

                    hoverFound = true;

                    if (_dwellItem != item.Name)
                    {
                        _dwellItem = item.Name;
                        _dwellStart = DateTime.Now;
                    }
                    else if ((DateTime.Now - _dwellStart).TotalSeconds >= DWELL_SECONDS)
                    {
                        item.OnSelect();
                        _dwellItem = null;
                    }
                    break;
                }
            }

            if (!hoverFound)
                _dwellItem = null;
        }

        // ════════════════════════════════════════════════════════════════════════
        //  DÉPLACEMENT DE DESSIN — geste "fist" (poing fermé), maintien 3s
        // ════════════════════════════════════════════════════════════════════════
        private void ProcessFistDrag(HandData data)
        {
            if (data.hands == null) return;

            int fistIdx = -1;
            bool sawClearGesture = false;

            for (int i = 0; i < Math.Min(data.hands.Count, 2); i++)
            {
                string g = i < data.gestures.Count ? data.gestures[i] : "other";
                if (g == "fist" && fistIdx < 0) fistIdx = i;
                if (g == "pointing" || g == "two_fingers" || g == "open") sawClearGesture = true;
            }

            if (fistIdx < 0 && !sawClearGesture && _fistHoldStart != DateTime.MinValue && !_isDragging)
            {
                for (int i = 0; i < Math.Min(data.hands.Count, 2); i++)
                {
                    string g = i < data.gestures.Count ? data.gestures[i] : "other";
                    if (g == "other") { fistIdx = i; break; }
                }
            }

            if (!_isDragging)
            {
                if (fistIdx < 0)
                {
                    _fistHoldStart = DateTime.MinValue;
                }
                else
                {
                    if (_fistHoldStart == DateTime.MinValue)
                        _fistHoldStart = DateTime.Now;

                    if ((DateTime.Now - _fistHoldStart).TotalSeconds >= FIST_SELECT_SECONDS)
                    {
                        var palm = data.hands[fistIdx][9];
                        var pos = new Point(palm.x * CW, palm.y * CH);
                        _selectedStroke = FindNearestStroke(pos);

                        if (_selectedStroke != null)
                        {
                            _isDragging = true;
                            _fistAnchorPos = pos;
                        }
                        _fistHoldStart = DateTime.MinValue;
                    }
                }
            }
            else
            {
                if (data.hands.Count == 0 || sawClearGesture)
                {
                    _isDragging = false;
                    _selectedStroke = null;
                }
                else if (fistIdx >= 0)
                {
                    var palm = data.hands[fistIdx][9];
                    var pos = new Point(palm.x * CW, palm.y * CH);
                    TranslateStroke(_selectedStroke!, pos.X - _fistAnchorPos.X, pos.Y - _fistAnchorPos.Y);
                    _fistAnchorPos = pos;
                }
            }
        }

        private Polyline? FindNearestStroke(Point pos)
        {
            Polyline? nearest = null;
            double minDist = double.MaxValue;
            const double maxDist = 40.0;

            foreach (var child in DrawingCanvas.Children)
            {
                if (child is not Polyline pl) continue;

                foreach (var pt in pl.Points)
                {
                    double d = Math.Sqrt(Math.Pow(pt.X - pos.X, 2) + Math.Pow(pt.Y - pos.Y, 2));
                    if (d < minDist) { minDist = d; nearest = pl; }
                }
            }

            return minDist <= maxDist ? nearest : null;
        }

        private static void TranslateStroke(Polyline stroke, double dx, double dy)
        {
            for (int i = 0; i < stroke.Points.Count; i++)
                stroke.Points[i] = new Point(stroke.Points[i].X + dx, stroke.Points[i].Y + dy);
        }

        private void SaveDrawing()
        {
            try
            {
                var rtb = new RenderTargetBitmap(CW, CH, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(DrawingCanvas);

                var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
                encoder.Frames.Add(BitmapFrame.Create(rtb));

                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string filename = $"dessin_{DateTime.Now:yyyyMMdd_HHmmss}.jpg";
                string path = System.IO.Path.Combine(desktop, filename);

                using (var stream = File.Create(path))
                    encoder.Save(stream);

                _lastSavedPath = path;

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _lastSavedPath = $"Erreur : {ex.Message}";
            }
        }

        private void DrawHandLandmarks(HandData data)
        {
            if (data.hands == null) return;

            foreach (var pts in data.hands)
            {
                foreach (var (a, b) in HandConnections)
                {
                    if (a >= pts.Count || b >= pts.Count) continue;
                    OverlayCanvas.Children.Add(new Line
                    {
                        X1 = pts[a].x * CW,
                        Y1 = pts[a].y * CH,
                        X2 = pts[b].x * CW,
                        Y2 = pts[b].y * CH,
                        Stroke = new SolidColorBrush(Color.FromArgb(170, 255, 220, 0)),
                        StrokeThickness = 1.5
                    });
                }

                foreach (var p in pts)
                {
                    var dot = new Ellipse
                    {
                        Width = 7,
                        Height = 7,
                        Fill = new SolidColorBrush(Color.FromArgb(200, 220, 50, 50)),
                        Stroke = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)),
                        StrokeThickness = 1
                    };
                    Canvas.SetLeft(dot, p.x * CW - 3.5);
                    Canvas.SetTop(dot, p.y * CH - 3.5);
                    OverlayCanvas.Children.Add(dot);
                }
            }
        }

        private void DrawCursors(HandData data)
        {
            if (data.hands == null) return;

            for (int i = 0; i < Math.Min(data.hands.Count, 2); i++)
            {
                string gesture = i < data.gestures.Count ? data.gestures[i] : "other";
                var hand = data.hands[i];

                if (gesture == "pointing")
                {
                    var tip = hand[8];
                    double cx = tip.x * CW;
                    double cy = tip.y * CH;
                    double r = GetCurrentThickness() + 6;
                    Color c = _isEraser ? Colors.Gray : _drawColor;

                    var ring = new Ellipse
                    {
                        Width = r,
                        Height = r,
                        Stroke = new SolidColorBrush(c),
                        StrokeThickness = 2,
                        Fill = Brushes.Transparent
                    };
                    Canvas.SetLeft(ring, cx - r / 2); Canvas.SetTop(ring, cy - r / 2);
                    OverlayCanvas.Children.Add(ring);

                    var dot = new Ellipse { Width = 4, Height = 4, Fill = new SolidColorBrush(c) };
                    Canvas.SetLeft(dot, cx - 2); Canvas.SetTop(dot, cy - 2);
                    OverlayCanvas.Children.Add(dot);
                }
                else if (gesture == "two_fingers")
                {
                    var t1 = hand[8];
                    var t2 = hand[12];
                    double cx = (t1.x + t2.x) / 2.0 * CW;
                    double cy = (t1.y + t2.y) / 2.0 * CH;

                    var ring = new Ellipse
                    {
                        Width = 26,
                        Height = 26,
                        Stroke = Brushes.Cyan,
                        StrokeThickness = 2.5,
                        Fill = new SolidColorBrush(Color.FromArgb(35, 0, 220, 220))
                    };
                    Canvas.SetLeft(ring, cx - 13); Canvas.SetTop(ring, cy - 13);
                    OverlayCanvas.Children.Add(ring);

                    var inner = new Ellipse
                    {
                        Width = 10,
                        Height = 10,
                        Stroke = Brushes.Cyan,
                        StrokeThickness = 1.5,
                        Fill = Brushes.Transparent
                    };
                    Canvas.SetLeft(inner, cx - 5); Canvas.SetTop(inner, cy - 5);
                    OverlayCanvas.Children.Add(inner);
                }
                else if (gesture == "open")
                {
                    var tip = hand[8];
                    double cx = tip.x * CW;
                    double cy = tip.y * CH;

                    var ring = new Ellipse
                    {
                        Width = 24,
                        Height = 24,
                        Stroke = Brushes.White,
                        StrokeThickness = 2,
                        Fill = new SolidColorBrush(Color.FromArgb(35, 255, 255, 255))
                    };
                    Canvas.SetLeft(ring, cx - 12); Canvas.SetTop(ring, cy - 12);
                    OverlayCanvas.Children.Add(ring);
                }
                else if (gesture == "fist")
                {
                    var palm = hand[9];
                    double cx = palm.x * CW;
                    double cy = palm.y * CH;

                    if (_isDragging)
                    {
                        var ring = new Ellipse
                        {
                            Width = 30,
                            Height = 30,
                            Stroke = Brushes.Orange,
                            StrokeThickness = 3,
                            Fill = new SolidColorBrush(Color.FromArgb(50, 255, 165, 0))
                        };
                        Canvas.SetLeft(ring, cx - 15); Canvas.SetTop(ring, cy - 15);
                        OverlayCanvas.Children.Add(ring);
                    }
                    else if (_fistHoldStart != DateTime.MinValue)
                    {
                        double prog = Math.Min(
                            (DateTime.Now - _fistHoldStart).TotalSeconds / FIST_SELECT_SECONDS, 1.0);
                        var ring = new Ellipse
                        {
                            Width = 28,
                            Height = 28,
                            Stroke = new SolidColorBrush(Color.FromArgb(200, 255, (byte)(165 * (1 - prog)), 0)),
                            StrokeThickness = 2.5,
                            Fill = Brushes.Transparent
                        };
                        Canvas.SetLeft(ring, cx - 14); Canvas.SetTop(ring, cy - 14);
                        OverlayCanvas.Children.Add(ring);

                        int secLeft = (int)Math.Ceiling(FIST_SELECT_SECONDS - (DateTime.Now - _fistHoldStart).TotalSeconds);
                        var txt = new TextBlock { Text = secLeft.ToString(), FontSize = 11, Foreground = Brushes.Orange, FontWeight = FontWeights.Bold };
                        Canvas.SetLeft(txt, cx - 4); Canvas.SetTop(txt, cy - 7);
                        OverlayCanvas.Children.Add(txt);
                    }
                }
            }
        }

        private void DrawPalette()
        {
            var bg = new Rectangle
            {
                Width = CW,
                Height = PALETTE_H,
                Fill = new SolidColorBrush(Color.FromArgb(185, 8, 8, 28))
            };
            Canvas.SetLeft(bg, 0); Canvas.SetTop(bg, 0);
            OverlayCanvas.Children.Add(bg);

            var sep = new Line
            {
                X1 = 0,
                Y1 = PALETTE_H,
                X2 = CW,
                Y2 = PALETTE_H,
                Stroke = new SolidColorBrush(Color.FromArgb(120, 100, 100, 180)),
                StrokeThickness = 1
            };
            OverlayCanvas.Children.Add(sep);

            var categories = new Dictionary<string, double>
            {
                { "PINCEAUX", 4   },
                { "TAILLE",   181 },
                { "COULEURS", 281 },
                { "ACTIONS",  519 },
            };

            foreach (var kv in categories)
            {
                var catLabel = new TextBlock
                {
                    Text = kv.Key,
                    FontSize = 7,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromArgb(160, 180, 180, 255))
                };
                Canvas.SetLeft(catLabel, kv.Value);
                Canvas.SetTop(catLabel, 3);
                OverlayCanvas.Children.Add(catLabel);
            }

            foreach (var item in _palette)
            {
                bool isSelected = _selectedItem == item.Name;
                bool isHovered = _dwellItem == item.Name;

                var rect = new Rectangle
                {
                    Width = item.Bounds.Width,
                    Height = item.Bounds.Height,
                    Fill = new SolidColorBrush(item.FillColor),
                    Stroke = isSelected
                        ? new SolidColorBrush(Color.FromArgb(255, 255, 255, 255))
                        : new SolidColorBrush(Color.FromArgb(100, 120, 120, 180)),
                    StrokeThickness = isSelected ? 2.0 : 0.8,
                    RadiusX = 4,
                    RadiusY = 4
                };
                Canvas.SetLeft(rect, item.Bounds.X);
                Canvas.SetTop(rect, item.Bounds.Y);
                OverlayCanvas.Children.Add(rect);

                // ── Gestion de l'affichage (Icône, Point de taille, ou Texte) ──
                if (!string.IsNullOrEmpty(item.IconPath))
                {
                    try
                    {
                        var img = new Image
                        {
                            Source = new BitmapImage(new Uri(item.IconPath, UriKind.RelativeOrAbsolute)),
                            Width = 24,
                            Height = 24,
                            Stretch = Stretch.Uniform
                        };
                        Canvas.SetLeft(img, item.Bounds.X + (item.Bounds.Width - 24) / 2);
                        Canvas.SetTop(img, item.Bounds.Y + (item.Bounds.Height - 24) / 2);
                        OverlayCanvas.Children.Add(img);
                    }
                    catch
                    {
                        var fallbackLabel = new TextBlock
                        {
                            Text = item.Label,
                            FontSize = 9,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = Brushes.White,
                            Width = item.Bounds.Width,
                            TextAlignment = TextAlignment.Center
                        };
                        Canvas.SetLeft(fallbackLabel, item.Bounds.X);
                        Canvas.SetTop(fallbackLabel, item.Bounds.Y + item.Bounds.Height / 2.0 - 6);
                        OverlayCanvas.Children.Add(fallbackLabel);
                    }
                }
                else if (item.Category == "TAILLE")
                {
                    // Dessine un point (petit ou gros) pour les tailles
                    double dotSize = item.Name == "size_s" ? 6.0 : 14.0;
                    var dot = new Ellipse
                    {
                        Width = dotSize,
                        Height = dotSize,
                        Fill = Brushes.White
                    };
                    Canvas.SetLeft(dot, item.Bounds.X + (item.Bounds.Width - dotSize) / 2);
                    Canvas.SetTop(dot, item.Bounds.Y + (item.Bounds.Height - dotSize) / 2);
                    OverlayCanvas.Children.Add(dot);
                }
                else if (!string.IsNullOrEmpty(item.Label))
                {
                    // N'affiche le texte QUE si Label n'est pas vide
                    bool isDark = item.FillColor.R < 80 && item.FillColor.G < 80 && item.FillColor.B < 80;
                    var label = new TextBlock
                    {
                        Text = item.Label,
                        FontSize = 9,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = isDark ? Brushes.White : Brushes.Black,
                        Width = item.Bounds.Width,
                        TextAlignment = TextAlignment.Center
                    };
                    Canvas.SetLeft(label, item.Bounds.X);
                    Canvas.SetTop(label, item.Bounds.Y + item.Bounds.Height / 2.0 - 6);
                    OverlayCanvas.Children.Add(label);
                }

                if (isHovered)
                {
                    double progress = Math.Min(
                        (DateTime.Now - _dwellStart).TotalSeconds / DWELL_SECONDS, 1.0);

                    var bar = new Rectangle
                    {
                        Width = item.Bounds.Width * progress,
                        Height = 4,
                        Fill = Brushes.Cyan,
                        RadiusX = 2,
                        RadiusY = 2
                    };
                    Canvas.SetLeft(bar, item.Bounds.X);
                    Canvas.SetTop(bar, item.Bounds.Y + item.Bounds.Height - 4);
                    OverlayCanvas.Children.Add(bar);
                }
            }

            var hint = new TextBlock
            {
                Text = "✌ index+majeur 1.5s = sélect  |  ✊ poing 3s = déplacer dessin  |  ✋✋ 4s = sauvegarder",
                FontSize = 7.5,
                Foreground = new SolidColorBrush(Color.FromArgb(140, 200, 200, 255)),
                TextAlignment = TextAlignment.Left
            };
            Canvas.SetLeft(hint, 4);
            Canvas.SetTop(hint, PALETTE_H + 2);
            OverlayCanvas.Children.Add(hint);
        }

        private void DrawSaveProgress(double heldSeconds)
        {
            double progress = Math.Min(heldSeconds / SAVE_HOLD_SECONDS, 1.0);
            double barW = CW * progress;

            var bgBar = new Rectangle
            {
                Width = CW,
                Height = 8,
                Fill = new SolidColorBrush(Color.FromArgb(100, 40, 40, 80))
            };
            Canvas.SetLeft(bgBar, 0); Canvas.SetTop(bgBar, CH - 8);
            OverlayCanvas.Children.Add(bgBar);

            byte r = (byte)(255 * (1.0 - progress));
            byte g = (byte)(200 * progress + 100);
            var fgBar = new Rectangle
            {
                Width = barW,
                Height = 8,
                Fill = new SolidColorBrush(Color.FromRgb(r, g, 60)),
                RadiusX = 2,
                RadiusY = 2
            };
            Canvas.SetLeft(fgBar, 0); Canvas.SetTop(fgBar, CH - 8);
            OverlayCanvas.Children.Add(fgBar);

            var txt = new TextBlock
            {
                Text = $"✋  Maintenez les deux mains ouvertes...  {(int)(heldSeconds + 1)}s / {(int)SAVE_HOLD_SECONDS}s",
                Foreground = Brushes.White,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                Width = CW,
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 6, ShadowDepth = 2 }
            };
            Canvas.SetLeft(txt, 0); Canvas.SetTop(txt, CH - 30);
            OverlayCanvas.Children.Add(txt);
        }

        private void DrawFinalOverlay()
        {
            var bg = new Rectangle
            {
                Width = CW,
                Height = CH,
                Fill = new SolidColorBrush(Color.FromArgb(145, 0, 0, 0))
            };
            Canvas.SetLeft(bg, 0); Canvas.SetTop(bg, 0);
            OverlayCanvas.Children.Add(bg);

            var title = new TextBlock
            {
                Text = "✋  Dessin sauvegardé  ✋",
                Foreground = Brushes.White,
                FontSize = 32,
                FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Center,
                Width = CW,
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 10, ShadowDepth = 4 }
            };
            Canvas.SetLeft(title, 0); Canvas.SetTop(title, CH / 2 - 65);
            OverlayCanvas.Children.Add(title);

            if (_lastSavedPath != null)
            {
                var pathLabel = new TextBlock
                {
                    Text = System.IO.Path.GetFileName(_lastSavedPath),
                    Foreground = new SolidColorBrush(Colors.LimeGreen),
                    FontSize = 14,
                    TextAlignment = TextAlignment.Center,
                    Width = CW
                };
                Canvas.SetLeft(pathLabel, 0); Canvas.SetTop(pathLabel, CH / 2 - 20);
                OverlayCanvas.Children.Add(pathLabel);
            }

            var hint = new TextBlock
            {
                Text = "Baissez une main pour reprendre le dessin",
                Foreground = new SolidColorBrush(Colors.LightGray),
                FontSize = 14,
                TextAlignment = TextAlignment.Center,
                Width = CW
            };
            Canvas.SetLeft(hint, 0); Canvas.SetTop(hint, CH / 2 + 10);
            OverlayCanvas.Children.Add(hint);
        }

        private void DrawSelectionHighlight()
        {
            if (_selectedStroke == null || _selectedStroke.Points.Count == 0) return;

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (var pt in _selectedStroke.Points)
            {
                if (pt.X < minX) minX = pt.X;
                if (pt.Y < minY) minY = pt.Y;
                if (pt.X > maxX) maxX = pt.X;
                if (pt.Y > maxY) maxY = pt.Y;
            }

            const double pad = 14.0;
            double w = maxX - minX + pad * 2;
            double h = maxY - minY + pad * 2;

            var fill = new Rectangle
            {
                Width = w,
                Height = h,
                Fill = new SolidColorBrush(Color.FromArgb(30, 255, 165, 0)),
                RadiusX = 6,
                RadiusY = 6
            };
            Canvas.SetLeft(fill, minX - pad); Canvas.SetTop(fill, minY - pad);
            OverlayCanvas.Children.Add(fill);

            var border = new Rectangle
            {
                Width = w,
                Height = h,
                Stroke = Brushes.Orange,
                StrokeThickness = 2.5,
                StrokeDashArray = new DoubleCollection { 6, 3 },
                Fill = Brushes.Transparent,
                RadiusX = 6,
                RadiusY = 6
            };
            Canvas.SetLeft(border, minX - pad); Canvas.SetTop(border, minY - pad);
            OverlayCanvas.Children.Add(border);

            var lbl = new TextBlock
            {
                Text = "⬆ Déplacer",
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.Orange,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 4, ShadowDepth = 1 }
            };
            Canvas.SetLeft(lbl, minX - pad);
            Canvas.SetTop(lbl, Math.Max(0, minY - pad - 16));
            OverlayCanvas.Children.Add(lbl);
        }

        private double GetCurrentThickness() => _tool switch
        {
            BrushTool.Crayon => 3.0 * _sizeMultiplier,
            BrushTool.Marqueur => 20.0 * _sizeMultiplier,
            _ => 8.0 * _sizeMultiplier
        };

        private double GetDrawThreshold() => _tool switch
        {
            BrushTool.Marqueur => 10.0,
            BrushTool.Crayon => 7.0,
            _ => 7.0
        };

        private string GetCurrentColor() => _drawColor switch
        {
            var c when c == Colors.Red => "Rouge",
            var c when c == Colors.LimeGreen => "Vert",
            var c when c == Colors.DodgerBlue => "Bleu",
            var c when c == Colors.Yellow => "Jaune",
            var c when c == Colors.White => "Blanc",
            var c when c == Colors.Orange => "Orange",
            _ => "Couleur"
        };
    }
}