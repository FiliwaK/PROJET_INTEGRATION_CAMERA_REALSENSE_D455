// ============================================================
//  MainViewModel.cs  —  ViewModel principal (pattern MVVM)
//
//  Rôle : fait le lien entre la socket TCP (données brutes)
//         et l'interface WPF (affichage).
//
//  Pattern MVVM (Model – View – ViewModel) :
//    Model     = HandData / PointModel (données)
//    View      = MainWindow.xaml (interface)
//    ViewModel = ce fichier (logique + état)
//
//  Ce que fait ce ViewModel :
//    1. Se connecte au serveur Python via SocketClient
//    2. Lit les messages JSON dans un thread de fond (Task.Run)
//    3. Met à jour StatusText / StatusColor sur le thread UI
//    4. Déclenche l'événement DataReceived → MainWindow dessine
//
//  Correction de l'erreur NullReferenceException :
//  ─────────────────────────────────────────────────
//  Ancienne version : le thread de fond appelait
//    Application.Current.Dispatcher.Invoke(...)
//  sans vérifier si Application.Current était null.
//  Quand on ferme la fenêtre, WPF met Application.Current
//  à null AVANT que le thread de fond ne s'arrête
//  → crash garanti.
//
//  Solution : CancellationToken + vérification null
//    - Stop() annule le token → le thread sort proprement
//    - Avant chaque Dispatcher.Invoke, on vérifie que
//      Application.Current != null
// ============================================================

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using Newtonsoft.Json;

public class MainViewModel : INotifyPropertyChanged
{
    // ── Dépendances ─────────────────────────────────────────────────────────────

    // Client TCP qui lit les messages JSON envoyés par Python
    private readonly SocketClient _socket = new SocketClient();

    // CancellationTokenSource : permet d'envoyer un signal d'arrêt
    // au thread de fond depuis le thread principal (quand la fenêtre ferme).
    // C'est LA correction de l'erreur NullReferenceException.
    private CancellationTokenSource _cts = new CancellationTokenSource();

    // ── Propriétés bindées au XAML ───────────────────────────────────────────────
    // INotifyPropertyChanged : notifie le XAML quand une valeur change
    // → le binding WPF met à jour l'affichage automatiquement

    private string _statusText = "En attente de connexion...";
    /// <summary>Texte affiché dans la barre de statut en bas de la fenêtre.</summary>
    public string StatusText
    {
        get => _statusText;
        // OnPropertyChanged() déclenche l'événement → WPF relit la propriété
        set { _statusText = value; OnPropertyChanged(); }
    }

    private string _statusColor = "Gray";
    /// <summary>
    /// Nom de couleur WPF pour le point indicateur (ex: "LimeGreen", "Orange", "Red").
    /// MainWindow le convertit en SolidColorBrush via ColorConverter.
    /// </summary>
    public string StatusColor
    {
        get => _statusColor;
        set { _statusColor = value; OnPropertyChanged(); }
    }

    // ── Événements ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Déclenché sur le thread UI à chaque nouvelle trame reçue de Python.
    /// MainWindow s'y abonne pour redessiner les marqueurs sur le canvas.
    /// (Action&lt;HandData&gt; = délégué qui prend un HandData en paramètre)
    /// </summary>
    public event Action<HandData>? DataReceived;

    // Requis par INotifyPropertyChanged pour notifier le XAML des changements
    public event PropertyChangedEventHandler? PropertyChanged;

    // ── Démarrage ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Appelé depuis MainWindow au démarrage.
    /// 1. Tente la connexion TCP au serveur Python
    /// 2. Lance la boucle de réception dans un thread de fond
    /// </summary>
    public void Start()
    {
        try
        {
            // Tente la connexion TCP (échoue si Python n'est pas lancé)
            _socket.Connect();
            StatusText = "Connecté — en attente de détection...";
            StatusColor = "Orange";
        }
        catch (Exception ex)
        {
            // Python pas lancé ou port occupé → on informe l'utilisateur
            StatusText = $"Erreur de connexion: {ex.Message}";
            StatusColor = "Red";
            return; // on n'essaie pas de lancer le thread si pas connecté
        }

        // Capture le token AVANT de rentrer dans le Task.Run
        // (le token est partagé entre le thread principal et le thread de fond)
        var token = _cts.Token;

        // Task.Run : exécute la boucle dans un thread de fond (ThreadPool)
        // → indispensable car SocketClient.Receive() est bloquant
        //   (il attend qu'une ligne arrive) → bloquerait le thread UI sinon
        Task.Run(() =>
        {
            // Boucle de lecture continue — s'arrête quand le token est annulé
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // Bloquant : attend la prochaine ligne JSON de Python
                    var json = _socket.Receive();

                    // Si la socket est fermée, ReadLine() retourne null / vide
                    if (string.IsNullOrWhiteSpace(json))
                        continue;

                    // Désérialise le JSON en objet C# HandData
                    // Newtonsoft.Json mappe automatiquement les clés JSON
                    // sur les propriétés de HandData (mêmes noms)
                    var data = JsonConvert.DeserializeObject<HandData>(json);
                    if (data == null)
                        continue;

                    // SÉCURITÉ CRITIQUE : vérifie que l'app n'est pas déjà fermée
                    // et que l'arrêt n'a pas été demandé entre le Receive et ici
                    if (Application.Current == null || token.IsCancellationRequested)
                        break;

                    // Dispatcher.Invoke : retourne sur le thread UI pour
                    // mettre à jour l'interface (WPF interdit les mises à jour
                    // UI depuis un thread autre que le thread principal)
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (data.hand_detected || data.body_detected)
                        {
                            // Compose le message selon ce qui est détecté
                            var parts = new List<string>();
                            if (data.hand_detected) parts.Add("main(s)");
                            if (data.body_detected) parts.Add("corps");
                            StatusText = $"Mouvement détecté : {string.Join(" + ", parts)}";
                            StatusColor = "LimeGreen"; // point vert = tout va bien
                        }
                        else
                        {
                            StatusText = "Aucun mouvement détecté";
                            StatusColor = "Orange"; // point orange = connecté mais rien
                        }

                        // Notifie MainWindow pour redessiner les marqueurs sur le canvas
                        DataReceived?.Invoke(data);
                    });
                }
                catch (OperationCanceledException)
                {
                    // Levé quand le token est annulé pendant un await/opération
                    // → arrêt normal et propre, pas une erreur
                    break;
                }
                catch (Exception ex)
                {
                    // Erreur réseau (déconnexion Python) ou parsing JSON invalide
                    // → on affiche l'erreur et on sort de la boucle
                    if (Application.Current != null && !token.IsCancellationRequested)
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            StatusText = $"Erreur: {ex.Message}";
                            StatusColor = "Red";
                        });
                    }
                    break;
                }
            }
        }, token); // on passe le token à Task.Run pour qu'il annule aussi la tâche
    }

    /// <summary>
    /// Arrête proprement le thread de fond et ferme la socket TCP.
    /// Appelé par MainWindow.Closing → AVANT que Application.Current devienne null.
    /// C'est la clé pour éviter le NullReferenceException.
    /// </summary>
    public void Stop()
    {
        // Envoie le signal d'annulation au thread de fond
        // → token.IsCancellationRequested devient true
        // → la boucle while(!token.IsCancellationRequested) s'arrête
        _cts.Cancel();

        // Ferme la connexion TCP → ReadLine() dans le thread de fond
        // va retourner null ou lever une exception → sortie propre
        _socket.Disconnect();
    }

    // ── MVVM helper ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Notifie le binding XAML qu'une propriété a changé.
    /// [CallerMemberName] remplit automatiquement le nom de la propriété
    /// → pas besoin d'écrire OnPropertyChanged("StatusText") manuellement.
    /// </summary>
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
