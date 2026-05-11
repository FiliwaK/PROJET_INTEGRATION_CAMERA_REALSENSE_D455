# ============================================================
#  test_dual_cam_bounce.py  v15
#
#  NOUVEAUTES v15 :
#    - Affichage 100% lisible et propre.
#    - RÈGLE IN/OUT : Exclusivement gérée par la caméra TOP !
#    - Suppression de la ligne de seuil jaune ("35cm") sur
#      le graphique de hauteur.
# ============================================================

import pyrealsense2 as rs
import numpy as np
import cv2
import time
import json
import os
import threading
from collections import deque

from ball_detector       import BallDetector
from side_height_tracker import SideHeightTracker, SIDE_CALIB_FILE
from geometry_engine     import GeometryEngine

SIDE_CAM_HEIGHT_M = 0.115
MAX_DISPLAY_H     = 0.50

CORNERS_JSON_PATHS = [
    "terrain_corners.json",
    os.path.join(os.path.dirname(__file__), "terrain_corners.json"),
    r"C:\Users\533\Desktop\Projet_3D\PythonDetection\terrain_corners.json",
]

# Seuils géométriques (TOP cam)
GEO_IN_AIR_M = 0.13
GEO_ON_GND_M = 0.09
GEO_MIN_DIST = 0.10

class FPSCounter:
    def __init__(self, n=30):
        self._t = deque(maxlen=n)
    def tick(self):
        self._t.append(time.time())
    @property
    def fps(self):
        if len(self._t) < 2: return 0.0
        dt = self._t[-1] - self._t[0]
        return (len(self._t)-1) / dt if dt > 0 else 0.0

def load_homography():
    for p in CORNERS_JSON_PATHS:
        if os.path.exists(p):
            try:
                with open(p) as f:
                    data = json.load(f)
                if "pixels" not in data or len(data["pixels"]) != 4: continue
                src = np.array(data["pixels"], dtype=np.float32)
                dst = np.array([[0,0],[1,0],[1,1],[0,1]], dtype=np.float32)
                H, _ = cv2.findHomography(src, dst)
                print(f"[main] Homographie OK : {p}")
                return H
            except Exception as e: 
                print(f"[main] Erreur : {e}")
    print("[main] terrain_corners.json introuvable")
    return None

def pixel_to_court(H, u, v):
    if H is None: return -1.0,-1.0
    out = cv2.perspectiveTransform(np.array([[[float(u),float(v)]]],dtype=np.float32),H)
    return round(float(out[0,0,0]),4), round(float(out[0,0,1]),4)

def get_gravity(serial):
    try:
        p=rs.pipeline(); c=rs.config()
        c.enable_device(serial); c.enable_stream(rs.stream.accel)
        p.start(c); samples=[]
        for _ in range(10):
            f=p.wait_for_frames()
            a=f.first_or_default(rs.stream.accel)
            if a: 
                d=a.as_motion_frame().get_motion_data()
                samples.append([d.x,d.y,d.z])
        p.stop()
        return np.mean(samples,axis=0) if samples else None
    except Exception: return None

def auto_detect():
    ctx=rs.context()
    serials=[d.get_info(rs.camera_info.serial_number) for d in ctx.query_devices()]
    if len(serials)<2: 
        raise RuntimeError(f"{len(serials)} camera(s) -- besoin 2.")
    vectors={s:get_gravity(s) for s in serials}
    s1,s2=serials
    if vectors[s1] is not None and vectors[s2] is not None:
        for s,v in vectors.items(): 
            print(f"  {s} g=[{v[0]:+.1f},{v[1]:+.1f},{v[2]:+.1f}]")
        top_is_s1=abs(vectors[s1][2])>abs(vectors[s2][2])
        st,ss=(s1,s2) if top_is_s1 else (s2,s1)
    else: 
        st,ss=s1,s2
        print("  IMU non lisible -> ordre par defaut")
    print(f"  TOP={st}  SIDE={ss}")
    return st,ss

def start_pipe(serial):
    p=rs.pipeline(); c=rs.config()
    c.enable_device(serial)
    c.enable_stream(rs.stream.color,640,480,rs.format.bgr8,30)
    c.enable_stream(rs.stream.depth,640,480,rs.format.z16,30)
    prof=p.start(c)
    al=rs.align(rs.stream.color)
    intr=prof.get_stream(rs.stream.color).as_video_stream_profile().get_intrinsics()
    return p,al,intr

# ── Thread SIDE ──
def make_side_thread(pipe_side, align_side, intr_side, tracker, side_lock, side_data, floor_ref, stop_event):
    fps = FPSCounter(30)
    no_ball = 0
    RESET_N = 8

    def loop():
        nonlocal no_ball
        while not stop_event.is_set():
            try:
                fr_s=pipe_side.wait_for_frames(timeout_ms=100)
                al_s=align_side.process(fr_s)
                cf_s=al_s.get_color_frame(); df_s=al_s.get_depth_frame()
                if not cf_s or not df_s: continue
                raw_s=np.asanyarray(cf_s.get_data())
            except Exception: continue

            fps.tick()
            prev_det_n=tracker._tracker_det_n
            det=tracker.detect_ball(raw_s)

            h_m=-1.0; ball_uvr=None; bounce=False; on_gnd=False; tracker_mode=False
            dist_s=-1.0

            if det:
                no_ball=0
                tracker_mode=(tracker._tracker_det_n==prev_det_n and tracker._tracker_ok)
                u_s,v_s,r_s=det; ball_uvr=(u_s,v_s,r_s)
                dist_s=SideHeightTracker.get_depth_median(df_s,u_s,v_s)
                h_m=tracker.compute_height(u_s,v_s,dist_s,intr_side)
                bounce=tracker.update(h_m,u_s,v_s)
                on_gnd=tracker.is_on_ground(h_m,-1,floor_ref[0])
            else:
                no_ball+=1
                if no_ball>=RESET_N: 
                    tracker.reset()
                    h_m=-1.0

            with side_lock:
                side_data["frame"]         = raw_s
                side_data["ball_uvr"]      = ball_uvr
                side_data["height_m"]      = h_m
                side_data["depth_m"]       = dist_s   
                side_data["on_ground"]     = on_gnd
                side_data["tracker_mode"]  = tracker_mode
                side_data["is_descending"] = tracker.is_descending
                side_data["valley_h"]      = tracker.valley_h
                side_data["fps"]           = fps.fps
                if bounce:
                    side_data["bounce"] = True

    return threading.Thread(target=loop, daemon=True)

# ── Calibrations ──
_side_pts_tmp=[]; _side_calib_ok=False

def _on_side_click(event,x,y,flags,param):
    global _side_pts_tmp,_side_calib_ok
    if event==cv2.EVENT_LBUTTONDOWN and len(_side_pts_tmp)<4:
        _side_pts_tmp.append((x,y))
        if len(_side_pts_tmp)==4: _side_calib_ok=True

def calibrate_terrain_side(pipe_side, align_side, tracker, force=False):
    global _side_pts_tmp,_side_calib_ok
    if not force and tracker.side_pts is not None:
        print("[calib SIDE] Terrain charge depuis fichier."); return
    if force and os.path.exists(SIDE_CALIB_FILE): 
        os.remove(SIDE_CALIB_FILE)
        
    print("\n== CALIBRATION TERRAIN SIDE ==")
    _side_pts_tmp=[]; _side_calib_ok=False
    COLORS=[(0,255,255),(0,165,255),(0,60,255),(0,255,0)]
    LABELS=["HG","HD","BD","BG"]
    
    cv2.namedWindow("Calibration terrain SIDE")
    cv2.setMouseCallback("Calibration terrain SIDE",_on_side_click)
    
    while True:
        try:
            fr=pipe_side.wait_for_frames(timeout_ms=500)
            al=align_side.process(fr); cf=al.get_color_frame()
            if not cf: continue
            img=np.asanyarray(cf.get_data()).copy()
        except Exception: continue
        
        for i,(cx,cy) in enumerate(_side_pts_tmp):
            cv2.circle(img,(cx,cy),9,COLORS[i],-1)
            cv2.circle(img,(cx,cy),11,(255,255,255),1)
            cv2.putText(img,LABELS[i],(cx+12,cy-6),cv2.FONT_HERSHEY_SIMPLEX,0.5,COLORS[i],1)
            
        if len(_side_pts_tmp)>=2:
            pts=np.array(_side_pts_tmp,dtype=np.int32)
            cv2.polylines(img,[pts],_side_calib_ok,(0,220,0),2)
            if _side_calib_ok:
                ov=img.copy()
                cv2.fillPoly(ov,[pts],(0,150,0))
                img=cv2.addWeighted(ov,0.15,img,0.85,0)
                
        hi=img.shape[0]
        if _side_calib_ok: 
            cv2.putText(img,"OK -> ENTREE pour valider",(10,hi-10),cv2.FONT_HERSHEY_SIMPLEX,0.55,(0,255,0),2)
        else:
            n=4-len(_side_pts_tmp)
            lbl=LABELS[len(_side_pts_tmp)] if len(_side_pts_tmp)<4 else ""
            cv2.putText(img,f"Coin {len(_side_pts_tmp)+1}/4: {lbl} ({n} restant)",(10,30),cv2.FONT_HERSHEY_SIMPLEX,0.6,(0,255,255),2)
            
        cv2.putText(img,"R=Reset ENTREE=OK Q=Passer",(10,hi-30),cv2.FONT_HERSHEY_SIMPLEX,0.40,(150,150,0),1)
        cv2.imshow("Calibration terrain SIDE",img)
        
        key=cv2.waitKey(1)&0xFF
        if key==ord('r'): 
            _side_pts_tmp=[]; _side_calib_ok=False
        elif key==13 and _side_calib_ok: 
            tracker.save_side_terrain(_side_pts_tmp); break
        elif key in (ord('q'),27): break
        
    cv2.destroyWindow("Calibration terrain SIDE")

_calib_click=None

def _on_calib_floor_click(event,x,y,flags,param):
    global _calib_click
    if event==cv2.EVENT_LBUTTONDOWN: _calib_click=(x,y)

def calibrate_floor_side(pipe_side, align_side, intr_side, tracker):
    global _calib_click
    print("\n== CALIBRATION SOL SIDE ==")
    collecting=False; samples=[]; t_start=0.0; _calib_click=None
    WIN="Calibration sol SIDE"
    cv2.namedWindow(WIN); cv2.setMouseCallback(WIN,_on_calib_floor_click)
    
    while True:
        try:
            fr=pipe_side.wait_for_frames(timeout_ms=500)
            al=align_side.process(fr); cf=al.get_color_frame(); df=al.get_depth_frame()
            if not cf or not df: continue
            raw_img=np.asanyarray(cf.get_data()).copy()
        except Exception as e:
            continue

        if _calib_click is not None:
            cx,cy=_calib_click; _calib_click=None; R=22
            bbox=(max(0,cx-R),max(0,cy-R),min(raw_img.shape[1]-max(0,cx-R),2*R),min(raw_img.shape[0]-max(0,cy-R),2*R))
            tracker.init_tracker_manual(raw_img,bbox)

        det=tracker.detect_ball(raw_img)
        img=tracker.draw_side_terrain(raw_img.copy())
        hi,wi=img.shape[:2]

        if det:
            u,v,r=det
            col=(0,200,255) if not collecting else (0,255,100)
            cv2.circle(img,(u,v),r,col,2)
            cv2.circle(img,(u,v),3,col,-1)
            
            if collecting:
                d=SideHeightTracker.get_depth_median(df,u,v)
                h=tracker.compute_height(u,v,d,intr_side)
                if h>=0: samples.append(h)
                pct=min(int((time.time()-t_start)/3.0*100),100)
                cv2.putText(img,f"Mesure {pct}% ({len(samples)} pts)",(10,50),cv2.FONT_HERSHEY_SIMPLEX,0.7,(0,255,100),2)
                
                if time.time()-t_start>=3.0:
                    if tracker.calibrate_floor(samples):
                        cv2.destroyWindow(WIN); return True
                    collecting=False; samples=[]
        else:
            cv2.putText(img,"Balle non detectee -- clic gauche",(10,50),cv2.FONT_HERSHEY_SIMPLEX,0.48,(0,80,255),2)

        cv2.putText(img,"Balle sur SOL -> ESPACE",(10,30),cv2.FONT_HERSHEY_SIMPLEX,0.52,(0,255,255),2)
        cv2.putText(img,"Clic=tracker C=terrain ESPACE=mesure Q=passer",(10,hi-10),cv2.FONT_HERSHEY_SIMPLEX,0.33,(80,80,80),1)
        cv2.imshow(WIN,img)

        key=cv2.waitKey(1)&0xFF
        if key==ord(' ') and not collecting and det: 
            collecting=True; samples=[]; t_start=time.time()
        elif key==ord('c'):
            cv2.destroyWindow(WIN)
            calibrate_terrain_side(pipe_side,align_side,tracker,force=True)
            cv2.namedWindow(WIN); cv2.setMouseCallback(WIN,_on_calib_floor_click)
            collecting=False; samples=[]
        elif key in (ord('q'),27): 
            cv2.destroyWindow(WIN); return False

def calibrate_floor_top(pipe_top, align_top):
    print("\n== CALIBRATION SOL TOP =="); floor_depth=-1.0
    cv2.namedWindow("Calibration sol TOP")
    
    while True:
        try:
            fr=pipe_top.wait_for_frames(timeout_ms=500); al=align_top.process(fr)
            cf=al.get_color_frame(); df=al.get_depth_frame()
            if not cf or not df: continue
            img=np.asanyarray(cf.get_data()).copy()
        except Exception: continue
        
        h,w=img.shape[:2]; cx,cy=w//2,h//2
        cv2.line(img,(cx-15,cy),(cx+15,cy),(0,255,255),1)
        cv2.line(img,(cx,cy-15),(cx,cy+15),(0,255,255),1)
        
        samp=[df.get_distance(cx+du,cy+dv) for du in range(-10,11,2) for dv in range(-10,11,2) if df.get_distance(cx+du,cy+dv)>0.1]
        live_d=float(np.median(samp)) if samp else 0
        
        cv2.putText(img,f"Sol: {live_d:.3f}m",(10,30),cv2.FONT_HERSHEY_SIMPLEX,0.6,(0,255,255),2)
        cv2.putText(img,"T=fixer Q=passer",(10,h-10),cv2.FONT_HERSHEY_SIMPLEX,0.45,(150,150,0),1)
        cv2.imshow("Calibration sol TOP",img)
        
        key=cv2.waitKey(1)&0xFF
        if key==ord('t') and live_d>0.1: 
            floor_depth=live_d; print(f"  Sol TOP = {floor_depth:.3f}m"); break
        elif key in (ord('q'),27): break
        
    cv2.destroyWindow("Calibration sol TOP")
    return floor_depth

# ── Dessins ──
def draw_cross(img, u, v, color=(0,80,255), size=15, thickness=1):
    cv2.line(img,(u-size,v),(u+size,v),color,thickness)
    cv2.line(img,(u,v-size),(u,v+size),color,thickness)
    cv2.circle(img,(u,v),size,color,1)
    cv2.circle(img,(u,v),size+8,color,1)
    cv2.circle(img,(u,v),2,color,-1)

def draw_top_overlay(debug_top, current_h, result_top,
                     bounce_cross_pixel, bounce_cross_until, bounce_flash_until,
                     last_is_in, tracker, floor_depth_top, dist_top, fps_val,
                     geo_h=-1.0, ground_smooth=True):
    out = debug_top.copy()
    hi, wi = out.shape[:2]
    now = time.time()

    if not ground_smooth and current_h > 0 and result_top["found"]:
        cv2.putText(out,f"EN VOL {current_h*100:.0f}cm -- suspendu",
                    (10,hi-30),cv2.FONT_HERSHEY_SIMPLEX,0.52,(0,200,255),2)

    if bounce_cross_pixel is not None and now < bounce_cross_until:
        col = (0,255,80) if last_is_in else (0,60,255)
        draw_cross(out, bounce_cross_pixel[0], bounce_cross_pixel[1], color=col, size=15, thickness=1)

    if now < bounce_flash_until:
        ov = out.copy()
        cv2.rectangle(ov, (0,0), (wi,hi), (0,40,200), -1)
        cv2.addWeighted(ov, 0.12, out, 0.88, 0, out) 
        v_txt = "REBOND IN" if last_is_in else "REBOND OUT"
        v_col = (0, 255, 80) if last_is_in else (0, 60, 255)
        cv2.putText(out, v_txt, (wi//2 - 80, 40), cv2.FONT_HERSHEY_DUPLEX, 0.8, v_col, 2)

    cv2.putText(out,f"TOP {fps_val:.0f}fps",(10,20),cv2.FONT_HERSHEY_SIMPLEX,0.5,(200,200,0),1)

    if geo_h >= 0.0:
        geo_in_air = geo_h > GEO_IN_AIR_M
        geo_col    = (0, 60, 255) if geo_in_air else (0, 220, 80)
        geo_lbl    = f"GEO:{geo_h*100:.0f}cm {'VOL' if geo_in_air else 'SOL'}"
        cv2.putText(out, geo_lbl, (wi - 160, 40), cv2.FONT_HERSHEY_SIMPLEX, 0.48, geo_col, 1)
    else:
        cv2.putText(out, "GEO:--", (wi - 100, 40), cv2.FONT_HERSHEY_SIMPLEX, 0.48, (80, 80, 80), 1)

    return out


def draw_side_overlay(img, ball_uvr, height_m, state_label, tracker,
                      tracker_mode, dist_top, floor_depth_top, fps_val,
                      ground_smooth):
    out = tracker.draw_side_terrain(img.copy())
    hi, wi = out.shape[:2]

    # Ligne qui montre le gap en pixels
    out = tracker.draw_ground_line(out)

    # ── Jauge de hauteur ──
    bx, by, bw, bh = 14, 50, 22, hi - 90
    ratio = (max(0.0, min(height_m, MAX_DISPLAY_H)) / MAX_DISPLAY_H if height_m >= 0 else 0)
    fill_h = int(ratio * bh)
    bc = (0, 220, 80) if ratio > 0.4 else ((0, 200, 255) if ratio > 0.15 else (0, 80, 255))
    
    # Fond
    cv2.rectangle(out, (bx, by), (bx + bw, by + bh), (30, 30, 30), -1)
    # Remplissage de la barre
    cv2.rectangle(out, (bx, by + bh - fill_h), (bx + bw, by + bh), bc, -1)
    # Contour extérieur
    cv2.rectangle(out, (bx, by), (bx + bw, by + bh), (80, 80, 80), 1)
    
    # Ligne rouge de base (Sol à 0)
    cv2.line(out, (bx - 3, by + bh), (bx + bw + 3, by + bh), (0, 50, 255), 2)
    
    # Texte hauteur exacte
    h_txt = f"{height_m*100:.1f}cm" if height_m >= 0 else "---"
    cv2.putText(out, h_txt, (bx, by - 8), cv2.FONT_HERSHEY_SIMPLEX, 0.40, (200, 200, 200), 1)

    # Dessin de la balle
    if ball_uvr:
        u, v, r = ball_uvr
        in_t = tracker.ball_in_terrain_side(u, v)
        col = (0, 255, 255) if (in_t is None or in_t) else (0, 80, 255)
        
        cv2.circle(out, (u, v), r, col, 1 if tracker_mode else 2)
        cv2.circle(out, (u, v), 3, col, -1)
        
        if tracker_mode:
            cv2.putText(out, "CSRT", (u + r + 3, v), cv2.FONT_HERSHEY_SIMPLEX, 0.38, (255, 200, 0), 1)

        # Indicateur Visuel override
        visual = tracker.is_ball_on_ground_visual(u, v, r, height_m)
        if visual is True:
            cv2.putText(out, "[VIS:SOL]", (u + r + 4, v - r - 4), cv2.FONT_HERSHEY_SIMPLEX, 0.38, (0, 255, 80), 1)
        elif visual is False:
            gap_px = tracker.get_pixel_gap()
            gap_txt = f"{gap_px:+.0f}px" if gap_px is not None else ""
            cv2.putText(out, f"[VIS:VOL {gap_txt}]", (u + r + 4, v - r - 4), cv2.FONT_HERSHEY_SIMPLEX, 0.38, (0, 150, 255), 1)

    # Etat de la machine à états (IDLE/DESCEND)
    cv2.putText(out, f"state: {state_label}", (50, 26), cv2.FONT_HERSHEY_SIMPLEX, 0.48, (180, 180, 60), 1)
    
    if height_m >= 0:
        status = "SOL" if ground_smooth else f"VOL {height_m*100:.0f}cm"
        col = (0, 255, 80) if ground_smooth else (0, 200, 255)
        cv2.putText(out, status, (50, hi - 30), cv2.FONT_HERSHEY_SIMPLEX, 0.8, col, 2)
    else:
        cv2.putText(out, "---", (50, hi - 30), cv2.FONT_HERSHEY_SIMPLEX, 0.8, (80, 80, 80), 1)

    cv2.putText(out, f"SIDE {fps_val:.0f}fps", (wi - 100, 20), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (200, 200, 0), 1)
    
    cv2.putText(out, "C=terrain ESPACE=sol T=TOP S=tracker R=reset Q=quit",
                (10, hi - 10), cv2.FONT_HERSHEY_SIMPLEX, 0.30, (80, 80, 80), 1)
    return out

def select_tracker_roi(tracker):
    if tracker._last_frame is None: return
    roi = cv2.selectROI("Selectionner", tracker._last_frame, fromCenter=False, showCrosshair=True)
    cv2.destroyWindow("Selectionner")
    x, y, w, h = roi
    if w > 4 and h > 4: 
        tracker.init_tracker_manual(tracker._last_frame, (x, y, w, h))

# ── Main ──
def main():
    print("="*55)
    print("  Dual cam bounce v15 (Visual Override Intégré)")
    print("  C=terrain ESPACE=sol T=TOP S=tracker R=reset Q=quit")
    print("="*55)

    H_court = load_homography()
    serial_top, serial_side = auto_detect()

    print("\nDemarrage camera SIDE...")
    pipe_side, align_side, intr_side = start_pipe(serial_side)

    deadline = time.time() + 1.0
    while time.time() < deadline:
        try: pipe_side.wait_for_frames(timeout_ms=500)
        except Exception: pass

    tracker = SideHeightTracker(camera_height_m=SIDE_CAM_HEIGHT_M)
    calibrate_terrain_side(pipe_side, align_side, tracker)
    calibrate_floor_side(pipe_side, align_side, intr_side, tracker)

    print("\nDemarrage camera TOP...")
    pipe_top, align_top, intr_top = start_pipe(serial_top)

    deadline = time.time() + 1.0
    while time.time() < deadline:
        try: pipe_top.wait_for_frames(timeout_ms=500)
        except Exception: pass

    floor_depth_top = calibrate_floor_top(pipe_top, align_top)
    ball_top        = BallDetector()

    geo_engine = GeometryEngine(ball_radius_m=0.037)
    floor_ref  = [floor_depth_top]
    side_lock  = threading.Lock()
    side_data  = {
        "frame":None,"ball_uvr":None,"height_m":-1.0,"depth_m":-1.0,"on_ground":False,
        "bounce":False,"tracker_mode":False,"is_descending":False,
        "valley_h":9999.0,"fps":0.0,
    }
    stop_event = threading.Event()

    side_thread = make_side_thread(pipe_side,align_side,intr_side,
                                   tracker,side_lock,side_data,floor_ref,stop_event)
    side_thread.start()

    fps_top      = FPSCounter(30)
    last_is_in   = True
    dist_top     = -1.0

    bounce_cross_pixel = None
    bounce_cross_until = 0.0
    bounce_flash_until = 0.0

    last_top_pixel   = None
    valley_top_pixel = None
    geo_height_top   = -1.0
    ground_hist = deque(maxlen=7)

    print("\nC'est parti !\n")

    try:
        while True:
            key = cv2.waitKey(1) & 0xFF
            if key in (ord('q'), 27): break
            if key == ord('r'):
                tracker.reset()
                bounce_cross_pixel = None
                bounce_cross_until = 0.0
                bounce_flash_until = 0.0
                valley_top_pixel = None
            if key == ord('c'): calibrate_terrain_side(pipe_side, align_side, tracker, force=True)
            if key == ord('t'):
                floor_depth_top = calibrate_floor_top(pipe_top, align_top)
                floor_ref[0] = floor_depth_top
            if key == ord('s'): select_tracker_roi(tracker)

            result_top = {"found":False,"is_in":True,"u":0,"v":0,"radius":0}
            debug_top = None
            geo_height_top = -1.0

            try:
                fr = pipe_top.wait_for_frames(timeout_ms=100)
                al = align_top.process(fr)
                cf = al.get_color_frame()
                df_t = al.get_depth_frame()
                
                if cf:
                    img_top = np.asanyarray(cf.get_data())
                    result_top = ball_top.detect(img_top)
                    debug_top = ball_top.draw_debug(img_top, result_top)
                    
                    if df_t and result_top["found"]:
                        u_t = result_top["u"]; v_t = result_top["v"]
                        dist_top = SideHeightTracker.get_depth_median(df_t, u_t, v_t)

                        if dist_top > GEO_MIN_DIST:
                            raw_dist = df_t.get_distance(u_t, v_t)
                            if raw_dist > GEO_MIN_DIST:
                                pt3d = rs.rs2_deproject_pixel_to_point(intr_top, [float(u_t), float(v_t)], dist_top)
                                geo_height_top = geo_engine.compute_height_above_floor(pt3d)
                    else:
                        dist_top = -1.0
            except Exception: pass

            fps_top.tick()
            if result_top["found"]: 
                last_top_pixel = (result_top["u"], result_top["v"])

            court_u, court_v = -1.0, -1.0
            if result_top["found"]:
                court_u, court_v = pixel_to_court(H_court, result_top["u"], result_top["v"])

            geo_corrected_pixel = last_top_pixel
            if (result_top["found"] and geo_height_top > 0.04 and geo_engine.plane_normal is not None and dist_top > GEO_MIN_DIST):
                gnd_pt = geo_engine.get_corrected_court_point(result_top["u"], result_top["v"], intr_top, geo_height_top)
                if gnd_pt is not None:
                    gnd_px = geo_engine.project_3d_to_pixel(gnd_pt, intr_top)
                    if gnd_px is not None and 0 <= gnd_px[0] < 640 and 0 <= gnd_px[1] < 480:
                        geo_corrected_pixel = gnd_px

            with side_lock:
                side_h       = side_data["height_m"]
                side_depth   = side_data.get("depth_m", -1.0)
                side_bounce  = side_data["bounce"]
                side_frame   = side_data["frame"]
                side_ball    = side_data["ball_uvr"]
                side_tm      = side_data["tracker_mode"]
                side_desc    = side_data["is_descending"]
                side_vly     = side_data["valley_h"]
                side_fps     = side_data["fps"]
                if side_bounce: side_data["bounce"] = False

            top_radius = result_top["radius"] if result_top["found"] else 0
            vote_sol = 0
            vote_vol = 0

            gap_px = tracker.get_pixel_gap()
            gap_str = f"{gap_px:+.0f}px" if gap_px is not None else "---"
            
            depth_str = f"{side_depth:.2f}m" if side_depth > 0 else "---"
            if side_depth > 0 and side_depth < 0.20:
                depth_str += "(!)"

            side_visual = tracker.is_ball_on_ground_visual(height_m=side_h)
            vis_tag = "VIS:SOL" if side_visual is True else ("VIS:VOL" if side_visual is False else "VIS:?")

            if side_visual is True: vote_sol += 2
            elif side_visual is False: vote_vol += 1

            if result_top["found"] and geo_engine.plane_normal is not None:
                expected_r = geo_engine.expected_radius_if_on_ground(result_top["u"], result_top["v"], intr_top)
                radius_ratio = (top_radius / expected_r) if expected_r > 0 else 0.0
                if radius_ratio > 0:
                    if   radius_ratio <= 1.20: vote_sol += 1
                    elif radius_ratio >= 1.50: vote_vol += 1
            else: radius_ratio = 0.0

            if geo_height_top >= 0.0:
                if   geo_height_top <  GEO_ON_GND_M: vote_sol += 1
                elif geo_height_top >  GEO_IN_AIR_M: vote_vol += 1

            if side_visual is None and side_h >= 0:
                if side_h <= tracker.get_ground_thresh(): vote_sol += 1
                else: vote_vol += 1

            on_gnd_full = (vote_vol <= vote_sol)
            ground_hist.append(on_gnd_full)
            ground_smooth = sum(ground_hist) > len(ground_hist)//2

            if side_desc and side_h >= 0 and side_h <= side_vly:
                valley_top_pixel = geo_corrected_pixel

            if side_bounce:
                if ground_smooth:
                    # RÈGLE DU IN/OUT : Exclusivement gérée par la TOP cam.
                    last_is_in = result_top.get("is_in", True)
                    
                    bounce_cross_pixel = valley_top_pixel or geo_corrected_pixel or last_top_pixel
                    bounce_cross_until = time.time() + 2.0
                    bounce_flash_until = time.time() + 0.5
                    valley_top_pixel = None

                    v_str = "IN" if last_is_in else "OUT"
                    print(f"\r  >>> REBOND {v_str} <<<  h={side_h:.3f}m  court=({court_u:.2f},{court_v:.2f})   ")

            if not side_desc: valley_top_pixel = None

            if side_h >= 0:
                geo_log = f"geo:{geo_height_top*100:.0f}cm" if geo_height_top >= 0 else "geo:--"
                status  = "SOL" if ground_smooth else "VOL"
                
                print(f"\r{status} [gap:{gap_str} Z:{depth_str}] h={side_h*100:.1f}cm "
                      f"{geo_log} {vis_tag} [s:{vote_sol} v:{vote_vol}]   ", end="")

            if debug_top is not None:
                cv2.imshow("TOP cam -- position + rebond",
                           draw_top_overlay(debug_top, side_h, result_top,
                                            bounce_cross_pixel, bounce_cross_until,
                                            bounce_flash_until, last_is_in,
                                            tracker, floor_depth_top, dist_top, fps_top.fps,
                                            geo_h=geo_height_top, ground_smooth=ground_smooth))

            if side_frame is not None:
                cv2.imshow("SIDE cam -- hauteur balle",
                           draw_side_overlay(side_frame, side_ball, side_h,
                                             tracker.get_state_label(), tracker, side_tm,
                                             dist_top, floor_depth_top, side_fps, ground_smooth))
    finally:
        stop_event.set()
        pipe_top.stop()
        pipe_side.stop()
        cv2.destroyAllWindows()
        print("\nCameras arretees.")

if __name__=="__main__":
    main()