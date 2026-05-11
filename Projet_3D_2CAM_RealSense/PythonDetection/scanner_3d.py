# ============================================================
#  scanner_3d.py  —  Scanner de terrain (caméra FIXE, optimisé)
#
#  Utilisation correcte :
#    1. Pose la D455 sur son support, pointée vers le terrain
#    2. Lance ce script
#    3. Attends ~30 secondes que le nuage se stabilise
#    4. Appuie sur S pour sauvegarder
#
#  Optimisations vs version précédente :
#    - On ne traite qu'1 frame sur SKIP_FRAMES (réduit la charge CPU)
#    - Le downsampling se fait toutes les DOWNSAMPLE_EVERY frames
#    - L'affichage Open3D se met à jour toutes les DISPLAY_EVERY frames
#    - Un compteur s'affiche directement dans la vue caméra
# ============================================================

import pyrealsense2 as rs
import numpy as np
import open3d as o3d
import cv2
import os

# ── Paramètres de performance ─────────────────────────────────────────────────

SKIP_FRAMES      = 3    # traite 1 frame sur 3 (réduit la charge CPU)
DOWNSAMPLE_EVERY = 20   # downsampling tous les 20 frames traités
DISPLAY_EVERY    = 10   # mise à jour Open3D tous les 10 frames traités

VOXEL_SIZE = 0.01       # résolution du scan en mètres (1cm)
MAX_DEPTH  = 4.0        # ignore ce qui est plus loin (mètres)

OUTPUT_PLY = "terrain_scan.ply"
OUTPUT_OBJ = "terrain_scan.obj"


def save_scan(pcd):
    if len(pcd.points) == 0:
        print("\n  Rien à sauvegarder.")
        return

    pcd_clean = pcd.voxel_down_sample(VOXEL_SIZE)
    print(f"\n  Sauvegarde — {len(pcd_clean.points):,} points...")

    o3d.io.write_point_cloud(OUTPUT_PLY, pcd_clean)
    print(f"  ✓  {OUTPUT_PLY}")

    pcd_clean.estimate_normals(
        search_param=o3d.geometry.KDTreeSearchParamHybrid(radius=0.05, max_nn=30)
    )
    pcd_clean.orient_normals_towards_camera_location()

    try:
        mesh, densities = o3d.geometry.TriangleMesh \
            .create_from_point_cloud_poisson(pcd_clean, depth=8)
        thresh = np.quantile(np.asarray(densities), 0.1)
        mask   = np.asarray(densities) > thresh
        mesh.remove_vertices_by_mask(~mask)
        mesh.compute_vertex_normals()
        o3d.io.write_triangle_mesh(OUTPUT_OBJ, mesh)
        print(f"  ✓  {OUTPUT_OBJ}")
    except Exception as e:
        print(f"  ⚠  OBJ échoué : {e}  (le PLY reste utilisable)")

    print("  Sauvegarde terminée.\n")


def main():
    print("=" * 50)
    print("  Scanner 3D — Caméra fixe")
    print("  Pose la D455 et attends ~30s")
    print("  S = Sauvegarder | R = Reset | Q = Quitter")
    print("=" * 50)

    # ── Caméra ────────────────────────────────────────────────────────────────
    pipeline = rs.pipeline()
    cfg      = rs.config()
    cfg.enable_stream(rs.stream.color, 640, 480, rs.format.bgr8, 30)
    cfg.enable_stream(rs.stream.depth, 640, 480, rs.format.z16,  30)
    profile  = pipeline.start(cfg)

    align = rs.align(rs.stream.color)
    intr  = profile.get_stream(rs.stream.color) \
                   .as_video_stream_profile()    \
                   .get_intrinsics()
    pinhole = o3d.camera.PinholeCameraIntrinsic(
        intr.width, intr.height,
        intr.fx, intr.fy, intr.ppx, intr.ppy
    )

    # ── Visualiseur ───────────────────────────────────────────────────────────
    vis = o3d.visualization.Visualizer()
    vis.create_window("Scanner 3D — D455", width=800, height=500)
    pcd_display = o3d.geometry.PointCloud()
    geo_added   = False

    pcd_total     = o3d.geometry.PointCloud()
    raw_count     = 0
    treated_count = 0

    try:
        while True:
            frames = pipeline.wait_for_frames()
            raw_count += 1

            # ── Toujours rafraîchir Open3D même si on skip ───────────────────
            vis.poll_events()
            vis.update_renderer()

            # ── Skip frames ───────────────────────────────────────────────────
            if raw_count % SKIP_FRAMES != 0:
                key = cv2.waitKey(1) & 0xFF
                if key in (ord('q'), 27):
                    save_scan(pcd_total)
                    break
                continue

            treated_count += 1

            # ── Capture ───────────────────────────────────────────────────────
            aligned     = align.process(frames)
            color_frame = aligned.get_color_frame()
            depth_frame = aligned.get_depth_frame()
            if not color_frame or not depth_frame:
                continue

            color_img = np.asanyarray(color_frame.get_data())
            depth_img = np.asanyarray(depth_frame.get_data())

            # ── Nuage de cette frame ──────────────────────────────────────────
            o3d_color = o3d.geometry.Image(color_img[:, :, ::-1].copy())
            o3d_depth = o3d.geometry.Image(depth_img)

            rgbd = o3d.geometry.RGBDImage.create_from_color_and_depth(
                o3d_color, o3d_depth,
                depth_scale=1000.0,
                depth_trunc=MAX_DEPTH,
                convert_rgb_to_intensity=False
            )
            frame_pcd = o3d.geometry.PointCloud.create_from_rgbd_image(rgbd, pinhole)
            frame_pcd.transform([[1,0,0,0],[0,-1,0,0],[0,0,-1,0],[0,0,0,1]])

            # ── Accumulation ──────────────────────────────────────────────────
            pcd_total += frame_pcd

            if treated_count % DOWNSAMPLE_EVERY == 0:
                pcd_total = pcd_total.voxel_down_sample(VOXEL_SIZE)

            n_points = len(pcd_total.points)

            # ── Affichage Open3D (périodique) ─────────────────────────────────
            if treated_count % DISPLAY_EVERY == 0:
                pcd_display.points = pcd_total.points
                pcd_display.colors = pcd_total.colors
                if not geo_added:
                    vis.add_geometry(pcd_display)
                    geo_added = True
                else:
                    vis.update_geometry(pcd_display)

            # ── Vue caméra avec HUD ───────────────────────────────────────────
            hud = color_img.copy()
            cv2.putText(hud, f"Points: {n_points:,}",
                        (10, 30), cv2.FONT_HERSHEY_SIMPLEX,
                        0.7, (0, 255, 0), 2)
            cv2.putText(hud, f"Frames traites: {treated_count}",
                        (10, 60), cv2.FONT_HERSHEY_SIMPLEX,
                        0.7, (0, 255, 0), 2)
            cv2.putText(hud, "S=Sauv  R=Reset  Q=Quitter",
                        (10, color_img.shape[0] - 10),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.55, (255, 255, 0), 1)
            cv2.imshow("Vue camera", hud)

            # ── Clavier ───────────────────────────────────────────────────────
            key = cv2.waitKey(1) & 0xFF
            if key == ord('s'):
                save_scan(pcd_total)
            elif key == ord('r'):
                pcd_total.clear()
                treated_count = 0
                print("\n  Scan réinitialisé.")
            elif key in (ord('q'), 27):
                save_scan(pcd_total)
                break

    finally:
        pipeline.stop()
        vis.destroy_window()
        cv2.destroyAllWindows()


if __name__ == "__main__":
    main()