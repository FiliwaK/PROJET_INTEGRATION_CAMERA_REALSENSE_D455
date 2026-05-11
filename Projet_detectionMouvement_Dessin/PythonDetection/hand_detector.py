# ============================================================
#  hand_detector.py  —  Détection et classification des gestes de main
#
#  Utilise MediaPipe Hands pour détecter jusqu'à 2 mains dans une image
#  et retourner les 21 points (landmarks) de chaque main ainsi qu'un
#  geste classifié.
#
#  GESTES RECONNUS :
#  ┌──────────────────┬─────────────────────────────────────────────────┐
#  │ "pointing"       │ Index seul levé          → DESSINE              │
#  │ "two_fingers"    │ Index + Majeur levés      → SÉLECTIONNE outil   │
#  │                  │                             (dwell 1.5s)        │
#  │ "open"           │ 4 doigts levés           → neutre / sauvegarde  │
#  │ "fist"           │ Aucun doigt levé         → DÉPLACE dessin (3s)  │
#  │ "other"          │ Autre combinaison         → rien                │
#  └──────────────────┴─────────────────────────────────────────────────┘
#
#  CORRECTION MIROIR :
#    x_retourné = 1.0 - x_mediapipe
#    → ta main droite = côté droit de l'écran ✓
# ============================================================

import mediapipe as mp
import cv2
import math  # utilisé pour calculer la distance entre les bouts de doigts (pinch)


class HandDetector:

    def __init__(self):
        self.mp_hands = mp.solutions.hands

        # Initialise le détecteur MediaPipe Hands
        # max_num_hands=2 : on détecte au maximum 2 mains simultanément
        # min_detection_confidence=0.6 : seuil de confiance pour la détection initiale
        self.hands = self.mp_hands.Hands(
            max_num_hands=2,
            min_detection_confidence=0.6
        )

    # ── Classification "doigts levés" ────────────────────────────────────────

    def _fingers_up(self, landmarks):
        """
        Retourne une liste de 4 booléens indiquant si chaque doigt est levé.
        Ordre : [index, majeur, annulaire, auriculaire] (le pouce n'est pas inclus).

        Comment ça marche :
          Chaque doigt a un "tip" (bout, ex: landmark 8 pour l'index)
          et un "pip" (articulation intermédiaire, ex: landmark 6).
          En MediaPipe, y=0 est en HAUT de l'image.
          Donc : tip.y < pip.y signifie que le bout est PLUS HAUT → doigt levé.
        """
        # Paires (tip, pip) pour chaque doigt
        pairs = [(8, 6), (12, 10), (16, 14), (20, 18)]
        return [landmarks[tip].y < landmarks[pip].y for tip, pip in pairs]

    # ── Calcul du ratio de distance pour le geste "pinch" ────────────────────

    def _is_fist(self, landmarks):
        """
        Détecte un poing fermé en vérifiant que tous les bouts de doigts
        sont en dessous de leurs articulations MCP (base de chaque doigt).
        Plus fiable que la comparaison tip/pip car le MCP est plus stable.
        """
        # (tip, MCP) pour index, majeur, annulaire, auriculaire
        mcp_pairs = [(8, 5), (12, 9), (16, 13), (20, 17)]
        return all(landmarks[tip].y > landmarks[mcp].y for tip, mcp in mcp_pairs)

    def _pinch_ratio(self, landmarks):
        """
        Calcule la distance entre le bout de l'index (landmark 8) et le bout
        du majeur (landmark 12), normalisée par la taille de la main.

        Pourquoi normaliser ?
          Si la main est loin de la caméra, les coordonnées normalisées
          sont compressées. Sans normalisation, le seuil serait faux
          selon la distance. On divise par la taille du poignet→MCP (landmarks 0→9).

        Valeur typique :
          < 0.35 → doigts collés  → "pinch"
          > 0.35 → doigts écartés → "two_fingers"
        """
        # Bout de l'index (8) et bout du majeur (12)
        ix, iy = landmarks[8].x,  landmarks[8].y
        mx, my = landmarks[12].x, landmarks[12].y
        tip_dist = math.sqrt((ix - mx)**2 + (iy - my)**2)

        # Taille de la main = distance poignet (0) → MCP du majeur (9)
        wx, wy     = landmarks[0].x, landmarks[0].y
        mcpx, mcpy = landmarks[9].x, landmarks[9].y
        hand_size  = math.sqrt((wx - mcpx)**2 + (wy - mcpy)**2) + 1e-6  # +1e-6 pour éviter division par zéro

        return tip_dist / hand_size

    # ── Classification principale du geste ───────────────────────────────────

    def get_gesture(self, landmarks):
        """
        Classifie le geste de la main en analysant les doigts levés.

        Retourne une chaîne parmi :
          "pointing"    → index seul levé                        → dessiner
          "two_fingers" → index + majeur levés, autres repliés   → sélectionner outil (dwell 1.5s)
          "open"        → 4 doigts levés                         → mode neutre / sauvegarde si ×2 (4s)
          "fist"        → aucun doigt levé                       → déplacer dessin (maintien 3s)
          "other"       → toute autre combinaison                → rien
        """
        up = self._fingers_up(landmarks)
        # up = [index, majeur, annulaire, auriculaire]

        if all(up):
            # Les 4 doigts sont levés → main ouverte
            return "open"

        if up[0] and up[1] and not up[2] and not up[3]:
            # Index ET majeur levés, annulaire et auriculaire repliés
            # → geste "V" ou "paix" → sélection d'outil
            return "two_fingers"

        if up[0] and not any(up[1:]):
            # Seulement l'index levé → dessin
            return "pointing"

        if not any(up) or self._is_fist(landmarks):
            # Poing fermé : aucun doigt levé (check pip) OU tous les bouts sous les MCPs
            # → double vérification pour une détection plus robuste
            return "fist"

        # Pouce seul ou toute autre combinaison → rien
        return "other"

    # ── Détection principale : analyse une frame et retourne les données ─────

    def detect(self, frame):
        """
        Analyse une image BGR et retourne un dict avec les données des mains.

        Paramètre :
          frame — image numpy BGR (issue de la caméra RealSense)

        Retour (dict) :
          hand_detected — bool : au moins une main visible
          body_detected — toujours False (le corps n'est plus détecté)
          hands         — liste de mains, chacune = 21 points {x, y}
                          x est inversé (miroir) pour que la main droite
                          apparaisse à droite de l'écran
          body          — toujours [] (corps supprimé → gain CPU)
          gestures      — liste de gestes classifiés, même ordre que hands
        """
        # MediaPipe travaille en RGB, mais OpenCV/RealSense donne du BGR → conversion
        rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)

        # Analyse l'image : retourne les landmarks de chaque main détectée
        hand_results = self.hands.process(rgb)

        hands_data = []  # contiendra les 21 points de chaque main
        gestures   = []  # contiendra le geste classifié de chaque main

        if hand_results.multi_hand_landmarks:
            for hand in hand_results.multi_hand_landmarks:
                # Classifie le geste sur les coordonnées ORIGINALES (avant flip miroir)
                # Important : le flip ne doit pas affecter la détection des doigts levés
                gestures.append(self.get_gesture(hand.landmark))

                # Retourne les coordonnées x pour corriger le miroir caméra
                # Sans cette correction : lever la main droite → curseur à gauche (contre-intuitif)
                # Avec : lever la main droite → curseur à droite ✓
                points = [{"x": 1.0 - lm.x, "y": lm.y} for lm in hand.landmark]
                hands_data.append(points)

        return {
            "hand_detected": len(hands_data) > 0,
            "body_detected": False,   # corps supprimé → toujours False
            "hands":    hands_data,
            "body":     [],           # tableau vide (le C# l'ignorera)
            "gestures": gestures
        }
