# ============================================================
#  video_server.py  —  Serveur TCP de flux vidéo (JPEG)
#
#  Rôle : envoie les frames de la caméra (redimensionnées et
#         compressées en JPEG) vers l'application C# pour
#         afficher l'aperçu caméra en temps réel.
#
#  Protocole :
#    Chaque frame = 4 octets (int big-endian) = taille JPEG
#                 + N octets = données JPEG
#
#  Port : 5001 (séparé du flux JSON sur 5001)
#
#  L'accept() tourne dans un thread daemon pour ne pas
#  bloquer le démarrage si le C# n'est pas encore connecté.
# ============================================================

import socket
import struct
import threading
import cv2


class VideoServer:

    PREVIEW_W = 640     # <-- Change 160 par 640
    PREVIEW_H = 480     # <-- Change 120 par 480
    JPEG_QUALITY = 80   # <-- Change 55 par 80 (qualité claire)  # compression agressive pour limiter la bande passante

    def __init__(self, host='127.0.0.1', port=5001):
        self.conn = None
        self._lock = threading.Lock()

        self.server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.server.bind((host, port))
        self.server.listen(1)

        # Accept en arrière-plan pour ne pas bloquer main.py
        threading.Thread(target=self._accept_loop, daemon=True).start()
        print(f"Video server listening on {host}:{port}...")

    def _accept_loop(self):
        """Attend en boucle les connexions du client C#."""
        while True:
            try:
                conn, addr = self.server.accept()
                print(f"Video client connected from {addr}")
                with self._lock:
                    self.conn = conn
            except Exception:
                break

    def send_frame(self, frame):
        """
        Redimensionne la frame, la retourne en miroir, l'encode en JPEG
        et l'envoie au client C# avec un en-tête de 4 octets (taille).

        Paramètre :
          frame — image numpy BGR (telle que reçue de RealSense)
        """
        with self._lock:
            if self.conn is None:
                return

        # Redimensionnement pour le petit aperçu
        small = cv2.resize(frame, (self.PREVIEW_W, self.PREVIEW_H))

        # Retournement miroir horizontal (cohérent avec la correction côté Python)
        small = cv2.flip(small, 1)

        # Encodage JPEG avec compression forte (aperçu, pas besoin de qualité max)
        ok, buf = cv2.imencode('.jpg', small,
                               [cv2.IMWRITE_JPEG_QUALITY, self.JPEG_QUALITY])
        if not ok:
            return

        data = buf.tobytes()

        # En-tête : taille sur 4 octets big-endian
        header = struct.pack('>I', len(data))

        try:
            with self._lock:
                if self.conn is not None:
                    self.conn.sendall(header + data)
        except (BrokenPipeError, ConnectionResetError, OSError):
            # Le C# s'est déconnecté → on remet conn à None et on attend reconnetion
            with self._lock:
                self.conn = None
            print("Video client disconnected, waiting for reconnection...")
