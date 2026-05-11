# ============================================================
#  side_height_tracker.py  v16 — Anti-occlusion, ROI dynamique,
#  Sensibilité Rebond Pro + Validation visuelle assouplie au fond
#  + OPTIMISATION ZÉRO LAG
# ============================================================

import numpy as np
import cv2
import json
import os
import pyrealsense2 as rs

try:
    from yolo_ball_detector import YoloBallDetector
    _YOLO_OK = True
except ImportError:
    _YOLO_OK = False

# HSV pickleball neon jaune-vert
BALL_LOWER = np.array([25, 120, 120], dtype=np.uint8)
BALL_UPPER = np.array([60, 255, 255], dtype=np.uint8)

FLOOR_CALIB_FILE = "floor_calib.json"
SIDE_CALIB_FILE  = "side_terrain_calib.json"

# Zone de detection
SIDE_MARGIN_H   = 30
SIDE_MARGIN_V   = 20
SIDE_MARGIN_TOP = 160

# Tracker CSRT
TRACKER_REINIT_EVERY  = 8
TRACKER_LOST_MAX      = 20
TRACKER_MAX_DRIFT     = 180
CSRT_MIN_COLOR_RATIO = 0.15

# Detection HSV
MIN_RADIUS_HSV = 9    
MAX_RADIUS_HSV = 80
MIN_CIRC_HSV   = 0.70

# Lissage EMA
EMA_ALPHA_SIDE = 0.55

# Machine a etats
_IDLE = 0; _DESCEND = 1; _COOLDOWN = 2
STATE_LABELS = {_IDLE: "IDLE", _DESCEND: "DESCEND", _COOLDOWN: "COOLDOWN"}

# ── Sensibilité Rebond (Type Pro) ──
ABOVE_THRESH_MIN = 2     
MIN_DROP_M       = 0.03  
VISUAL_ON_GROUND_PX = 20  
VISUAL_IN_AIR_PX    = 40  


class SideHeightTracker:

    def __init__(self, camera_height_m=0.115, rise_thresh=0.015, cooldown_frames=15):

        self._cam_h         = camera_height_m
        self._floor_h       = None
        self._ground_thresh = None
        self._rise_thresh   = rise_thresh
        self._cooldown_max  = cooldown_frames

        self._state              = _IDLE
        self._cooldown_left      = 0
        self._valley_h           = 9999.0
        self._peak_h             = 0.0
        self._above_thresh_count = 0

        self.side_pts           = None
        self._detect_mask       = None
        self._detect_mask_shape = None

        self._tracker       = None
        self._tracker_ok    = False
        self._tracker_lost  = 0
        self._tracker_det_n = 0
        self._last_frame    = None

        self._last_ball_u   = None
        self._last_ball_v   = None
        self._last_ball_r   = None
        self._ema_u         = None
        self._ema_v         = None
        self._ema_r         = None
        self._ema_lost      = 0
        self._EMA_RESET_N   = 6
        
        self._vx            = 0.0
        self._vy            = 0.0

        self._yolo = None
        if _YOLO_OK:
            try:
                self._yolo = YoloBallDetector()
                print("[SideTracker] YOLO actif")
            except Exception:
                print("[SideTracker] YOLO indispo -> HSV + tracker")

        self._load_floor_calib()
        self._load_side_terrain()

    def _load_floor_calib(self):
        if os.path.exists(FLOOR_CALIB_FILE):
            try:
                with open(FLOOR_CALIB_FILE) as f:
                    d = json.load(f)
                self._floor_h       = d["floor_h"]
                self._ground_thresh = d["ground_thresh"]
                print(f"[SideTracker] Sol : h={self._floor_h*100:.1f}cm  seuil={self._ground_thresh*100:.1f}cm")
            except Exception: pass

    def calibrate_floor(self, samples):
        valid =[h for h in samples if 0.0 < h < 0.4]
        if len(valid) < 5: return False
        self._floor_h       = float(np.median(valid))
        self._ground_thresh = self._floor_h + 0.057
        try:
            with open(FLOOR_CALIB_FILE, "w") as f:
                json.dump({"floor_h": self._floor_h, "ground_thresh": self._ground_thresh}, f, indent=2)
        except Exception: pass
        return True

    def is_calibrated(self): return self._floor_h is not None
    def get_ground_thresh(self): return self._ground_thresh if self._ground_thresh else 0.08

    def _load_side_terrain(self):
        if os.path.exists(SIDE_CALIB_FILE):
            try:
                with open(SIDE_CALIB_FILE) as f:
                    d = json.load(f)
                if "corners" in d and len(d["corners"]) == 4:
                    self.side_pts = np.array(d["corners"], dtype=np.int32)
                    self._detect_mask = None
            except Exception: pass

    def save_side_terrain(self, corners):
        self.side_pts = np.array(corners, dtype=np.int32)
        self._detect_mask = None
        xs=[p[0] for p in corners]; ys=[p[1] for p in corners]
        try:
            with open(SIDE_CALIB_FILE,"w") as f:
                json.dump({"corners":corners,"x_left":min(xs),"x_right":max(xs),"y_top":min(ys),"y_bot":max(ys)},f,indent=2)
        except Exception: pass

    def ball_in_terrain_side(self, u, v):
        if self.side_pts is None: return None
        return cv2.pointPolygonTest(self.side_pts.astype(np.float32),(float(u),float(v)),False) >= 0

    def draw_side_terrain(self, img):
        if self.side_pts is None: return img
        out=img.copy(); ov=out.copy()
        cv2.fillPoly(ov,[self.side_pts],(0,100,0))
        cv2.addWeighted(ov,0.12,out,0.88,0,out)
        cv2.polylines(out,[self.side_pts],True,(0,220,0),2)
        xs=[p[0] for p in self.side_pts]; ys=[p[1] for p in self.side_pts]
        cv2.line(out,(min(xs),max(ys)),(max(xs),max(ys)),(0,80,255),2)
        return out

    def get_floor_y_at_x(self, u):
        if self.side_pts is None: return None
        bg = self.side_pts[3]
        bd = self.side_pts[2]
        x_range = float(bd[0] - bg[0])
        if abs(x_range) < 1.0: return float(bg[1])
        t = max(0.0, min(1.0, (float(u) - float(bg[0])) / x_range))
        return float(bg[1]) + t * float(int(bd[1]) - int(bg[1]))

    def is_ball_on_ground_visual(self, u=None, v=None, r=None, height_m=-1.0):
        u_use = u if u is not None else self._last_ball_u
        v_use = v if v is not None else self._last_ball_v
        r_use = r if r is not None else self._last_ball_r

        if u_use is None or v_use is None or r_use is None: return None
        if self.side_pts is None: return None

        ball_bottom_u = float(u_use)
        ball_bottom_v = float(v_use) + float(r_use)

        dist = cv2.pointPolygonTest(self.side_pts.astype(np.float32), (ball_bottom_u, ball_bottom_v), True)

        tol_sol = max(15.0, float(r_use) * 1.0)
        tol_vol = max(35.0, float(r_use) * 1.8)

        if dist >= -tol_sol:
            if height_m > 0.12: return False
            if float(r_use) > 40.0: return False
            return True    
        elif dist < -tol_vol:
            return False   
        else:
            if height_m >= 0.0 and height_m <= self.get_ground_thresh(): return True
            return None    

    def get_pixel_gap(self):
        if self._last_ball_u is None or self.side_pts is None: return None
        bv = float(self._last_ball_v) + float(self._last_ball_r)
        return cv2.pointPolygonTest(self.side_pts.astype(np.float32), (float(self._last_ball_u), bv), measureDist=True)

    def draw_ground_line(self, img):
        if self.side_pts is None: return img
        out = img.copy()
        if self._last_ball_u is not None and self._last_ball_v is not None and self._last_ball_r is not None:
            ball_bottom_u = int(self._last_ball_u)
            ball_bottom_v = int(self._last_ball_v + self._last_ball_r)
            dist = cv2.pointPolygonTest(self.side_pts.astype(np.float32), (float(ball_bottom_u), float(ball_bottom_v)), measureDist=True)
            cv2.line(out, (ball_bottom_u - 20, ball_bottom_v), (ball_bottom_u + 20, ball_bottom_v), (255, 200, 0), 1)
            col = (0, 255, 80) if dist >= -VISUAL_ON_GROUND_PX else (0, 150, 255)
            cv2.putText(out, f"dist:{dist:.0f}px", (ball_bottom_u + int(self._last_ball_r) + 4, ball_bottom_v - 4), cv2.FONT_HERSHEY_SIMPLEX, 0.38, col, 1)
        return out

    def _smooth_side(self, u, v, r):
        if self._ema_u is None:
            self._ema_u, self._ema_v, self._ema_r = float(u), float(v), float(r)
        else:
            self._ema_u = EMA_ALPHA_SIDE * u + (1.0 - EMA_ALPHA_SIDE) * self._ema_u
            self._ema_v = EMA_ALPHA_SIDE * v + (1.0 - EMA_ALPHA_SIDE) * self._ema_v
            self._ema_r = EMA_ALPHA_SIDE * r + (1.0 - EMA_ALPHA_SIDE) * self._ema_r
        return int(self._ema_u), int(self._ema_v), int(self._ema_r)

    def _get_detect_mask(self, h_img, w_img):
        if self._detect_mask is not None and self._detect_mask_shape == (h_img, w_img): return self._detect_mask
        mask = np.zeros((h_img, w_img), dtype=np.uint8)
        if self.side_pts is None: mask[:] = 255
        else:
            xs = self.side_pts[:, 0]; ys = self.side_pts[:, 1]
            cv2.fillPoly(mask, [self.side_pts], 255)
            y_top = int(min(ys)); y_top_search = max(0, y_top - SIDE_MARGIN_TOP)
            x_left = max(0, int(min(xs)) - SIDE_MARGIN_H); x_right = min(w_img, int(max(xs)) + SIDE_MARGIN_H)
            mask[y_top_search:y_top, x_left:x_right] = 255
            y_bot = min(h_img, int(max(ys)) + SIDE_MARGIN_V)
            mask[int(max(ys)):y_bot, x_left:x_right] = 255
        self._detect_mask = mask; self._detect_mask_shape = (h_img, w_img)
        return mask

    def _check_ball_color(self, bgr, u, v, r):
        h_img, w_img = bgr.shape[:2]
        r_c  = max(4, min(int(r), MAX_RADIUS_HSV))
        x1, y1 = max(0, u - r_c), max(0, v - r_c)
        x2, y2 = min(w_img, u + r_c + 1), min(h_img, v + r_c + 1)
        if x2 <= x1 or y2 <= y1: return 0.0
        roi = bgr[y1:y2, x1:x2]
        if roi.size == 0: return 0.0
        blur_roi  = cv2.GaussianBlur(roi, (5, 5), 0)
        hsv_roi   = cv2.cvtColor(blur_roi, cv2.COLOR_BGR2HSV)
        ball_mask = cv2.inRange(hsv_roi, BALL_LOWER, BALL_UPPER)
        return float(np.count_nonzero(ball_mask)) / max(1, ball_mask.size)

    @staticmethod
    def _try_create_tracker():
        try: return cv2.TrackerCSRT_create()
        except AttributeError:
            try: return cv2.legacy.TrackerCSRT_create()
            except Exception: return None

    def _reinit_tracker(self, frame, u, v, r):
        if frame is None or r < 4: return
        m=max(4,r//3); x=max(0,u-r-m); y=max(0,v-r-m)
        w=min(frame.shape[1]-x,(r+m)*2); h=min(frame.shape[0]-y,(r+m)*2)
        if w<8 or h<8: return
        try:
            t=self._try_create_tracker()
            if t and t.init(frame,(x,y,w,h)):
                self._tracker=t; self._tracker_ok=True; self._tracker_lost=0
        except Exception: pass

    def init_tracker_manual(self, frame, bbox):
        try:
            t=self._try_create_tracker()
            if t and t.init(frame,bbox):
                self._tracker=t; self._tracker_ok=True; self._tracker_lost=0
        except Exception: pass

    def _update_tracker(self, frame):
        if self._tracker is None or not self._tracker_ok: return None
        try:
            ok,bbox=self._tracker.update(frame)
            if ok:
                x,y,w,h=[int(v) for v in bbox]
                cx=x+w//2; cy=y+h//2; r=max(w,h)//2
                if r>=4:
                    self._tracker_lost=0
                    return cx,cy,r
            self._tracker_lost+=1
            if self._tracker_lost>=TRACKER_LOST_MAX: self._tracker=None; self._tracker_ok=False
        except Exception: self._tracker=None
        return None

    def _run_hsv(self, bgr, detect_mask, pred_u, pred_v, search_radius):
        """Helper ZÉRO LAG : Exécute HSV super rapidement dans la zone donnée"""
        blurred = cv2.GaussianBlur(bgr, (5, 5), 0)
        hsv = cv2.cvtColor(blurred, cv2.COLOR_BGR2HSV)
        mask = cv2.inRange(hsv, BALL_LOWER, BALL_UPPER)
        
        if pred_u is not None and search_radius is not None:
            roi = np.zeros(mask.shape[:2], dtype=np.uint8)
            cv2.circle(roi, (pred_u, pred_v), search_radius, 255, -1)
            mask = cv2.bitwise_and(mask, roi)
            
        mask = cv2.bitwise_and(mask, detect_mask)
        mask = cv2.erode(mask, None, iterations=1)
        mask = cv2.dilate(mask, None, iterations=2)
        
        cnts, _ = cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        best = None
        for c in cnts:
            area = cv2.contourArea(c)
            if area < 80: continue 
            ((cx,cy),r) = cv2.minEnclosingCircle(c)
            if MIN_RADIUS_HSV <= r <= MAX_RADIUS_HSV:
                peri = cv2.arcLength(c, True)
                circ = (4*np.pi*area/(peri*peri)) if peri>0 else 0
                if circ >= MIN_CIRC_HSV:
                    if best is None or r > best[2]: best = (int(cx), int(cy), int(r))
        return best

    def detect_ball(self, bgr):
        self._last_frame = bgr
        h_img, w_img = bgr.shape[:2]
        detect_mask = self._get_detect_mask(h_img, w_img)

        pred_u, pred_v, search_radius = None, None, None
        if self._last_ball_u is not None and self._last_ball_v is not None:
            pred_u = int(self._last_ball_u + self._vx)
            pred_v = int(self._last_ball_v + self._vy)
            search_radius = max(60, int(np.hypot(self._vx, self._vy) * 2.0 + 40))

        raw = None

        # 1. OPTIMISATION ZÉRO LAG : Tracker CSRT en priorité absolue (~2ms)
        track_res = self._update_tracker(bgr)
        if track_res is not None:
            u_t, v_t, r_t = track_res
            if detect_mask[v_t, u_t] > 0 and self._check_ball_color(bgr, u_t, v_t, r_t) >= CSRT_MIN_COLOR_RATIO:
                raw = track_res
            else: self._tracker = None; self._tracker_ok = False

        # 2. HSV dans la ROI (si balle suivie mais Tracker perdu) (~5ms)
        if raw is None and pred_u is not None and search_radius is not None:
            raw = self._run_hsv(bgr, detect_mask, pred_u, pred_v, search_radius)

        # 3. YOLO en secours (Scanner l'image complète) (~50ms)
        if raw is None and self._yolo is not None:
            roi_c = (pred_u, pred_v) if pred_u is not None else None
            det = self._yolo.detect(bgr, roi_center=roi_c, roi_radius=search_radius)
            if det is not None:
                u_d, v_d = int(np.clip(det["u"], 0, w_img-1)), int(np.clip(det["v"], 0, h_img-1))
                if detect_mask[v_d, u_d] > 0: raw = (det["u"], det["v"], det["radius"])

        # 4. Fallback HSV complet
        if raw is None:
            raw = self._run_hsv(bgr, detect_mask, None, None, None)

        if raw is not None:
            self._ema_lost = 0
            if self._last_ball_u is not None:
                self._vx = self._vx * 0.7 + (raw[0] - self._last_ball_u) * 0.3
                self._vy = self._vy * 0.7 + (raw[1] - self._last_ball_v) * 0.3
            else:
                self._vx = self._vy = 0.0

            su, sv, sr = self._smooth_side(raw[0], raw[1], raw[2])
            self._last_ball_u, self._last_ball_v, self._last_ball_r = su, sv, sr
            
            self._tracker_det_n += 1
            if self._tracker_det_n % TRACKER_REINIT_EVERY == 0 or not self._tracker_ok:
                self._reinit_tracker(bgr, raw[0], raw[1], raw[2])
                
            return su, sv, sr
        else:
            self._ema_lost += 1
            if self._ema_lost <= 3 and pred_u is not None:
                self._last_ball_u, self._last_ball_v = float(pred_u), float(pred_v)
                return int(pred_u), int(pred_v), int(self._last_ball_r)

            if self._ema_lost >= self._EMA_RESET_N:
                self._ema_u = self._ema_v = self._ema_r = None
                self._last_ball_u = self._last_ball_v = self._last_ball_r = None
                self._vx = self._vy = 0.0
                
            return None


    @staticmethod
    def get_depth_median(depth_frame, u, v, radius=4):
        # OPTIMISATION MAJEURE : 9 points interrogés au lieu de 64 -> Boost énorme des FPS
        w=depth_frame.get_width(); h=depth_frame.get_height()
        depths=[]
        for du in [-radius, 0, radius]:
            for dv in [-radius, 0, radius]:
                pu=max(0,min(u+du,w-1)); pv=max(0,min(v+dv,h-1))
                d=depth_frame.get_distance(pu,pv)
                if d>0.05: depths.append(d)
        return float(np.median(depths)) if depths else 0.0

    def compute_height(self, u, v, depth_m, intrinsics):
        if depth_m <= 0.05: return -1.0
        pt = rs.rs2_deproject_pixel_to_point(intrinsics,[float(u),float(v)],depth_m)
        corrected_y = (pt[1] * 0.92) - (pt[2] * 0.39)
        return max(0.0, round(self._cam_h + (-float(corrected_y)), 4))

    def is_on_ground(self, height_side, dist_top=-1, floor_depth_top=-1, top_radius=0):
        thresh = self.get_ground_thresh()
        if height_side >= 0:
            return height_side <= thresh
        return True 

    # ── Logique Intacte : Rebond Pro ──────────────────────────────────────────
    def update(self, height_m, ball_u=0, ball_v=0):
        if height_m < 0: return False
        thresh = self.get_ground_thresh()

        if self._state == _COOLDOWN:
            self._cooldown_left -= 1
            if self._cooldown_left <= 0: self._state = _IDLE
            return False

        if self._state == _IDLE:
            self._valley_h = height_m
            if height_m > thresh + 0.04:
                self._above_thresh_count += 1
                if self._above_thresh_count >= ABOVE_THRESH_MIN:
                    self._state=_DESCEND; self._peak_h=height_m; self._above_thresh_count=0
            else: 
                self._above_thresh_count=0
            return False

        if self._state == _DESCEND:
            if height_m > self._peak_h: self._peak_h=height_m
            if height_m < self._valley_h: self._valley_h=height_m
            
            rise = height_m - self._valley_h
            drop = self._peak_h - self._valley_h
            
            # Sensibilité Pro : Dès que ça remonte de 1.5cm, on valide le rebond !
            if rise > self._rise_thresh:
                if self._valley_h <= thresh + 0.05 and drop >= MIN_DROP_M:
                    self._state=_COOLDOWN
                    self._cooldown_left=self._cooldown_max
                    return True
                else:
                    self._state=_IDLE
                    self._valley_h=height_m
                    self._above_thresh_count=0
            return False
            
        return False

    def get_state_label(self): return STATE_LABELS.get(self._state,"?")
    @property
    def is_descending(self): return self._state == _DESCEND
    @property
    def valley_h(self): return self._valley_h

    def reset(self):
        self._state=_IDLE; self._cooldown_left=0; self._valley_h=9999.0
        self._peak_h=0.0; self._above_thresh_count=0
        self._ema_u=None; self._ema_v=None; self._ema_r=None
        self._last_ball_u=None; self._last_ball_v=None; self._last_ball_r=None
        self._ema_lost=0; self._vx=0.0; self._vy=0.0