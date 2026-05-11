# ============================================================
#  main.py  —  Point d'entrée Python
#
#  NOUVEAUTÉ v2 :
#    • VideoServer activé → envoie le flux caméra (JPEG 160×120)
#      vers le C# sur le port 5001.
#    • Le C# affiche ce flux en arrière-plan semi-transparent.
#
#  Flux :
#    RealSense D455 → HandDetector → SocketServer (port 5000)  → données JSON
#                   →              VideoServer  (port 5001)  → flux JPEG
# ============================================================

import numpy as np
import pyrealsense2 as rs
from hand_detector import HandDetector
from socket_server import SocketServer
from video_server  import VideoServer     # ← NOUVEAU

# ── Objets principaux ────────────────────────────────────────────────────────

detector     = HandDetector()
server       = SocketServer()              # port 5000 — données de détection (JSON)
video_server = VideoServer()               # port 5001 — flux caméra (JPEG)

# ── Initialisation RealSense D455 ────────────────────────────────────────────

pipeline = rs.pipeline()
config   = rs.config()
config.enable_stream(rs.stream.color, 640, 480, rs.format.bgr8, 30)
pipeline.start(config)

print("RealSense camera started. Waiting for C# to connect on ports 5000 and 5001...")

# ── Boucle principale ────────────────────────────────────────────────────────

try:
    while True:
        frames      = pipeline.wait_for_frames()
        color_frame = frames.get_color_frame()

        if not color_frame:
            continue

        # Tableau numpy : matrice H×W×3 BGR
        frame = np.asanyarray(color_frame.get_data())

        # 1. Détection des mains → dict JSON
        result = detector.detect(frame)

        # 2. Envoie les données de détection au C# (port 5000)
        server.send(result)

        # 3. Envoie le flux vidéo au C# (port 5001) — NOUVEAU
        #    Le VideoServer redimensionne à 160×120 et compresse en JPEG
        #    → charge réseau minimale, rafraîchissement ≈ 30 fps
        video_server.send_frame(frame)

finally:
    pipeline.stop()
    print("Camera stopped.")