// ============================================================
//  VideoClient.cs  —  Client TCP pour le flux vidéo aperçu caméra
//
//  Rôle : se connecte au serveur vidéo Python (port 5001) et
//         reçoit les frames JPEG compressées pour les afficher
//         dans le petit aperçu caméra de l'interface.
//
//  Protocole :
//    Chaque frame = 4 octets (int big-endian) = taille JPEG
//                 + N octets = données JPEG
//
//  L'événement FrameReceived est déclenché sur le thread de fond.
//  Le BitmapImage est Freeze()d pour être utilisable sur le thread UI.
// ============================================================

using System.IO;
using System.Net.Sockets;
using System.Windows.Media.Imaging;

public class VideoClient
{
    private TcpClient?    _client;
    private NetworkStream? _stream;

    /// <summary>
    /// Déclenché dans un thread de fond à chaque frame reçue.
    /// Le BitmapImage est Freeze()d → utilisable directement sur le thread UI.
    /// </summary>
    public event Action<BitmapImage>? FrameReceived;

    /// <summary>
    /// Tente la connexion TCP au serveur vidéo Python.
    /// Lève une exception si Python n'est pas encore prêt (ConnectionRefused).
    /// </summary>
    public void Connect(string host = "127.0.0.1", int port = 5001)
    {
        _client = new TcpClient();
        _client.Connect(host, port);
        _stream = _client.GetStream();
    }

    /// <summary>
    /// Lance la boucle de réception dans un thread de fond.
    /// Lit les frames JPEG (protocole 4-byte header + data) et déclenche FrameReceived.
    /// </summary>
    public void StartReceiving(CancellationToken token)
    {
        Task.Run(() =>
        {
            var lenBuf = new byte[4];

            while (!token.IsCancellationRequested)
            {
                try
                {
                    // Lit exactement 4 octets → taille de la prochaine frame JPEG
                    ReadExact(_stream!, lenBuf, 4);

                    // Décode l'entier big-endian
                    int frameLen = (lenBuf[0] << 24) | (lenBuf[1] << 16)
                                 | (lenBuf[2] <<  8) |  lenBuf[3];

                    if (frameLen <= 0 || frameLen > 500_000)
                        continue; // valeur absurde → on saute pour éviter un crash

                    // Lit les données JPEG
                    var jpegData = new byte[frameLen];
                    ReadExact(_stream!, jpegData, frameLen);

                    // Convertit en BitmapImage (décodage JPEG)
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.StreamSource  = new MemoryStream(jpegData);
                    bmp.CacheOption   = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze(); // indispensable : Freeze() permet l'utilisation cross-thread

                    FrameReceived?.Invoke(bmp);
                }
                catch (OperationCanceledException) { break; }
                catch { break; }
            }
        }, token);
    }

    /// <summary>
    /// Lit exactement 'count' octets depuis le NetworkStream.
    /// TCP peut fragmenter → on boucle jusqu'à avoir tout reçu.
    /// </summary>
    private static void ReadExact(NetworkStream stream, byte[] buf, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = stream.Read(buf, offset, count - offset);
            if (read == 0)
                throw new EndOfStreamException("Video stream closed.");
            offset += read;
        }
    }

    /// <summary>Ferme la connexion TCP.</summary>
    public void Disconnect()
    {
        _stream?.Close();
        _client?.Close();
    }
}
