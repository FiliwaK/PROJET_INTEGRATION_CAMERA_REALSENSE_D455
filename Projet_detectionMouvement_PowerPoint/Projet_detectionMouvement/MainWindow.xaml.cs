using Microsoft.Win32;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Projet_detectionMouvement
{
    public partial class MainWindow : Window
    {
        private const int CW = 640;
        private const int CH = 480;

        private readonly MainViewModel _vm = new MainViewModel();
        private readonly VideoClient _videoClient = new VideoClient();
        private readonly CancellationTokenSource _videoCts = new CancellationTokenSource();
        private readonly GestureRecognizer _gestures = new GestureRecognizer();

        // PPT
        private PowerPointService? _pptService = null;
        private int _currentSlideIndex = 0;
        private bool _slideAActive = true;
        private bool _isPresentationMode = false;
        private bool _isPptSelected = false;
        private double _slideScale = 1.0;

        // Variables pour le calcul des FPS
        private int _frameCount = 0;
        private DateTime _lastFpsTime = DateTime.Now;

        private static readonly (int A, int B)[] HandConnections =
        {
            (0,1),(1,2),(2,3),(3,4),
            (0,5),(5,6),(6,7),(7,8),
            (0,9),(9,10),(10,11),(11,12),
            (0,13),(13,14),(14,15),(15,16),
            (0,17),(17,18),(18,19),(19,20),
            (5,9),(9,13),(13,17),
        };

        public MainWindow()
        {
            InitializeComponent();

            _vm.DataReceived += DrawFrame;

            // ── SÉCURITÉ : Navigation slides (Seulement si NON sélectionné) ──
            _gestures.SwipedRight += () => Dispatcher.Invoke(() => {
                if (!_isPptSelected) ShowSlide(_currentSlideIndex + 1, true);
            });

            _gestures.SwipedLeft += () => Dispatcher.Invoke(() => {
                if (!_isPptSelected) ShowSlide(_currentSlideIndex - 1, false);
            });

            // Deux mains – fermer la présentation
            _gestures.BothHandsOpenHeld += () => Dispatcher.Invoke(ExitPresentationMode);

            // Main droite – manipulation slide
            _gestures.RightHandToggledSelection += () => Dispatcher.Invoke(TogglePptSelection);
            _gestures.RightHandMoved += (dx, dy) => Dispatcher.Invoke(() => MovePpt(dx, dy));
            _gestures.RightHandZoomed += (factor) => Dispatcher.Invoke(() => ZoomPpt(factor));

            // Flux vidéo avec reconnexion automatique
            _videoClient.FrameReceived += bmp => Dispatcher.BeginInvoke(() => CameraBackground.Source = bmp);

            Task.Run(async () =>
            {
                while (!_videoCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        _videoClient.Connect("127.0.0.1", 5001);
                        _videoClient.ReceiveLoop(_videoCts.Token);
                    }
                    catch
                    {
                        await Task.Delay(1500);
                    }
                    finally
                    {
                        _videoClient.Disconnect();
                    }
                }
            });

            Closing += (_, _) =>
            {
                _videoCts.Cancel();
                _videoClient.Disconnect();
                _vm.Stop();
                _pptService?.Dispose();
            };

            _vm.Start();
        }

        // ── LOGIQUE POWERPOINT ───────────────────────────────────────────────

        private void TogglePptSelection()
        {
            if (!_isPresentationMode) return;
            _isPptSelected = !_isPptSelected;
            PptSelectionBorder.BorderThickness = _isPptSelected ? new Thickness(6) : new Thickness(0);
        }

        private void MovePpt(float dx, float dy)
        {
            if (!_isPptSelected || !_isPresentationMode) return;
            PptTranslate.X += dx * 1500;
            PptTranslate.Y += dy * 1500;
        }

        private void ZoomPpt(float factor)
        {
            if (!_isPptSelected || !_isPresentationMode) return;
            _slideScale = Math.Clamp(_slideScale * factor, 0.5, 4.0);
            PptScale.ScaleX = _slideScale;
            PptScale.ScaleY = _slideScale;
        }

        private void BtnOpenPpt_Click(object sender, RoutedEventArgs e)
        {
            if (_isPresentationMode) return;

            var dlg = new OpenFileDialog { Filter = "PowerPoint|*.pptx;*.ppt" };
            if (dlg.ShowDialog() != true) return;

            BtnText.Text = "CHARGEMENT EN COURS...";
            BtnIcon.Text = "⌛  ";
            BtnOpenPpt.IsEnabled = false;

            Task.Run(() =>
            {
                var svc = new PowerPointService();
                bool ok = svc.Open(dlg.FileName);

                Dispatcher.Invoke(() =>
                {
                    if (ok)
                    {
                        _pptService = svc;
                        EnterPresentationMode();
                    }
                    else
                    {
                        svc.Dispose();
                        MessageBox.Show("Erreur lors de l'ouverture du PowerPoint.");
                        BtnText.Text = "OUVRIR POWERPOINT";
                        BtnIcon.Text = "●  ";
                        BtnOpenPpt.IsEnabled = true;
                    }
                });
            });
        }

        private void BtnClosePpt_Click(object sender, RoutedEventArgs e)
        {
            ExitPresentationMode();
        }

        private void EnterPresentationMode()
        {
            _isPresentationMode = true;
            _currentSlideIndex = 0;
            _isPptSelected = false;
            _slideScale = 1.0;

            PptTranslate.X = 0; PptTranslate.Y = 0;
            PptScale.ScaleX = 1; PptScale.ScaleY = 1;
            PptSelectionBorder.BorderThickness = new Thickness(0);

            _gestures.Reset();

            // Cache le bouton d'ouverture, affiche le PPT et le compteur de slides
            OpenPptPanel.Visibility = Visibility.Collapsed;
            PptSlidePanel.Visibility = Visibility.Visible;
            BtnClosePpt.Visibility = Visibility.Visible;
            SlideCounterText.Visibility = Visibility.Visible;
            SlideLabel.Visibility = Visibility.Visible;

            // Statut en Haut à Droite -> Actif (Vert)
            PresentationStatusText.Text = "PRÉSENTATION ACTIVE";
            PresentationStatusText.Foreground = Brushes.LimeGreen;
            PresentationStatusText.Effect = (DropShadowEffect)FindResource("GreenGlow");
            PresentationStatusDot.Fill = Brushes.LimeGreen;
            PresentationStatusDot.Effect = (DropShadowEffect)FindResource("GreenGlow");

            ShowSlide(0, true);
        }

        private void ExitPresentationMode()
        {
            if (!_isPresentationMode) return;

            _isPresentationMode = false;
            _isPptSelected = false;

            // Cache le PPT et le compteur de slides, affiche le bouton d'ouverture
            PptSlidePanel.Visibility = Visibility.Collapsed;
            BtnClosePpt.Visibility = Visibility.Collapsed;
            SlideCounterText.Visibility = Visibility.Collapsed;
            SlideLabel.Visibility = Visibility.Collapsed;
            OpenPptPanel.Visibility = Visibility.Visible;

            // Statut en Haut à Droite -> Inactif (Rouge)
            PresentationStatusText.Text = "PRÉSENTATION INACTIVE";
            PresentationStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF3060"));
            PresentationStatusText.Effect = (DropShadowEffect)FindResource("RedGlow");
            PresentationStatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF3060"));
            PresentationStatusDot.Effect = (DropShadowEffect)FindResource("RedGlow");

            // Réinitialise le bouton proprement
            BtnText.Text = "OUVRIR POWERPOINT";
            BtnIcon.Text = "●  ";
            BtnOpenPpt.IsEnabled = true;

            _pptService?.Dispose();
            _pptService = null;
            _gestures.Reset();
        }

        private void ShowSlide(int index, bool forward)
        {
            if (_pptService == null || _pptService.SlideImagePaths.Count == 0) return;

            index = Math.Clamp(index, 0, _pptService.SlideCount - 1);
            var bmp = new BitmapImage(new Uri(_pptService.SlideImagePaths[index]));
            bmp.Freeze();

            var activeImg = _slideAActive ? SlideImageA : SlideImageB;
            var incomingImg = _slideAActive ? SlideImageB : SlideImageA;

            incomingImg.Source = bmp;

            var dur = new Duration(TimeSpan.FromMilliseconds(400));
            incomingImg.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur));
            activeImg.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, dur));

            _slideAActive = !_slideAActive;
            _currentSlideIndex = index;

            // Met à jour le texte du compteur (ex: "1 / 8")
            SlideCounterText.Text = $"{index + 1} / {_pptService.SlideCount}";
        }

        // ── DESSIN SQUELETTE ET FPS ───────────────────────────────────────────

        private void DrawFrame(HandData data)
        {
            // Calcul des FPS
            _frameCount++;
            var now = DateTime.Now;
            if ((now - _lastFpsTime).TotalSeconds >= 1)
            {
                FpsCounterText.Text = $"FPS  ·  {_frameCount}";
                _frameCount = 0;
                _lastFpsTime = now;
            }

            _gestures.Update(data);
            OverlayCanvas.Children.Clear();
            DrawHandLandmarks(data);
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
                        Stroke = new SolidColorBrush(Color.FromArgb(150, 0, 255, 255)),
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
                        Stroke = Brushes.White,
                        StrokeThickness = 1
                    };
                    Canvas.SetLeft(dot, p.x * CW - 3.5);
                    Canvas.SetTop(dot, p.y * CH - 3.5);
                    OverlayCanvas.Children.Add(dot);
                }
            }
        }
    }
}