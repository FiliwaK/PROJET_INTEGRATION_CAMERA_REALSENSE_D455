// ============================================================
//  HandData.cs  —  Modèles de données reçus depuis Python
//
//  Rôle : ces classes C# représentent exactement la structure
//         JSON envoyée par Python via la socket TCP.
//         Newtonsoft.Json désérialise le JSON reçu directement
//         dans ces objets (les noms de propriétés doivent
//         correspondre aux clés du dict Python).
//
//  Structure JSON attendue :
//  {
//    "hand_detected": true,
//    "body_detected": false,
//    "hands": [ [ {x, y}, ... ], ... ],   ← liste de mains
//    "body":  [ {x, y, visibility}, ... ] ← 33 points du corps
//  }
// ============================================================

/// <summary>
/// Représente une trame complète reçue du serveur Python.
/// Contient les données de détection des mains ET du corps.
/// </summary>
public class HandData
{
    /// <summary>
    /// true si au moins une main est visible dans l'image.
    /// Utilisé par le ViewModel pour afficher le statut "main(s) détectée(s)".
    /// </summary>
    public bool hand_detected { get; set; }

    /// <summary>
    /// true si un corps humain est visible dans l'image.
    /// Utilisé par le ViewModel pour afficher le statut "corps détecté".
    /// </summary>
    public bool body_detected { get; set; }

    /// <summary>
    /// Liste des mains détectées (0, 1 ou 2 mains).
    /// Chaque main est une liste de 21 points (landmarks MediaPipe Hands).
    /// Les coordonnées x/y sont normalisées entre 0.0 et 1.0.
    /// </summary>
    public List<List<PointModel>> hands { get; set; } = new();

    /// <summary>
    /// Liste des 33 points du squelette du corps (landmarks MediaPipe Pose).
    /// Vide si aucun corps n'est détecté.
    /// </summary>
    public List<PointModel> body { get; set; } = new();
}

/// <summary>
/// Représente un point (landmark) retourné par MediaPipe.
/// Utilisé pour les mains (21 points) et le corps (33 points).
///
/// Coordonnées normalisées [0.0 – 1.0] relatives à la taille de l'image.
/// Pour afficher sur un canvas 640×480 : x * 640, y * 480.
/// </summary>
public class PointModel
{
    /// <summary>Position horizontale normalisée (0 = gauche, 1 = droite).</summary>
    public float x { get; set; }

    /// <summary>Position verticale normalisée (0 = haut, 1 = bas).</summary>
    public float y { get; set; }

    /// <summary>
    /// Score de visibilité du point (0.0 à 1.0).
    /// Utilisé uniquement pour les points du corps (MediaPipe Pose).
    /// Un point avec visibility < 0.5 est hors champ ou occulté
    /// → on ne le dessine pas côté C#.
    /// Toujours 0 pour les points des mains (MediaPipe Hands ne le fournit pas).
    /// </summary>
    public float visibility { get; set; }
}
