using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Media.Imaging;

public class VideoClient
{
    private TcpClient? _client;
    private NetworkStream? _stream;

    public event Action<BitmapImage>? FrameReceived;

    public void Connect(string host = "127.0.0.1", int port = 5001)
    {
        Disconnect(); // Nettoie toute ancienne connexion
        _client = new TcpClient();
        _client.Connect(host, port);
        _stream = _client.GetStream();
    }

    // NOUVEAU : Boucle bloquante qui lit les frames tant que la connexion est active
    public void ReceiveLoop(CancellationToken token)
    {
        var lenBuf = new byte[4];

        while (!token.IsCancellationRequested)
        {
            // Lit exactement 4 octets pour la taille
            ReadExact(_stream!, lenBuf, 4);

            int frameLen = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];

            // Limite de sécurité augmentée à 5 Mo pour la HD
            if (frameLen <= 0 || frameLen > 5_000_000)
                throw new Exception("Taille de frame invalide, reconnexion nécessaire.");

            var jpegData = new byte[frameLen];
            ReadExact(_stream!, jpegData, frameLen);

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new MemoryStream(jpegData);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze(); // Permet l'utilisation sur le thread principal UI

            FrameReceived?.Invoke(bmp);
        }
    }

    private static void ReadExact(NetworkStream stream, byte[] buf, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = stream.Read(buf, offset, count - offset);
            if (read == 0)
                throw new EndOfStreamException("Flux vidéo fermé par le serveur.");
            offset += read;
        }
    }

    public void Disconnect()
    {
        _stream?.Close();
        _client?.Close();
    }
}