# ============================================================
#  terrain_calibrator.py  —  4 coins du terrain uniquement
#
#  Clique les 4 coins dans l'ordre :
#    1-HautGauche  2-HautDroite  3-BasDroite  4-BasGauche
#
#  Sauvegarde dans terrain_corners.json :
#    "corners" : positions 3D réelles en mètres (pour WPF)
#    "pixels"  : positions 2D en pixels (pour ball_detector)
# ============================================================

import pyrealsense2 as rs
import numpy as np
import cv2
import json

OUTPUT_JSON   = "terrain_corners.json"
CORNER_LABELS = ["1-HautGauche", "2-HautDroite", "3-BasDroite", "4-BasGauche"]
CORNER_COLORS = [(0, 255, 255), (0, 200, 255), (0, 100, 255), (0, 50, 200)]

corners_2d = []
corners_3d = []
last_depth  = None
intrinsics  = None


def get_depth_median(depth_frame, u, v, r=3):
    depths = []
    for du in range(-r, r + 1):
        for dv in range(-r, r + 1):
            d = depth_frame.get_distance(
                max(0, min(u + du, 639)),
                max(0, min(v + dv, 479)))
            if d > 0.1:
                depths.append(d)
    return float(np.median(depths)) if depths else 0.0


def pixel_to_3d(u, v, depth_frame):
    dist = get_depth_median(depth_frame, u, v)
    if dist <= 0:
        return None
    pt = rs.rs2_deproject_pixel_to_point(intrinsics, [float(u), float(v)], dist)
    return {"x": round(pt[0], 4), "y": round(pt[1], 4), "z": round(pt[2], 4)}


def on_click(event, x, y, flags, param):
    global corners_2d, corners_3d, last_depth
    if event != cv2.EVENT_LBUTTONDOWN or len(corners_2d) >= 4 or last_depth is None:
        return

    pt3d = pixel_to_3d(x, y, last_depth)
    if pt3d is None:
        print(f"  ⚠ Profondeur indisponible en ({x},{y})")
        return

    corners_2d.append([x, y])
    corners_3d.append(pt3d)
    i = len(corners_2d) - 1
    print(f"  {CORNER_LABELS[i]} → pixel=({x},{y})  "
          f"X={pt3d['x']:+.3f}m Y={pt3d['y']:+.3f}m Z={pt3d['z']:.3f}m")

    if len(corners_2d) == 4:
        data = {"corners": corners_3d, "pixels": corners_2d}
        with open(OUTPUT_JSON, "w") as f:
            json.dump(data, f, indent=2)
        print(f"\n  ✅ Sauvegardé dans {OUTPUT_JSON}\n")


def draw_overlay(img):
    vis = img.copy()
    h   = vis.shape[0]

    for i, (px, py) in enumerate(corners_2d):
        cv2.circle(vis, (px, py), 8, CORNER_COLORS[i], -1)
        cv2.circle(vis, (px, py), 10, (255, 255, 255), 1)
        cv2.putText(vis, CORNER_LABELS[i], (px + 12, py - 8),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.5, CORNER_COLORS[i], 1)

    if len(corners_2d) >= 2:
        pts = np.array(corners_2d, dtype=np.int32)
        cv2.polylines(vis, [pts], isClosed=(len(corners_2d) == 4),
                      color=(0, 255, 0), thickness=2)

    if len(corners_2d) == 4:
        pts = np.array(corners_2d, dtype=np.int32)
        overlay = vis.copy()
        cv2.fillPoly(overlay, [pts], (0, 180, 0))
        vis = cv2.addWeighted(overlay, 0.2, vis, 0.8, 0)
        cv2.putText(vis, "OK — appuie Q pour quitter",
                    (10, h - 40), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 255, 0), 2)
    else:
        remaining = 4 - len(corners_2d)
        cv2.putText(vis,
                    f"Clique: {CORNER_LABELS[len(corners_2d)]}  ({remaining} restant)",
                    (10, 30), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 255, 255), 2)

    cv2.putText(vis, "R=Reset  Q=Quitter",
                (10, h - 10), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (200, 200, 0), 1)
    return vis


def main():
    global last_depth, intrinsics, corners_2d, corners_3d

    print("=" * 55)
    print("  Calibration terrain — clique les 4 coins dans l'ordre")
    for l in CORNER_LABELS:
        print(f"    {l}")
    print("=" * 55)

    pipeline  = rs.pipeline()
    cfg       = rs.config()
    cfg.enable_stream(rs.stream.color, 640, 480, rs.format.bgr8, 30)
    cfg.enable_stream(rs.stream.depth, 640, 480, rs.format.z16,  30)
    profile   = pipeline.start(cfg)
    align     = rs.align(rs.stream.color)
    intrinsics = (profile.get_stream(rs.stream.color)
                  .as_video_stream_profile().get_intrinsics())

    cv2.namedWindow("Calibration Terrain")
    cv2.setMouseCallback("Calibration Terrain", on_click)

    try:
        while True:
            frames      = pipeline.wait_for_frames()
            aligned     = align.process(frames)
            color_frame = aligned.get_color_frame()
            depth_frame = aligned.get_depth_frame()
            if not color_frame or not depth_frame:
                continue

            last_depth = depth_frame
            img = np.asanyarray(color_frame.get_data())
            cv2.imshow("Calibration Terrain", draw_overlay(img))

            key = cv2.waitKey(1) & 0xFF
            if key == ord('r'):
                corners_2d, corners_3d = [], []
                print("  Réinitialisé.")
            elif key in (ord('q'), 27):
                break
    finally:
        pipeline.stop()
        cv2.destroyAllWindows()


if __name__ == "__main__":
    main()