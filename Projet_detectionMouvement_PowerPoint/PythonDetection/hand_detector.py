# ============================================================
#  hand_detector.py  —  Détection et classification des gestes de main
#
#  NOUVEAUTÉ v2 :
#    • pinch_scales[] : distance normalisée pouce↔index pour chaque main
#      → utilisé en C# pour détecter le zoom (deux mains qui s'écartent)
#
#  GESTES RECONNUS :
#  ┌──────────────────┬──────────────────────────────────────────────────┐
#  │ "pointing"       │ Index seul levé       → DESSINE / laser pointer  │
#  │ "two_fingers"    │ Index + Majeur levés  → sélectionne outil        │
#  │ "open"           │ 4 doigts levés        → neutre / quitte PPT      │
#  │ "other"          │ Autre combinaison     → rien                     │
#  └──────────────────┴──────────────────────────────────────────────────┘
#
#  CORRECTION MIROIR : x_retourné = 1.0 - x_mediapipe
# ============================================================

import mediapipe as mp
import cv2
import math


class HandDetector:

    def __init__(self):
        self.mp_hands = mp.solutions.hands
        self.hands = self.mp_hands.Hands(
            max_num_hands=2,
            min_detection_confidence=0.6
        )

    # ── Doigts levés ─────────────────────────────────────────────────────────

    def _fingers_up(self, landmarks):
        """
        [index, majeur, annulaire, auriculaire] : True = levé.
        tip.y < pip.y → bout plus haut que l'articulation → doigt levé.
        """
        pairs = [(8, 6), (12, 10), (16, 14), (20, 18)]
        return [landmarks[tip].y < landmarks[pip].y for tip, pip in pairs]

    # ── Distance pouce↔index (pour le pinch/zoom) ────────────────────────────

    def _thumb_index_distance(self, landmarks):
        """
        Distance normalisée entre le bout du POUCE (landmark 4)
        et le bout de l'INDEX (landmark 8).

        Normalisée par la taille de la main (poignet→MCP majeur) pour être
        indépendante de la distance caméra.

        Valeurs typiques :
          ≈ 0.10 → doigts collés  (pinch fermé)
          ≈ 0.60 → doigts écartés (pinch ouvert)

        En C# on compare la différence entre deux frames successives
        pour détecter un geste de zoom.
        """
        tx, ty = landmarks[4].x, landmarks[4].y   # pouce tip
        ix, iy = landmarks[8].x, landmarks[8].y   # index tip
        tip_dist = math.sqrt((tx - ix) ** 2 + (ty - iy) ** 2)

        # Taille de référence = poignet (0) → MCP du majeur (9)
        wx, wy     = landmarks[0].x, landmarks[0].y
        mcpx, mcpy = landmarks[9].x, landmarks[9].y
        hand_size  = math.sqrt((wx - mcpx) ** 2 + (wy - mcpy) ** 2) + 1e-6

        return tip_dist / hand_size

    # ── Classification du geste ───────────────────────────────────────────────

    def get_gesture(self, landmarks):
        """
        Retourne : "pointing" | "two_fingers" | "open" | "other"
        """
        up = self._fingers_up(landmarks)

        if all(up):
            return "open"

        if up[0] and up[1] and not up[2] and not up[3]:
            return "two_fingers"

        if up[0] and not any(up[1:]):
            return "pointing"

        return "other"

    # ── Détection principale ─────────────────────────────────────────────────

    def detect(self, frame):
        """
        Analyse une image BGR et retourne un dict avec toutes les données de mains.

        Retour :
          hand_detected — bool
          body_detected — toujours False (corps supprimé)
          hands         — liste de mains, chacune = 21 points {x, y} (x miroir)
          body          — toujours []
          gestures      — geste classifié par main
          pinch_scales  — NOUVEAU : distance pouce↔index normalisée par main
                          → utilisé par le C# pour détecter le zoom deux mains
        """
        rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
        hand_results = self.hands.process(rgb)

        hands_data   = []
        gestures     = []
        pinch_scales = []  # ← NOUVEAU : une valeur par main détectée

        if hand_results.multi_hand_landmarks:
            for hand in hand_results.multi_hand_landmarks:

                # Classification sur les coordonnées originales (avant flip miroir)
                gestures.append(self.get_gesture(hand.landmark))

                # Distance pouce↔index (pour le zoom en mode PPT)
                pinch_scales.append(self._thumb_index_distance(hand.landmark))

                # Correction miroir : x retourné pour que la main droite
                # apparaisse à droite de l'écran
                points = [{"x": 1.0 - lm.x, "y": lm.y} for lm in hand.landmark]
                hands_data.append(points)

        return {
            "hand_detected": len(hands_data) > 0,
            "body_detected": False,
            "hands":         hands_data,
            "body":          [],
            "gestures":      gestures,
            "pinch_scales":  pinch_scales,   # ← NOUVEAU
        }