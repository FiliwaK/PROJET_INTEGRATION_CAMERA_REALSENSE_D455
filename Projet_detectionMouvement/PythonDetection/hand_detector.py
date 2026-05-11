# ============================================================
#  hand_detector.py  —  Détection des mains et du corps
#
#  Rôle : reçoit une image (frame numpy), utilise MediaPipe
#         pour détecter les mains (21 points) et le corps
#         (33 points), retourne un dictionnaire JSON-sérialisable.
#
#  Librairies :
#    - mediapipe  : modèles IA de détection de pose / mains
#    - cv2 (OpenCV) : conversion de couleur BGR → RGB
# ============================================================

import mediapipe as mp   # bibliothèque Google pour la détection de corps / mains
import cv2               # OpenCV — utilisé ici uniquement pour convertir BGR → RGB


class HandDetector:
    """
    Détecte en temps réel :
      - jusqu'à 2 mains (21 points chacune)
      - 1 corps humain complet (33 points)

    Utilise deux modèles MediaPipe distincts :
      - mp.solutions.hands  → spécialisé mains
      - mp.solutions.pose   → squelette du corps entier
    """

    def __init__(self):
        # ── Chargement des modules MediaPipe ────────────────────────────────

        # Accès au sous-module "hands" de MediaPipe
        self.mp_hands = mp.solutions.hands

        # Accès au sous-module "pose" de MediaPipe
        self.mp_pose = mp.solutions.pose

        # Création du détecteur de mains :
        #   max_num_hands=2 → détecte jusqu'à 2 mains simultanément
        #   min_detection_confidence=0.6 → seuil : 60% de certitude minimum
        #                                   pour valider une détection
        self.hands = self.mp_hands.Hands(
            max_num_hands=2,
            min_detection_confidence=0.6
        )

        # Création du détecteur de pose (corps entier) :
        #   min_detection_confidence=0.6 → seuil de détection initiale
        #   min_tracking_confidence=0.5  → seuil pour continuer à suivre
        #                                   un corps déjà détecté (tracking)
        self.pose = self.mp_pose.Pose(
            min_detection_confidence=0.6,
            min_tracking_confidence=0.5
        )

    def detect(self, frame):
        """
        Analyse une image et retourne les positions détectées.

        Paramètre :
          frame  — image numpy au format BGR (venant de la RealSense)

        Retour : dict avec les clés :
          hand_detected  — bool : au moins une main visible ?
          body_detected  — bool : un corps visible ?
          hands          — liste de mains, chaque main = liste de 21 points {x, y}
          body           — liste de 33 points {x, y, visibility}

        Les coordonnées x/y sont normalisées entre 0.0 et 1.0
        (relatif à la taille de l'image → le C# les multiplie par 640/480)
        """

        # MediaPipe attend du RGB, mais la RealSense (et OpenCV) fournit du BGR
        # → conversion obligatoire avant d'envoyer au modèle
        rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)

        # ── Détection des mains ─────────────────────────────────────────────

        # Passe l'image dans le modèle de détection de mains
        # hand_results.multi_hand_landmarks = liste des mains trouvées
        #   (None si aucune main détectée)
        hand_results = self.hands.process(rgb)

        # ── Détection du corps ──────────────────────────────────────────────

        # Passe l'image dans le modèle de pose
        # pose_results.pose_landmarks = les 33 points du corps
        #   (None si aucun corps détecté)
        pose_results = self.pose.process(rgb)

        # ── Construction du résultat mains ──────────────────────────────────

        hands_data = []

        if hand_results.multi_hand_landmarks:
            # Pour chaque main détectée (0, 1 ou 2 mains)
            for hand in hand_results.multi_hand_landmarks:
                # Extrait les 21 points de cette main
                # lm.x et lm.y sont normalisés [0.0 – 1.0]
                points = [{"x": lm.x, "y": lm.y} for lm in hand.landmark]
                hands_data.append(points)

        # ── Construction du résultat corps ──────────────────────────────────

        body_data = []

        if pose_results.pose_landmarks:
            # Extrait les 33 points du squelette (épaules, coudes, hanches, etc.)
            # lm.visibility : score entre 0 et 1 indiquant si le point est
            #   visible ou caché (ex : jambes hors champ)
            for lm in pose_results.pose_landmarks.landmark:
                body_data.append({
                    "x": lm.x,
                    "y": lm.y,
                    "visibility": lm.visibility   # utilisé côté C# pour ne pas
                                                  # dessiner les points cachés
                })

        # ── Retour final ────────────────────────────────────────────────────

        # Ce dict sera sérialisé en JSON par SocketServer et envoyé au C#
        return {
            "hand_detected": len(hands_data) > 0,   # bool pratique pour le statut
            "body_detected": len(body_data) > 0,    # bool pratique pour le statut
            "hands": hands_data,                     # liste des mains
            "body": body_data                        # liste des points du corps
        }
