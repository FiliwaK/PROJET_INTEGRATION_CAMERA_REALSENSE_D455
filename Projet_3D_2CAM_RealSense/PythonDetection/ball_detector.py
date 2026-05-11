# ============================================================
#  ball_detector.py  v4 — Détection hybride YOLO + HSV avec 
#  Kinematics (Vélocité) et filtrage anti-occlusion (ROI)
#  + OPTIMISATION ZÉRO-LAG (Tracking-by-Detection)
# ============================================================

import cv2
import numpy as np
import json
import os

try:
    from yolo_ball_detector import YoloBallDetector
    _YOLO_AVAILABLE = True
except ImportError:
    _YOLO_AVAILABLE = False
    print("[BallDetector] onnxruntime absent — mode HSV seul")

CORNERS_JSON = "terrain_corners.json"

# ── HSV — balle pickleball jaune-vert fluo ────────────────────────────────────
BALL_LOWER = np.array([25, 120, 120], dtype=np.uint8)
BALL_UPPER = np.array([60, 255, 255], dtype=np.uint8)

BORDER_ERODE_PX  = 8
DETECT_MARGIN_PX = 25
MIN_RADIUS = 9
MAX_RADIUS = 60

# ── Lissage EMA et Cinématique (OPTIMISÉ ZÉRO LAG) ────────────────────────────
EMA_ALPHA      = 0.85   # Lissage très réactif (Au lieu de 0.55)
VELOCITY_ALPHA = 0.50   # Suit les changements de direction secs (Au lieu de 0.30)


class BallDetector:

    def __init__(self):
        self.terrain_pixels = None
        self.net_pixels     = None
        self._yolo: "YoloBallDetector | None" = None

        # ── État de suivi Cinématique ──────────────────────────────────────
        self._tracked_u      = None
        self._tracked_v      = None
        self._tracked_radius = None
        
        self._vx             = 0.0  
        self._vy             = 0.0  
        
        self._lost_frames    = 0
        self._MAX_COAST      = 1    # Prédit 1 seule frame max pour éviter de partir dans le vide
        self._MAX_LOST       = 4    # Reset rapide si balle vraiment perdue

        self._load_terrain()
        self._load_yolo()

    # ── Chargement ────────────────────────────────────────────────────────────

    def _load_terrain(self):
        paths =[
            CORNERS_JSON,
            os.path.join(os.path.dirname(__file__), CORNERS_JSON),
            r"C:\Users\533\Desktop\Projet_3D\PythonDetection\terrain_corners.json",
        ]
        for p in paths:
            if os.path.exists(p):
                try:
                    with open(p) as f:
                        data = json.load(f)
                    if "pixels" in data and len(data["pixels"]) == 4:
                        self.terrain_pixels = np.array(data["pixels"], dtype=np.int32)
                        if "net_pixels" in data and len(data["net_pixels"]) == 2:
                            self.net_pixels = [
                                tuple(data["net_pixels"][0]),
                                tuple(data["net_pixels"][1]),
                            ]
                        print(f"[BallDetector] Terrain chargé : {p}")
                        return
                except Exception as e:
                    print(f"[BallDetector] Erreur terrain : {e}")
        print("[BallDetector] ⚠ terrain_corners.json introuvable — détection plein cadre")

    def _load_yolo(self):
        if not _YOLO_AVAILABLE:
            return
        try:
            self._yolo = YoloBallDetector()
            print("[BallDetector] Mode YOLO actif (+ fallback HSV)")
        except Exception as e:
            print(f"[BallDetector] Erreur YOLO : {e} — mode HSV seul")

    def _is_in_terrain(self, cx: int, cy: int) -> bool:
        if self.terrain_pixels is None:
            return True
        result = cv2.pointPolygonTest(
            self.terrain_pixels.astype(np.float32),
            (float(cx), float(cy)), False)
        return result >= 0

    @staticmethod
    def _valid_radius(r: int) -> bool:
        return MIN_RADIUS <= r <= MAX_RADIUS

    # ── Détection principale (avec prédiction) ────────────────────────────────

    def detect(self, color_bgr: np.ndarray) -> dict:
        pred_u, pred_v = None, None
        search_radius  = None
        
        if self._tracked_u is not None:
            pred_u = int(self._tracked_u + self._vx)
            pred_v = int(self._tracked_v + self._vy)
            speed = np.hypot(self._vx, self._vy)
            search_radius = max(80, int(speed * 1.5 + 40))

        raw = self._detect_raw(color_bgr, pred_u, pred_v, search_radius)

        if raw["found"]:
            u, v, r = raw["u"], raw["v"], raw["radius"]
            
            if self._tracked_u is None:
                self._tracked_u, self._tracked_v, self._tracked_radius = float(u), float(v), float(r)
                self._vx, self._vy = 0.0, 0.0
            else:
                raw_vx = float(u) - self._tracked_u
                raw_vy = float(v) - self._tracked_v
                self._vx = (self._vx * (1 - VELOCITY_ALPHA)) + (raw_vx * VELOCITY_ALPHA)
                self._vy = (self._vy * (1 - VELOCITY_ALPHA)) + (raw_vy * VELOCITY_ALPHA)
                
                self._tracked_u = (EMA_ALPHA * u) + ((1 - EMA_ALPHA) * self._tracked_u)
                self._tracked_v = (EMA_ALPHA * v) + ((1 - EMA_ALPHA) * self._tracked_v)
                self._tracked_radius = (EMA_ALPHA * r) + ((1 - EMA_ALPHA) * self._tracked_radius)

            self._lost_frames = 0
            is_in = self._is_in_terrain(int(self._tracked_u), int(self._tracked_v))
            
            return {
                "found":  True,
                "u":      int(self._tracked_u),
                "v":      int(self._tracked_v),
                "radius": int(self._tracked_radius),
                "is_in":  is_in,
                "mode":   raw["mode"],
            }
        else:
            self._lost_frames += 1
            if self._lost_frames <= self._MAX_COAST and pred_u is not None:
                self._tracked_u = float(pred_u)
                self._tracked_v = float(pred_v)
                return {
                    "found":  True,
                    "u":      pred_u,
                    "v":      pred_v,
                    "radius": int(self._tracked_radius),
                    "is_in":  self._is_in_terrain(pred_u, pred_v),
                    "mode":   "predict"
                }
            
            if self._lost_frames >= self._MAX_LOST:
                self._tracked_u = self._tracked_v = self._tracked_radius = None
                self._vx = self._vy = 0.0

            return {"found": False, "u": 0, "v": 0, "radius": 0, "is_in": True, "mode": "none"}

    def _detect_raw(self, color_bgr: np.ndarray, pred_u, pred_v, search_radius) -> dict:
        """Détection brute ZÉRO LAG : on teste HSV dans la ROI en priorité absolue (~5ms)"""
        
        # 1. Tracker ultrarapide HSV si on a une position prévue
        if pred_u is not None:
            res_hsv = self._detect_hsv(color_bgr, pred_u, pred_v, search_radius)
            if res_hsv["found"]:
                return res_hsv

        # 2. Si balle perdue, YOLO prend le relais (~50ms)
        if self._yolo is not None:
            roi_c = (pred_u, pred_v) if pred_u is not None else None
            det = self._yolo.detect(color_bgr, roi_center=roi_c, roi_radius=search_radius)
            if det is not None and self._valid_radius(det["radius"]):
                return {
                    "found":  True,
                    "u":      det["u"],
                    "v":      det["v"],
                    "radius": det["radius"],
                    "mode":   "yolo",
                }

        # 3. Fallback HSV complet
        return self._detect_hsv(color_bgr, None, None, None)

    def _detect_hsv(self, color_bgr: np.ndarray, pred_u, pred_v, search_radius) -> dict:
        blurred = cv2.GaussianBlur(color_bgr, (5, 5), 0) # Blur réduit pour les FPS
        hsv     = cv2.cvtColor(blurred, cv2.COLOR_BGR2HSV)
        h, w    = color_bgr.shape[:2]

        ball_mask = cv2.inRange(hsv, BALL_LOWER, BALL_UPPER)

        if pred_u is not None and search_radius is not None:
            roi_mask = np.zeros((h, w), dtype=np.uint8)
            cv2.circle(roi_mask, (pred_u, pred_v), search_radius, 255, -1)
            ball_mask = cv2.bitwise_and(ball_mask, roi_mask)

        if self.terrain_pixels is not None:
            interior = np.zeros((h, w), dtype=np.uint8)
            cv2.fillPoly(interior, [self.terrain_pixels], 255)

            k_expand = cv2.getStructuringElement(
                cv2.MORPH_ELLIPSE,
                (DETECT_MARGIN_PX * 2 + 1, DETECT_MARGIN_PX * 2 + 1))
            detect_zone = cv2.dilate(interior, k_expand, iterations=1)

            k_erode = cv2.getStructuringElement(
                cv2.MORPH_ELLIPSE,
                (BORDER_ERODE_PX * 2 + 1, BORDER_ERODE_PX * 2 + 1))
            interior_eroded = cv2.erode(interior, k_erode, iterations=1)

            outside    = cv2.bitwise_and(detect_zone, cv2.bitwise_not(interior))
            valid_zone = cv2.bitwise_or(interior_eroded, outside)
            ball_mask  = cv2.bitwise_and(ball_mask, valid_zone)

        ball_mask = cv2.erode(ball_mask,  None, iterations=1)
        ball_mask = cv2.dilate(ball_mask, None, iterations=2)

        contours, _ = cv2.findContours(
            ball_mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)

        best = None
        best_score = 0
        for c in contours:
            area = cv2.contourArea(c)
            if area < 80: continue
            ((cx, cy), radius) = cv2.minEnclosingCircle(c)
            if radius < MIN_RADIUS or radius > MAX_RADIUS: continue
            peri = cv2.arcLength(c, True)
            circ = (4 * np.pi * area / (peri * peri)) if peri > 0 else 0
            if circ < 0.70: continue
            
            score = circ * area
            if score > best_score:
                best_score = score
                best = (int(cx), int(cy), int(radius))

        if best is None:
            return {"found": False, "u": 0, "v": 0, "radius": 0, "is_in": True, "mode": "hsv"}

        cx, cy, r = best
        return {
            "found":  True,
            "u":      cx,
            "v":      cy,
            "radius": r,
            "is_in":  self._is_in_terrain(cx, cy),
            "mode":   "hsv",
        }

    # ── Debug overlay (INTOUCHÉ, exactement comme ton fichier) ─────────────────
    def draw_debug(self, color_bgr: np.ndarray, result: dict) -> np.ndarray:
        img = color_bgr.copy()
        h, w = img.shape[:2]

        if self.terrain_pixels is not None:
            k = cv2.getStructuringElement(
                cv2.MORPH_ELLIPSE,
                (DETECT_MARGIN_PX * 2 + 1, DETECT_MARGIN_PX * 2 + 1))
            interior = np.zeros((h, w), dtype=np.uint8)
            cv2.fillPoly(interior, [self.terrain_pixels], 255)
            detect_zone = cv2.dilate(interior, k, iterations=1)
            ext_cnts, _ = cv2.findContours(
                detect_zone, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
            cv2.drawContours(img, ext_cnts, -1, (0, 140, 255), 1)
            cv2.polylines(img, [self.terrain_pixels],
                          isClosed=True, color=(0, 255, 0), thickness=2)

        if self._tracked_u is not None:
            speed = np.hypot(self._vx, self._vy)
            sr = max(80, int(speed * 1.5 + 40))
            pu, pv = int(self._tracked_u + self._vx), int(self._tracked_v + self._vy)
            cv2.circle(img, (pu, pv), sr, (255, 255, 255), 1, lineType=cv2.LINE_AA)
            cv2.putText(img, "ROI", (pu + sr + 5, pv), cv2.FONT_HERSHEY_SIMPLEX, 0.4, (255, 255, 255), 1)

        if result["found"]:
            is_in = result.get("is_in", True)
            mode  = result.get("mode", "?")
            
            color = (0, 255, 0) if is_in else (0, 60, 255)
            if mode == "predict":
                color = (255, 255, 0) 
                
            label = f"{'IN' if is_in else 'OUT'}  r={result['radius']}  [{mode}]"
            cv2.circle(img, (result["u"], result["v"]), result["radius"], color, 2)
            cv2.circle(img, (result["u"], result["v"]), 4, color, -1)

            cv2.drawMarker(img, (result["u"], result["v"]),
                           color, cv2.MARKER_CROSS, 10, 1)

            cv2.putText(img, label,
                        (result["u"] - 50, result["v"] - result["radius"] - 10),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.6, color, 2)
        else:
            cv2.putText(img, "Aucune balle",
                        (20, 30), cv2.FONT_HERSHEY_SIMPLEX, 0.7, (0, 0, 255), 2)

        if self._tracked_u is not None and result["found"] and mode != "predict":
            cv2.circle(img,
                       (int(self._tracked_u), int(self._tracked_v)),
                       int(self._tracked_radius) + 4,
                       (255, 200, 0), 1)

        cv2.putText(img, "Vert=terrain | Orange=zone OUT | Q=quitter",
                    (10, img.shape[0] - 10),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.4, (200, 200, 200), 1)
        return img