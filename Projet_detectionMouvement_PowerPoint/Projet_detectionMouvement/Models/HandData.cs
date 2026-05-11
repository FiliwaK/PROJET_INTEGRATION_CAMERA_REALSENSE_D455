// ============================================================
//  HandData.cs  —  Modèles de données reçus depuis Python
//
//  NOUVEAUTÉ v2 : champ "pinch_scales"
//    pinch_scales[i] = distance normalisée pouce↔index de la main i
//    Valeur ≈ 0.10 (doigts collés) à ≈ 0.60 (doigts écartés).
//    Utilisé en C# pour calculer la variation de zoom (deux mains).
//
//  Structure JSON complète attendue :
//  {
//    "hand_detected":  true,
//    "body_detected":  false,
//    "hands":          [ [{x,y}×21], [{x,y}×21] ],
//    "body":           [],
//    "gestures":       [ "pointing", "open" ],
//    "pinch_scales":   [ 0.42, 0.18 ]           ← NOUVEAU
//  }
// ============================================================

/// <summary>
/// Trame complète reçue du serveur Python à chaque image de la caméra.
/// </summary>
public class HandData
{
    /// <summary>Au moins une main visible dans l'image.</summary>
    public bool hand_detected { get; set; }

    /// <summary>Un corps humain visible dans l'image (toujours false, corps supprimé).</summary>
    public bool body_detected { get; set; }

    /// <summary>
    /// Liste des mains détectées (0, 1 ou 2).
    /// Chaque main = liste de 21 PointModel, coordonnées normalisées [0–1].
    /// L'index i de cette liste correspond à gestures[i] et pinch_scales[i].
    /// </summary>
    public List<List<PointModel>> hands { get; set; } = new();

    /// <summary>
    /// 33 points du squelette corps (MediaPipe Pose).
    /// Toujours vide dans cette version (corps supprimé pour performances).
    /// </summary>
    public List<PointModel> body { get; set; } = new();

    /// <summary>
    /// Geste classifié de chaque main, même ordre que 'hands'.
    ///   "pointing"    → index levé       → dessine / laser pointer
    ///   "two_fingers" → V doigts         → sélection palette
    ///   "open"        → 4 doigts levés   → neutre / quitter PPT
    ///   "other"       → autre            → rien
    /// </summary>
    public List<string> gestures { get; set; } = new();

    /// <summary>
    /// NOUVEAU — Distance normalisée pouce↔index pour chaque main.
    /// Même ordre que 'hands' et 'gestures'.
    ///
    /// Utilisé par GestureRecognizer pour le zoom deux mains :
    ///   On compare la distance ENTRE LES DEUX POIGNETS (pas ce champ)
    ///   pour le zoom, mais ce champ peut servir à détecter un pinch
    ///   d'une seule main (ex: mettre en pause la présentation).
    ///
    /// Valeurs : ≈ 0.10 (doigts collés) → ≈ 0.60 (doigts très écartés)
    /// </summary>
    public List<float> pinch_scales { get; set; } = new();
}

/// <summary>
/// Un point (landmark) MediaPipe.
/// Coordonnées normalisées [0–1] : multiplier par 640/480 pour des pixels canvas.
/// </summary>
public class PointModel
{
    /// <summary>Position horizontale (0 = gauche, 1 = droite) — déjà corrigé miroir.</summary>
    public float x { get; set; }

    /// <summary>Position verticale (0 = haut, 1 = bas).</summary>
    public float y { get; set; }

    /// <summary>
    /// Score de visibilité [0–1] (uniquement pour les points du corps).
    /// visibility &lt; 0.5 → point hors champ ou caché → à ignorer.
    /// </summary>
    public float visibility { get; set; }
}