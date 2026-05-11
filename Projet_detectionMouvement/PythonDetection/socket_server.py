# ============================================================
#  socket_server.py  —  Serveur TCP côté Python
#
#  Rôle : ouvre un port TCP, attend que l'app C# se connecte,
#         puis lui envoie les résultats de détection frame
#         par frame, au format JSON.
#
#  Pourquoi TCP et pas HTTP / WebSocket ?
#    → TCP brut est le plus simple et le plus rapide pour
#      envoyer des données localement entre deux processus
#      sur la même machine (latence quasi nulle).
#
#  Protocole de message :
#    Chaque message = 1 ligne JSON terminée par "\n"
#    Ex : {"hand_detected": true, "hands": [...], ...}\n
#
#    IMPORTANT : le délimiteur "\n" est indispensable.
#    TCP est un flux d'octets continu — sans délimiteur,
#    le C# ne saurait pas où s'arrête un message et où
#    commence le suivant (risque de messages tronqués).
#    Côté C#, StreamReader.ReadLine() lit exactement
#    jusqu'au "\n" → un appel = un message complet.
# ============================================================

import socket   # module Python standard pour les sockets réseau
import json     # sérialisation Python dict → chaîne JSON


class SocketServer:
    """
    Serveur TCP qui attend une connexion de l'app C# WPF
    et lui envoie les données de détection en temps réel.
    """

    def __init__(self, host='127.0.0.1', port=5000):
        """
        Paramètres :
          host — adresse d'écoute (127.0.0.1 = local uniquement,
                 l'app C# tourne sur la même machine)
          port — numéro de port (doit correspondre côté C#)
        """

        self.host = host
        self.port = port

        # ── Création de la socket serveur ────────────────────────────────────

        # AF_INET   = protocole IPv4
        # SOCK_STREAM = TCP (flux fiable, ordonné, sans perte)
        self.server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)

        # SO_REUSEADDR : permet de relancer le serveur immédiatement
        # après un arrêt sans attendre l'expiration du délai TIME_WAIT
        # (évite "Address already in use" si on relance vite)
        self.server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)

        # Associe la socket à l'adresse et au port
        self.server.bind((host, port))

        # Met la socket en mode écoute (1 = file d'attente de 1 connexion)
        self.server.listen(1)

        print(f"Python server started on {host}:{port}, waiting for C# client...")

        # Bloquant : attend qu'un client (l'app C#) se connecte
        # conn   = socket de communication avec CE client
        # addr   = (ip, port) du client qui s'est connecté
        self.conn, addr = self.server.accept()

        print(f"C# client connected from {addr}")

    def send(self, data):
        """
        Sérialise le dictionnaire 'data' en JSON et l'envoie au C#.

        Paramètre :
          data — dict Python (retourné par HandDetector.detect)

        Le "\n" final est crucial : c'est le délimiteur que
        StreamReader.ReadLine() utilise côté C# pour savoir
        qu'un message complet est arrivé.
        """

        # Sérialise le dict Python en chaîne JSON + ajoute le délimiteur de ligne
        message = json.dumps(data) + "\n"

        try:
            # sendall() garantit que tous les octets sont envoyés
            # (contrairement à send() qui peut envoyer partiellement)
            self.conn.sendall(message.encode('utf-8'))

        except (BrokenPipeError, ConnectionResetError):
            # Le C# s'est déconnecté (fermeture de la fenêtre, crash, etc.)
            # On attend une nouvelle connexion au lieu de planter
            print("C# client disconnected. Waiting for reconnection...")
            self.conn, addr = self.server.accept()
            print(f"Reconnected from {addr}")
