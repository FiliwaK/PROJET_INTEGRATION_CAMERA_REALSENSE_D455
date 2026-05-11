# ============================================================
#  main.py  —  Point d'entrée du programme Python
#
#  Rôle : capture les images de la caméra Intel RealSense D455,
#         les envoie au détecteur (MediaPipe), puis transmet
#         les résultats à l'application C# via une socket TCP.
#
#  Flux : RealSense D455 → HandDetector → SocketServer → C# WPF
# ============================================================

import numpy as np           # manipulation des tableaux d'images
import pyrealsense2 as rs    # SDK Intel RealSense pour accéder à la caméra D455
from hand_detector import HandDetector   # notre classe de détection des mains
from socket_server import SocketServer   # notre serveur TCP qui envoie les données au C#

# ── Création des objets principaux ──────────────────────────────────────────

# HandDetector : initialise MediaPipe Hands pour détecter les gestes
detector = HandDetector()

# SocketServer : ouvre le port TCP 5000 et attend la connexion de l'app C#
# (bloquant ici : le programme attend que le C# se connecte avant de continuer)
server = SocketServer()

# ── Initialisation de la caméra RealSense D455 ──────────────────────────────

# Pipeline : objet principal du SDK RealSense, gère le flux de la caméra
pipeline = rs.pipeline()

# Config : configure ce qu'on veut recevoir de la caméra
config = rs.config()

# On active uniquement le flux couleur (RGB) :
#   - résolution 640×480 pixels
#   - format BGR (compatible OpenCV / numpy)
#   - 30 images par seconde
config.enable_stream(rs.stream.color, 640, 480, rs.format.bgr8, 30)

# Démarre la caméra avec cette configuration
pipeline.start(config)

print("RealSense camera started.")

# ── Boucle principale de capture + détection ────────────────────────────────

try:
    while True:
        # Attend la prochaine trame disponible depuis la caméra
        # (bloquant jusqu'à ce qu'une nouvelle image soit prête)
        frames = pipeline.wait_for_frames()

        # Extrait uniquement le flux couleur (on ignore la profondeur ici)
        color_frame = frames.get_color_frame()

        # Si la trame est invalide (caméra pas encore prête), on saute
        if not color_frame:
            continue

        # Convertit la trame RealSense en tableau numpy (matrice H×W×3 de pixels)
        # → format utilisable par OpenCV et MediaPipe
        frame = np.asanyarray(color_frame.get_data())

        # Envoie l'image au détecteur → retourne un dict avec les positions
        # des mains détectées et les gestes classifiés
        result = detector.detect(frame)

        # Envoie le résultat au C# via la socket TCP (format JSON + "\n")
        server.send(result)

finally:
    # Quand on quitte (Ctrl+C ou erreur), on arrête proprement la caméra
    pipeline.stop()
    print("Camera stopped.")
