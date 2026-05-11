import numpy as np
import json
import os

class GeometryEngine:
    def __init__(self, corners_json="terrain_corners.json", ball_radius_m=0.037):
        """ ball_radius_m = 0.037 correspond à une balle de pickleball (74mm de diam) """
        self.ball_radius_m = ball_radius_m
        self.plane_normal = None
        self.plane_p0 = None
        self._load_plane(corners_json)

    def _load_plane(self, corners_json):
        if not os.path.exists(corners_json):
            print("[Geometry] ⚠ Fichier corners introuvable. Géométrie désactivée.")
            return
        
        with open(corners_json) as f:
            data = json.load(f)
            
        if "corners" not in data or len(data["corners"]) < 3:
            return

        # Récupération des 3 premiers points 3D pour définir le plan
        p1 = np.array([data["corners"][0]["x"], data["corners"][0]["y"], data["corners"][0]["z"]])
        p2 = np.array([data["corners"][1]["x"], data["corners"][1]["y"], data["corners"][1]["z"]])
        p3 = np.array([data["corners"][2]["x"], data["corners"][2]["y"], data["corners"][2]["z"]])

        # Calcul du vecteur normal (perpendiculaire au sol)
        v1 = p2 - p1
        v2 = p3 - p1
        normal = np.cross(v1, v2)
        normal = normal / np.linalg.norm(normal)

        # On s'assure que la normale pointe vers le haut (vers la caméra)
        # Dans le système RealSense, Z positif = s'éloigne de la caméra.
        # Donc la normale pointant vers la caméra doit avoir un Z négatif.
        if normal[2] > 0:
            normal = -normal

        self.plane_normal = normal
        self.plane_p0 = p1
        print(f"[Geometry] Plan du sol modélisé avec succès.")

    def get_ray_direction(self, u, v, intrinsics):
        """ Convertit un pixel (u,v) en un rayon 3D normalisé partant de la caméra """
        dx = (u - intrinsics.ppx) / intrinsics.fx
        dy = (v - intrinsics.ppy) / intrinsics.fy
        dz = 1.0
        ray = np.array([dx, dy, dz])
        return ray / np.linalg.norm(ray)

    def triangulate_3d_position(self, u, v, intrinsics, height_from_side_m=0.0):
        """ 
        Calcule la position 3D exacte (x,y,z) en combinant le pixel de la TOP 
        et la hauteur calculée par la SIDE.
        """
        if self.plane_normal is None:
            return None

        ray = self.get_ray_direction(u, v, intrinsics)
        
        # On cherche la distance 't' sur le rayon pour que la distance 
        # entre ce point et le sol soit égale à 'height_from_side_m'
        denom = np.dot(self.plane_normal, ray)
        if abs(denom) < 1e-6:
            return None # Le rayon est parallèle au sol (impossible en pratique)

        # Formule d'intersection Rayon - Plan avec décalage de hauteur
        t = (height_from_side_m + np.dot(self.plane_normal, self.plane_p0)) / denom
        
        if t < 0:
            return None # La balle serait derrière la caméra

        return t * ray # Retourne [X, Y, Z] 

    def expected_radius_if_on_ground(self, u, v, intrinsics):
        """ 
        Calcule la taille (en pixels) que la balle DEVRAIT avoir 
        si elle était exactement posée au sol à cet endroit précis.
        """
        p_ground = self.triangulate_3d_position(u, v, intrinsics, height_from_side_m=0.0)
        if p_ground is None:
            return 30 # Fallback

        distance = np.linalg.norm(p_ground)
        focal_avg = (intrinsics.fx + intrinsics.fy) / 2.0
        
        # R_pixel = (Focale * Rayon_Reel) / Distance
        expected_r_px = (focal_avg * self.ball_radius_m) / distance
        return expected_r_px

    # ══════════════════════════════════════════════════════════════════════════
    #  NOUVELLES MÉTHODES — Géométrie angulaire TOP cam
    # ══════════════════════════════════════════════════════════════════════════

    def compute_height_above_floor(self, point_3d):
        """
        Calcule la hauteur géométrique d'un point 3D réel au-dessus du plan du sol calibré.

        Principe : on projette le vecteur (P - p0) sur la normale du plan.
        La normale pointe vers la caméra (Z négatif en repère RealSense).
        Un point au-dessus du sol (entre la caméra et le sol) donne une valeur > 0.

        Args:
            point_3d : [x, y, z] en mètres (obtenu via rs2_deproject)

        Returns:
            float : hauteur en mètres (>0 = en l'air, 0 = au sol),
                    ou -1.0 si le plan n'est pas calibré / point invalide.
        """
        if self.plane_normal is None or self.plane_p0 is None:
            return -1.0

        p = np.asarray(point_3d, dtype=float)
        # Point trop proche de l'origine = profondeur invalide
        if np.linalg.norm(p) < 0.05:
            return -1.0

        # Projection signée sur la normale
        # normale pointe vers cam → dot > 0 quand P est au-dessus du sol
        h = float(np.dot(self.plane_normal, p - self.plane_p0))
        return round(max(0.0, h), 4)

    def get_corrected_court_point(self, u, v, intrinsics, height_m):
        """
        Retourne le point 3D **au sol directement sous la balle**, corrigé
        de la parallaxe introduite par l'angle de la caméra TOP.

        Quand la balle est en l'air à height_m, son pixel apparent (u,v) est
        décalé par rapport à sa vraie position au sol (effet de perspective).
        Cette méthode calcule la vraie projection au sol :
          1. Intersection du rayon (u,v) avec le plan à hauteur height_m
             → position 3D réelle de la balle
          2. Translation de -height_m le long de la normale
             → point sur le plan du sol directement sous la balle

        Args:
            u, v      : pixel de la balle dans la TOP cam
            intrinsics: intrinsèques de la TOP cam (RealSense)
            height_m  : hauteur de la balle au-dessus du sol (m)

        Returns:
            ndarray [x,y,z] du point au sol, ou None si indisponible.
        """
        if self.plane_normal is None:
            return None

        ball_3d = self.triangulate_3d_position(u, v, intrinsics, height_m)
        if ball_3d is None:
            return None

        # Projeter vers le sol : reculer le long de la normale de height_m
        # (normale pointe vers la caméra → on soustrait pour aller vers le sol)
        ground_point = ball_3d - height_m * self.plane_normal
        return ground_point

    def project_3d_to_pixel(self, point_3d, intrinsics):
        """
        Projette un point 3D [x,y,z] en coordonnées pixel (u,v) via pinhole.
        Retourne (u, v) entiers, ou None si z <= 0.
        """
        x, y, z = float(point_3d[0]), float(point_3d[1]), float(point_3d[2])
        if z <= 0.01:
            return None
        u = intrinsics.fx * x / z + intrinsics.ppx
        v = intrinsics.fy * y / z + intrinsics.ppy
        return int(round(u)), int(round(v))