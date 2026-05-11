# ============================================================
#  main.py  v11  --  Dual cam : TOP + SIDE -> representation 3D
#
#  ARCHITECTURE :
#    TOP  -> position balle (court_u/v), IN/OUT exclusive, Z sol
#    SIDE -> hauteur balle, detection rebond (thread parallele)
# ============================================================

import json
import os
import threading
import numpy as np
import cv2
import pyrealsense2 as rs
from collections import deque

from ball_detector       import BallDetector
from socket_server       import SocketServer
from side_height_tracker import SideHeightTracker
from geometry_engine     import GeometryEngine          

CORNERS_JSON_PATHS = [
    "terrain_corners.json",
    os.path.join(os.path.dirname(__file__), "terrain_corners.json"),
    r"C:\Users\533\Desktop\Projet_3D\PythonDetection\terrain_corners.json",
]

SIDE_CAM_HEIGHT_M = 0.115
BOUNCE_PERSIST_N  = 5

# ── Seuils de décision géométrique (TOP cam) ──────────────────────────────────
GEO_IN_AIR_M = 0.13   
GEO_ON_GND_M = 0.09   
GEO_MIN_DIST = 0.10   


def load_homography():
    for p in CORNERS_JSON_PATHS:
        if os.path.exists(p):
            try:
                with open(p) as f:
                    data = json.load(f)
                if "pixels" not in data or len(data["pixels"]) != 4:
                    continue
                src = np.array(data["pixels"], dtype=np.float32)
                dst = np.array([[0,0],[1,0],[1,1],[0,1]], dtype=np.float32)
                H, _ = cv2.findHomography(src, dst)
                print(f"[main] Homographie OK : {p}")
                return H
            except Exception as e:
                print(f"[main] Erreur homographie : {e}")
    print("[main] ATTENTION terrain_corners.json introuvable")
    return None

def pixel_to_court(H, u, v):
    if H is None:
        return -1.0, -1.0
    pt  = np.array([[[float(u), float(v)]]], dtype=np.float32)
    out = cv2.perspectiveTransform(pt, H)
    return round(float(out[0, 0, 0]), 4), round(float(out[0, 0, 1]), 4)

def get_gravity(serial):
    try:
        p = rs.pipeline(); c = rs.config()
        c.enable_device(serial)
        c.enable_stream(rs.stream.accel)
        p.start(c)
        samples = []
        for _ in range(10):
            f = p.wait_for_frames()
            a = f.first_or_default(rs.stream.accel)
            if a:
                d = a.as_motion_frame().get_motion_data()
                samples.append([d.x, d.y, d.z])
        p.stop()
        return np.mean(samples, axis=0) if samples else None
    except Exception:
        return None

def auto_detect_cameras():
    ctx     = rs.context()
    serials = [d.get_info(rs.camera_info.serial_number)
               for d in ctx.query_devices()]
    if len(serials) < 2:
        print(f"[main] Seulement {len(serials)} camera(s) -- mode TOP seul")
        return (serials[0] if serials else None), None

    vectors = {s: get_gravity(s) for s in serials}
    s1, s2  = serials[0], serials[1]

    if vectors.get(s1) is not None and vectors.get(s2) is not None:
        for s, v in vectors.items():
            print(f"  {s} g=[{v[0]:+.1f},{v[1]:+.1f},{v[2]:+.1f}]")
        top_is_s1 = abs(vectors[s1][2]) > abs(vectors[s2][2])
        st, ss    = (s1, s2) if top_is_s1 else (s2, s1)
    else:
        st, ss = s1, s2
        print("[main] IMU non lisible -> ordre par defaut")

    print(f"[main] TOP={st}  SIDE={ss}")
    return st, ss

def start_pipe(serial):
    p = rs.pipeline(); c = rs.config()
    c.enable_device(serial)
    c.enable_stream(rs.stream.color, 640, 480, rs.format.bgr8, 30)
    c.enable_stream(rs.stream.depth, 640, 480, rs.format.z16,  30)
    prof = p.start(c)
    al   = rs.align(rs.stream.color)
    intr = (prof.get_stream(rs.stream.color)
            .as_video_stream_profile()
            .get_intrinsics())
    return p, al, intr


def make_side_thread(pipe_side, align_side, intr_side,
                     side_tracker, side_lock, side_state):
    def loop():
        no_ball = 0
        RESET_N = 8
        while True:
            try:
                fr_s = pipe_side.wait_for_frames(timeout_ms=500)
                al_s = align_side.process(fr_s)
                cf_s = al_s.get_color_frame()
                df_s = al_s.get_depth_frame()
                if not cf_s or not df_s:
                    continue

                img_s = np.asanyarray(cf_s.get_data())
                det   = side_tracker.detect_ball(img_s)

                if det:
                    no_ball  = 0
                    u_s, v_s, _ = det
                    dist_s = SideHeightTracker.get_depth_median(df_s, u_s, v_s)
                    h_m    = side_tracker.compute_height(u_s, v_s, dist_s, intr_side)
                    bounce = side_tracker.update(h_m, u_s, v_s)

                    with side_lock:
                        side_state["height_m"]  = h_m
                        side_state["bounce"]    = bounce
                        side_state["ball_u"]    = u_s
                        side_state["ball_v"]    = v_s
                        side_state["is_desc"]   = side_tracker.is_descending
                        side_state["valley_h"]  = side_tracker.valley_h
                else:
                    no_ball += 1
                    if no_ball >= RESET_N:
                        side_tracker.reset()
                        with side_lock:
                            side_state["height_m"]  = -1.0
                            side_state["bounce"]    = False
                            side_state["is_desc"]   = False
            except Exception:
                pass

    return threading.Thread(target=loop, daemon=True)


def main():
    H_court = load_homography()
    server  = SocketServer()

    serial_top, serial_side = auto_detect_cameras()
    pipe_top, align_top, intr_top = start_pipe(serial_top)

    geo_engine = GeometryEngine(ball_radius_m=0.037)
    if geo_engine.plane_normal is not None:
        print("[main] GeometryEngine OK — triangulation angulaire activée")
    else:
        print("[main] GeometryEngine — plan du sol non disponible (calibration manquante?)")

    pipe_side    = None
    align_side   = None
    intr_side    = None
    side_tracker = None

    if serial_side:
        try:
            pipe_side, align_side, intr_side = start_pipe(serial_side)
            side_tracker = SideHeightTracker(camera_height_m=SIDE_CAM_HEIGHT_M)
            if not side_tracker.is_calibrated():
                print("[main] ATTENTION Sol SIDE non calibre -- lance test d'abord")
            else:
                print("[main] SIDE prete")
        except Exception as e:
            print(f"[main] SIDE indisponible : {e}")
            pipe_side = None

    floor_state = {"floor_depth_top": -1.0}

    side_lock  = threading.Lock()
    side_state = {
        "height_m":  -1.0,
        "bounce":    False,
        "ball_u":    0,
        "ball_v":    0,
        "is_desc":   False,
        "valley_h":  9999.0
    }

    if pipe_side and side_tracker:
        t = make_side_thread(
            pipe_side, align_side, intr_side,
            side_tracker, side_lock, side_state)
        t.start()
        print("[main] Thread SIDE demarre")

    floor_samples  = []
    floor_calib_ok = False

    bounce_persist  = 0
    bounce_court_u  = -1.0
    bounce_court_v  = -1.0
    last_top_court  = (-1.0, -1.0)
    valley_court    = None

    ground_hist = deque(maxlen=7)

    detector = BallDetector()
    print("Boucle principale demarree...")

    try:
        while True:
            frames      = pipe_top.wait_for_frames()
            aligned     = align_top.process(frames)
            color_frame = aligned.get_color_frame()
            depth_frame = aligned.get_depth_frame()

            if not color_frame or not depth_frame:
                continue

            img    = np.asanyarray(color_frame.get_data())
            result = detector.detect(img)
            
            top_radius = result["radius"] if result["found"] else 0
            
            dist_top = -1.0
            geo_height_top = -1.0

            with side_lock:
                side_h      = side_state["height_m"]
                side_bounce = side_state["bounce"]
                side_ball_u = side_state["ball_u"]
                side_ball_v = side_state["ball_v"]
                side_desc   = side_state["is_desc"]
                side_vly    = side_state["valley_h"]
                if side_bounce:
                    side_state["bounce"] = False

            if result["found"]:
                u, v  = result["u"], result["v"]
                is_in_top = result["is_in"]
                dist  = depth_frame.get_distance(u, v)

                dist_top = SideHeightTracker.get_depth_median(
                    depth_frame, u, v, radius=5)

                if not floor_calib_ok and dist_top > 0.1:
                    floor_samples.append(dist_top)
                    if len(floor_samples) >= 30:
                        floor_state["floor_depth_top"] = float(np.median(floor_samples))
                        floor_calib_ok = True
                        print(f"[main] Sol TOP calibre : {floor_state['floor_depth_top']:.3f}m")

                if dist > 0:
                    pt3d = rs.rs2_deproject_pixel_to_point(intr_top, [u, v], dist)
                    x3d = round(float(pt3d[0]), 4)
                    y3d = round(float(pt3d[1]), 4)
                    z3d = round(float(pt3d[2]), 4)

                    if dist > GEO_MIN_DIST:
                        geo_height_top = geo_engine.compute_height_above_floor(
                            [x3d, y3d, z3d])
                else:
                    x3d = y3d = z3d = 0.0

                court_u, court_v = pixel_to_court(H_court, u, v)
                last_top_court   = (court_u, court_v)

                court_u_geo, court_v_geo = court_u, court_v 
                if (geo_height_top > 0.04
                        and geo_engine.plane_normal is not None
                        and dist > GEO_MIN_DIST):
                    gnd_pt = geo_engine.get_corrected_court_point(
                        u, v, intr_top, geo_height_top)
                    if gnd_pt is not None:
                        gnd_px = geo_engine.project_3d_to_pixel(gnd_pt, intr_top)
                        if gnd_px is not None:
                            cu_g, cv_g = pixel_to_court(H_court, gnd_px[0], gnd_px[1])
                            if 0.0 <= cu_g <= 1.0 and 0.0 <= cv_g <= 1.0:
                                court_u_geo, court_v_geo = cu_g, cv_g

                if pipe_side and side_tracker and side_desc and side_h >= 0:
                    if side_h <= side_vly:
                        valley_court = (court_u_geo, court_v_geo)

                # ── EXCLUSIVITÉ TOP CAM POUR LE IN / OUT ──────────────────────
                is_in_fused = is_in_top

                if side_bounce:
                    bounce_persist = BOUNCE_PERSIST_N
                    if valley_court is not None:
                        bounce_court_u, bounce_court_v = valley_court
                        valley_court = None
                    else:
                        bounce_court_u, bounce_court_v = last_top_court
                    print(f"\r  >>> REBOND {'IN' if is_in_fused else 'OUT'} <<<  court=({court_u:.2f},{court_v:.2f})  h={side_h:.3f}m   ")

                bounce_to_send = (bounce_persist > 0)
                if bounce_persist > 0:
                    bounce_persist -= 1

                vote_sol = 0
                vote_vol = 0

                # Source A : SIDE visuel
                if side_tracker:
                    side_visual = side_tracker.is_ball_on_ground_visual(height_m=side_h)
                    if side_visual is True:
                        vote_sol += 2
                    elif side_visual is False:
                        vote_vol += 1
                else:
                    side_visual = None

                # Source B : ratio taille
                expected_r = geo_engine.expected_radius_if_on_ground(u, v, intr_top) if geo_engine.plane_normal is not None else 0.0
                radius_ratio = (top_radius / expected_r) if expected_r > 0 else 0.0
                if radius_ratio > 0:
                    if   radius_ratio <= 1.20: vote_sol += 1
                    elif radius_ratio >= 1.50: vote_vol += 1

                # Source C : hauteur géométrique TOP
                if geo_height_top >= 0.0:
                    if   geo_height_top <  GEO_ON_GND_M: vote_sol += 1
                    elif geo_height_top >  GEO_IN_AIR_M: vote_vol += 1

                # Source D : hauteur SIDE 
                if side_visual is None and side_tracker and side_h >= 0:
                    if   side_h <= side_tracker.get_ground_thresh(): vote_sol += 1
                    else:                                            vote_vol += 1
                elif not side_tracker:
                    if dist_top > 0 and floor_state["floor_depth_top"] > 0:
                        if floor_state["floor_depth_top"] - dist_top <= 0.12:
                            vote_sol += 1
                        else:
                            vote_vol += 1

                on_gnd_raw = (vote_vol <= vote_sol)

                ground_hist.append(on_gnd_raw)
                on_ground_fused = sum(ground_hist) > len(ground_hist) // 2

                verdict_str = "IN " if is_in_fused else "OUT"
                gnd_str     = ("SOL" if on_ground_fused else f"VOL {side_h*100:.0f}cm")
                
                vis_tag  = ("VIS:SOL" if side_visual is True
                             else ("VIS:VOL" if side_visual is False else "VIS:?"))
                geo_str  = (f"geo:{geo_height_top*100:.0f}cm r:{radius_ratio:.2f}"
                            if geo_height_top >= 0 else "geo:--")
                            
                print(f"\r{verdict_str}  {gnd_str}  {geo_str}  {vis_tag}"
                      f"  [s:{vote_sol}v:{vote_vol}]"
                      f"  court=({court_u:.2f},{court_v:.2f})   ", end="")

                payload = {
                    "ball_detected":  True,
                    "x":              x3d,
                    "y":              y3d,
                    "z":              z3d,
                    "u":              u,
                    "v":              v,
                    "radius":         result["radius"],
                    "is_in":          is_in_fused,
                    "court_u":        court_u,
                    "court_v":        court_v,
                    "court_u_geo":    round(court_u_geo, 4),
                    "court_v_geo":    round(court_v_geo, 4),
                    "height_m":       round(side_h, 4) if side_h >= 0 else -1.0,
                    "height_geo_top": round(geo_height_top, 4),
                    "on_ground":      on_ground_fused,
                    "bounce":         bounce_to_send,
                    "bounce_court_u": bounce_court_u if bounce_to_send else -1.0,
                    "bounce_court_v": bounce_court_v if bounce_to_send else -1.0,
                }

            else:
                payload = {
                    "ball_detected":  False,
                    "x": 0.0, "y": 0.0, "z": 0.0,
                    "u": 0,   "v": 0,   "radius": 0,
                    "is_in":          True,
                    "court_u":        -1.0,
                    "court_v":        -1.0,
                    "court_u_geo":    -1.0,
                    "court_v_geo":    -1.0,
                    "height_m":       -1.0,
                    "height_geo_top": -1.0,
                    "on_ground":      False,
                    "bounce":         False,
                    "bounce_court_u": -1.0,
                    "bounce_court_v": -1.0,
                }

            server.send(payload)

    finally:
        pipe_top.stop()
        if pipe_side:
            pipe_side.stop()
        print("\nCameras arretees.")

if __name__ == "__main__":
    main()