// ============================================================
//  MainViewModel.cs  —  ViewModel Digital Twin (Version 3D)
//
//  Identique à l'ancienne version SAUF :
//    - HandData  remplacé par  BallData
//    - Le message de statut indique la position Z de la balle
// ============================================================

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using Newtonsoft.Json;

public class MainViewModel : INotifyPropertyChanged
{
    private readonly SocketClient _socket = new SocketClient();
    private CancellationTokenSource _cts = new CancellationTokenSource();

    // ── Propriétés bindées au XAML ───────────────────────────────────────────

    private string _statusText = "En attente de connexion...";
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    private string _statusColor = "Gray";
    public string StatusColor
    {
        get => _statusColor;
        set { _statusColor = value; OnPropertyChanged(); }
    }

    // ── Événement → MainWindow dessine ──────────────────────────────────────
    public event Action<BallData>? DataReceived;
    public event PropertyChangedEventHandler? PropertyChanged;

    // ── Démarrage ────────────────────────────────────────────────────────────

    public void Start()
    {
        try
        {
            _socket.Connect();
            StatusText = "Connecté — en attente de détection...";
            StatusColor = "Orange";
        }
        catch (Exception ex)
        {
            StatusText = $"Erreur connexion : {ex.Message}";
            StatusColor = "Red";
            return;
        }

        var token = _cts.Token;

        Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var json = _socket.Receive();
                    if (string.IsNullOrWhiteSpace(json)) continue;

                    // Désérialise en BallData (x, y, z, ball_detected...)
                    var data = JsonConvert.DeserializeObject<BallData>(json);
                    if (data == null) continue;

                    if (Application.Current == null || token.IsCancellationRequested)
                        break;

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (data.ball_detected && data.z > 0)
                        {
                            StatusText = $"Balle détectée  |  Z : {data.z:0.00} m";
                            StatusColor = "LimeGreen";
                        }
                        else
                        {
                            StatusText = "Aucune balle détectée";
                            StatusColor = "Orange";
                        }

                        // Notifie MainWindow pour déplacer la sphère
                        DataReceived?.Invoke(data);
                    });
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    if (Application.Current != null && !token.IsCancellationRequested)
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            StatusText = $"Erreur : {ex.Message}";
                            StatusColor = "Red";
                        });
                    break;
                }
            }
        }, token);
    }

    public void Stop()
    {
        _cts.Cancel();
        _socket.Disconnect();
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}