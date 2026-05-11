// ============================================================
//  HandData.cs  —  Modèles de données reçus depuis Python
//
//  NOUVEAUTÉ : ajout du champ "gestures"
//    gestures[i] contient le geste de la main i (même index que hands[i])
//    Valeurs possibles : "pointing" | "open" | "fist"
//
//  Structure JSON complète attendue :
//  {
//    "hand_detected": true,
//    "body_detected": false,
//    "hands":    [ [{x,y}×21], [{x,y}×21] ],
//    "body":     [ {x,y,visibility}×33 ],
//    "gestures": [ "pointing", "open" ]   ← NOUVEAU
//  }
// ============================================================

/// <summary>
/// Trame complète reçue du serveur Python à chaque image de la caméra.
/// </summary>
public class HandData
{
    /// <summary>Au moins une main visible dans l'image.</summary>
    public bool hand_detected { get; set; }

    /// <summary>Un corps humain visible dans l'image.</summary>
    public bool body_detected { get; set; }

    /// <summary>
    /// Liste des mains détectées (0, 1 ou 2).
    /// Chaque main = liste de 21 PointModel, coordonnées normalisées [0–1].
    /// L'index i de cette liste correspond à gestures[i].
    /// </summary>
    public List<List<PointModel>> hands { get; set; } = new();

    /// <summary>
    /// 33 points du squelette du corps (MediaPipe Pose).
    /// Vide si aucun corps détecté.
    /// </summary>
    public List<PointModel> body { get; set; } = new();

    /// <summary>
    /// NOUVEAU — Geste classifié de chaque main, même ordre que 'hands'.
    /// Valeurs : "pointing" (index levé → dessine)
    ///           "open"     (4 doigts levés → survol palette / arrêt)
    ///           "fist"     (poing ou autre → neutre)
    ///
    /// Règle spéciale : si gestures == ["open","open"] → les deux mains
    /// sont ouvertes → C# passe en mode "version finale".
    /// </summary>
    public List<string> gestures { get; set; } = new();
}

/// <summary>
/// Un point (landmark) MediaPipe.
/// Coordonnées normalisées [0–1] : multiplier par 640/480 pour obtenir des pixels.
/// </summary>
public class PointModel
{
    /// <summary>Position horizontale (0 = gauche, 1 = droite).</summary>
    public float x { get; set; }

    /// <summary>Position verticale (0 = haut, 1 = bas).</summary>
    public float y { get; set; }

    /// <summary>
    /// Score de visibilité [0–1] (seulement pour les points du corps).
    /// Si visibility &lt; 0.5 → point hors champ ou caché → on ne le dessine pas.
    /// </summary>
    public float visibility { get; set; }
}
