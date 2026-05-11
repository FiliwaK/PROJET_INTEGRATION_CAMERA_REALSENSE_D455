// ============================================================
//  SocketClient.cs  —  Client TCP côté C#
//
//  Rôle : se connecte au serveur TCP Python (127.0.0.1:5000)
//         et lit les messages JSON envoyés frame par frame.
//
//  Pourquoi StreamReader.ReadLine() et pas stream.Read() ?
//  ─────────────────────────────────────────────────────────
//  TCP est un flux d'octets continu, sans notion de "message".
//  Si Python envoie 500 octets, le C# peut recevoir 200 octets
//  dans un premier appel et 300 dans un second — les messages
//  peuvent être fragmentés ou regroupés.
//
//  Solution : Python termine chaque message par "\n".
//  StreamReader.ReadLine() lit exactement jusqu'au "\n"
//  → un appel = un message JSON complet, toujours.
//
//  Ancienne version (bug) : stream.Read() lisait des octets bruts
//  → JSON tronqué → crash à la désérialisation.
// ============================================================

using System.IO;
using System.Net.Sockets;
using System.Text;

public class SocketClient
{
    // TcpClient : représente la connexion TCP avec le serveur Python
    private TcpClient? client;

    // StreamReader : lit le flux TCP ligne par ligne (délimiteur "\n")
    // UTF-8 : encodage utilisé des deux côtés (Python encode('utf-8'))
    private StreamReader? reader;

    /// <summary>
    /// Établit la connexion TCP avec le serveur Python.
    /// Doit être appelé APRÈS que Python ait démarré son serveur,
    /// sinon une exception "Connection refused" est levée.
    /// </summary>
    public void Connect()
    {
        // Se connecte à l'adresse 127.0.0.1 (localhost) sur le port 5000
        // → même machine, même port que socket_server.py
        client = new TcpClient("127.0.0.1", 5000);

        // Crée un StreamReader sur le flux réseau pour lire ligne par ligne
        // leaveOpen: false → ferme le stream quand le reader est fermé
        reader = new StreamReader(client.GetStream(), Encoding.UTF8);
    }

    /// <summary>
    /// Lit et retourne le prochain message JSON complet.
    ///
    /// Bloquant : attend jusqu'à ce qu'une ligne complète ("\n") arrive.
    /// Retourne string.Empty si la connexion est fermée côté Python.
    ///
    /// Un message = une ligne = une trame de détection complète.
    /// </summary>
    public string Receive()
    {
        // ReadLine() lit jusqu'au "\n" et le retire du résultat
        // Retourne null si la connexion est fermée → ?? remplace par string.Empty
        return reader?.ReadLine() ?? string.Empty;
    }

    /// <summary>
    /// Ferme proprement la connexion TCP.
    /// Appelé par MainViewModel.Stop() quand la fenêtre se ferme.
    /// </summary>
    public void Disconnect()
    {
        reader?.Close();  // ferme le StreamReader et le flux réseau sous-jacent
        client?.Close();  // libère la connexion TCP
    }
}
