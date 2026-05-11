# ============================================================
#  yolo_ball_detector.py  v4 - Ajout du filtrage par ROI (Prédiction)
# ============================================================

import os
import numpy as np
import cv2
import onnxruntime as ort

MODEL_PATHS =[
    "ball_detect.onnx",
    os.path.join(os.path.dirname(__file__), "ball_detect.onnx"),
    r"C:\Users\533\Desktop\Projet_3D\PythonDetection\ball_detect.onnx",
]

MIN_RADIUS_PX    = 6
MAX_RADIUS_PX    = 60
MAX_ASPECT_RATIO = 2.0


class YoloBallDetector:
    IMG_SIZE    = 640
    CONF_THRESH = 0.35  # Légèrement baissé pour tolérer le flou de mouvement
    NMS_IOU     = 0.45

    def __init__(self, model_path=None):
        path = model_path or self._find_model()
        if path is None:
            raise FileNotFoundError("ball_detect.onnx introuvable.")

        self._model_path = path
        self._dml_errors = 0
        self._MAX_DML_ERRORS = 3

        # On demande le CPUProvider ici
        self._session    = self._create_session(["CPUExecutionProvider"])
        self._input_name = self._session.get_inputs()[0].name
        active = self._session.get_providers()[0]
        print(f"[YoloBallDetector] Charge : {os.path.basename(path)}  ({active})")

    # ------------------------------------------------------------------ session

    def _create_session(self, providers):
        # On force l'utilisation du CPU pour éviter les crashs de DirectML
        return ort.InferenceSession(self._model_path, providers=["CPUExecutionProvider"])

    def _switch_to_cpu(self):
        print("[YoloBallDetector] Bascule vers CPU (trop d erreurs DML)...")
        try:
            self._session    = self._create_session(["CPUExecutionProvider"])
            self._input_name = self._session.get_inputs()[0].name
            self._dml_errors = 0
            print("[YoloBallDetector] CPU OK")
        except Exception as e:
            print(f"[YoloBallDetector] Erreur bascule CPU : {e}")

    # ---------------------------------------------------------------- detection

    def detect(self, img_bgr, roi_center=None, roi_radius=None):
        """
        Retourne {"u","v","radius","conf"} ou None.
        Ne leve jamais d exception.
        roi_center: tuple (u, v) de la position prédite
        roi_radius: float rayon de recherche max autour de la prédiction
        """
        try:
            return self._detect_internal(img_bgr, roi_center, roi_radius)
        except (UnicodeDecodeError, UnicodeEncodeError):
            # Bug DML Windows FR : message erreur CP1252 decode en UTF-8 -> crash
            self._dml_errors += 1
            if self._dml_errors >= self._MAX_DML_ERRORS:
                self._switch_to_cpu()
            return None
        except Exception:
            self._dml_errors += 1
            if self._dml_errors >= self._MAX_DML_ERRORS:
                self._switch_to_cpu()
            return None

    def _detect_internal(self, img_bgr, roi_center, roi_radius):
        h, w = img_bgr.shape[:2]

        resized = cv2.resize(img_bgr, (self.IMG_SIZE, self.IMG_SIZE))
        rgb     = cv2.cvtColor(resized, cv2.COLOR_BGR2RGB).astype(np.float32) / 255.0
        tensor  = rgb.transpose(2, 0, 1)[np.newaxis]

        raw   = self._session.run(None, {self._input_name: tensor})[0]
        confs = raw[0, 4, :]
        valid_mask = confs >= self.CONF_THRESH
        if not np.any(valid_mask):
            return None

        idxs      = np.where(valid_mask)[0]
        confs_val = confs[idxs]
        cxs       = raw[0, 0, idxs]
        cys       = raw[0, 1, idxs]
        bws       = raw[0, 2, idxs]
        bhs       = raw[0, 3, idxs]

        sx = w / self.IMG_SIZE;  sy = h / self.IMG_SIZE
        cxs_real = cxs * sx;    cys_real = cys * sy
        bws_real = bws * sx;    bhs_real = bhs * sy

        radii     = np.maximum(bws_real, bhs_real) / 2.0
        size_mask = (radii >= MIN_RADIUS_PX) & (radii <= MAX_RADIUS_PX)
        if not np.any(size_mask): return None

        cxs_real  = cxs_real[size_mask];  cys_real  = cys_real[size_mask]
        bws_real  = bws_real[size_mask];  bhs_real  = bhs_real[size_mask]
        radii     = radii[size_mask];     confs_val = confs_val[size_mask]

        aspect  = np.maximum(bws_real, bhs_real) / (np.minimum(bws_real, bhs_real) + 1e-6)
        ar_mask = aspect <= MAX_ASPECT_RATIO
        if not np.any(ar_mask): return None

        cxs_real  = cxs_real[ar_mask];   cys_real  = cys_real[ar_mask]
        bws_real  = bws_real[ar_mask];   bhs_real  = bhs_real[ar_mask]
        radii     = radii[ar_mask];      confs_val = confs_val[ar_mask]

        x1s    = (cxs_real - bws_real / 2).tolist()
        y1s    = (cys_real - bhs_real / 2).tolist()
        boxes  = list(zip([int(x) for x in x1s], [int(y) for y in y1s],[int(x) for x in bws_real.tolist()],[int(y) for y in bhs_real.tolist()]))
        scores = confs_val.tolist()

        nms_idxs = cv2.dnn.NMSBoxes(boxes, scores,
                                     score_threshold=self.CONF_THRESH,
                                     nms_threshold=self.NMS_IOU)
        if nms_idxs is None or len(nms_idxs) == 0:
            return None

        nms_idxs = nms_idxs.flatten()
        
        # --- NOUVEAUTÉ : Filtrage spatial (ROI) basé sur la cinématique ---
        best_i = -1
        best_score = -1.0

        for i in nms_idxs:
            u, v = cxs_real[i], cys_real[i]
            
            # Si on a une prédiction (balle suivie), on rejette les faux positifs au loin
            if roi_center is not None and roi_radius is not None:
                dist = np.hypot(u - roi_center[0], v - roi_center[1])
                if dist > roi_radius:
                    continue # Objet (tête, chaussure) trop loin de la trajectoire prévue
            
            if scores[i] > best_score:
                best_score = scores[i]
                best_i = i

        if best_i == -1:
            return None # Aucune balle trouvée DANS la zone d'intérêt

        return {
            "u":      int(cxs_real[best_i]),
            "v":      int(cys_real[best_i]),
            "radius": max(4, int(radii[best_i])),
            "conf":   round(float(confs_val[best_i]), 3),
        }

    @staticmethod
    def _find_model():
        for p in MODEL_PATHS:
            if os.path.exists(p):
                return p
        return None