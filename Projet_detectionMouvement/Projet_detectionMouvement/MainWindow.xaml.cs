// ============================================================
//  MainWindow.xaml.cs  —  Code-behind de la fenêtre principale
//
//  Rôle : affiche les marqueurs visuels (points + lignes)
//         sur le canvas WPF à partir des données reçues de Python.
//
//  Ce fichier s'occupe UNIQUEMENT de l'affichage (View dans MVVM).
//  La logique (connexion, réception, statut) est dans MainViewModel.
//
//  Dessin :
//    - Mains  : points rouges + lignes jaunes (21 points, MediaPipe Hands)
//    - Corps  : points cyan   + lignes vertes (33 points, MediaPipe Pose)
//
//  Coordonnées :
//    MediaPipe retourne des valeurs normalisées [0.0 – 1.0]
//    On multiplie par 640 (largeur) et 480 (hauteur) du canvas
//    pour obtenir les pixels réels.
// ============================================================

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Projet_detectionMouvement
{
    public partial class MainWindow : Window
    {
        // ViewModel : gère la connexion TCP et la réception des données
        private readonly MainViewModel _vm = new MainViewModel();

        // ── Tables de connexions MediaPipe ───────────────────────────────────────
        //
        // Ces tableaux définissent quels points relier par une ligne
        // pour former le squelette. Chaque paire (A, B) = indice des deux
        // points à connecter (selon la numérotation MediaPipe).

        /// <summary>
        /// Connexions du squelette du corps (MediaPipe Pose, 33 landmarks).
        /// Chaque paire (A, B) = deux indices de points à relier par une ligne.
        /// Référence : https://developers.google.com/mediapipe/solutions/vision/pose_landmarker
        /// </summary>
        private static readonly (int A, int B)[] PoseConnections =
        {
            (11, 12),               // épaule gauche ↔ épaule droite
            (11, 13), (13, 15),     // bras gauche  : épaule → coude → poignet
            (12, 14), (14, 16),     // bras droit   : épaule → coude → poignet
            (11, 23), (12, 24),     // torse        : épaules → hanches
            (23, 24),               // hanche gauche ↔ hanche droite
            (23, 25), (25, 27),     // jambe gauche : hanche → genou → cheville
            (24, 26), (26, 28),     // jambe droite : hanche → genou → cheville
        };

        /// <summary>
        /// Connexions de la main (MediaPipe Hands, 21 landmarks par main).
        /// Référence : https://developers.google.com/mediapipe/solutions/vision/hand_landmarker
        /// </summary>
        private static readonly (int A, int B)[] HandConnections =
        {
            (0,1),(1,2),(2,3),(3,4),           // pouce        : poignet → bout
            (0,5),(5,6),(6,7),(7,8),            // index        : poignet → bout
            (0,9),(9,10),(10,11),(11,12),       // majeur       : poignet → bout
            (0,13),(13,14),(14,15),(15,16),     // annulaire    : poignet → bout
            (0,17),(17,18),(18,19),(19,20),     // auriculaire  : poignet → bout
            (5,9),(9,13),(13,17),               // paume : joints des bases des doigts
        };

        // ── Constructeur ─────────────────────────────────────────────────────────

        public MainWindow()
        {
            // Initialise les composants définis dans MainWindow.xaml
            InitializeComponent();

            // Abonnement aux changements de propriétés du ViewModel.
            // Quand StatusText ou StatusColor changent, on met à jour
            // les contrôles correspondants dans le XAML.
            _vm.PropertyChanged += (_, e) =>
            {
                // Mise à jour du texte de statut
                if (e.PropertyName == nameof(_vm.StatusText))
                    StatusText.Text = _vm.StatusText;

                // Mise à jour de la couleur du point indicateur
                // ColorConverter.ConvertFromString("LimeGreen") → Color struct WPF
                if (e.PropertyName == nameof(_vm.StatusColor))
                    StatusDot.Fill = new SolidColorBrush(
                        (Color)ColorConverter.ConvertFromString(_vm.StatusColor));
            };

            // À chaque nouvelle trame reçue de Python, on redessine les marqueurs
            _vm.DataReceived += DrawFrame;

            // CORRECTION de l'erreur NullReferenceException :
            // On s'abonne à l'événement Closing de la fenêtre.
            // Quand l'utilisateur ferme la fenêtre (clic sur la croix),
            // Stop() est appelé AVANT que Application.Current devienne null.
            // Cela envoie le signal d'annulation au thread de fond.
            Closing += (_, _) => _vm.Stop();

            // Lance la connexion TCP et démarre le thread de réception
            _vm.Start();
        }

        // ── Dessin principal ─────────────────────────────────────────────────────

        /// <summary>
        /// Appelé sur le thread UI à chaque nouvelle trame (par le ViewModel).
        /// Efface l'ancienne frame et redessine tout.
        /// </summary>
        private void DrawFrame(HandData data)
        {
            // On efface tous les éléments du canvas avant de redessiner
            // (sinon les marqueurs s'accumulent et ralentissent l'app)
            OverlayCanvas.Children.Clear();

            // Dessine chaque main détectée (0, 1 ou 2)
            if (data.hands != null)
                foreach (var hand in data.hands)
                    DrawHand(hand);

            // Dessine le squelette du corps si des points sont présents
            if (data.body != null && data.body.Count > 0)
                DrawBody(data.body);
        }

        // ── Dessin des mains ─────────────────────────────────────────────────────

        /// <summary>
        /// Dessine une main : lignes jaunes entre articulations + points rouges.
        ///
        /// Paramètre pts : liste de 21 PointModel avec x/y normalisés [0–1].
        /// Conversion pixel : x * 640, y * 480 (taille du canvas en XAML).
        /// </summary>
        private void DrawHand(List<PointModel> pts)
        {
            // ── Lignes du squelette de la main (jaunes) ──────────────────────────
            foreach (var (a, b) in HandConnections)
            {
                // Sécurité : ignore si l'index dépasse la liste
                // (ne devrait pas arriver, mais protège contre un JSON partiel)
                if (a >= pts.Count || b >= pts.Count) continue;

                // Crée une ligne entre les points a et b
                OverlayCanvas.Children.Add(new Line
                {
                    X1 = pts[a].x * 640,   // coordonnée X du point A en pixels
                    Y1 = pts[a].y * 480,   // coordonnée Y du point A en pixels
                    X2 = pts[b].x * 640,   // coordonnée X du point B en pixels
                    Y2 = pts[b].y * 480,   // coordonnée Y du point B en pixels
                    Stroke = Brushes.Yellow,
                    StrokeThickness = 1.5
                });
            }

            // ── Points articulaires (rouges) ─────────────────────────────────────
            foreach (var p in pts)
            {
                var dot = new Ellipse
                {
                    Width = 8, Height = 8,
                    Fill = Brushes.Red,
                    Stroke = Brushes.White,     // contour blanc pour la lisibilité
                    StrokeThickness = 1
                };
                // Canvas.SetLeft/Top positionne le coin haut-gauche de l'ellipse
                // → on soustrait le rayon (4) pour centrer le point
                Canvas.SetLeft(dot, p.x * 640 - 4);
                Canvas.SetTop(dot, p.y * 480 - 4);
                OverlayCanvas.Children.Add(dot);
            }
        }

        // ── Dessin du corps ──────────────────────────────────────────────────────

        /// <summary>
        /// Dessine le squelette du corps : lignes vertes + points cyan.
        /// Les points avec visibility &lt; 0.5 sont ignorés (hors champ ou cachés).
        ///
        /// Paramètre pts : liste de 33 PointModel (landmarks MediaPipe Pose).
        /// </summary>
        private void DrawBody(List<PointModel> pts)
        {
            // ── Lignes du squelette (vertes) ─────────────────────────────────────
            foreach (var (a, b) in PoseConnections)
            {
                if (a >= pts.Count || b >= pts.Count) continue;

                // Ne dessine pas la ligne si l'un des points est peu visible
                // (ex : jambes hors du champ de la caméra)
                if (pts[a].visibility < 0.5f || pts[b].visibility < 0.5f) continue;

                OverlayCanvas.Children.Add(new Line
                {
                    X1 = pts[a].x * 640,
                    Y1 = pts[a].y * 480,
                    X2 = pts[b].x * 640,
                    Y2 = pts[b].y * 480,
                    Stroke = Brushes.LimeGreen,
                    StrokeThickness = 2
                });
            }

            // ── Points articulaires du corps (cyan) ──────────────────────────────
            foreach (var p in pts)
            {
                // Ignore les points peu visibles (cachés ou hors champ)
                if (p.visibility < 0.5f) continue;

                var dot = new Ellipse
                {
                    Width = 10, Height = 10,   // légèrement plus grand que les mains
                    Fill = Brushes.Cyan,
                    Stroke = Brushes.White,
                    StrokeThickness = 1
                };
                // Centre le point : on soustrait le rayon (5)
                Canvas.SetLeft(dot, p.x * 640 - 5);
                Canvas.SetTop(dot, p.y * 480 - 5);
                OverlayCanvas.Children.Add(dot);
            }
        }
    }
}
