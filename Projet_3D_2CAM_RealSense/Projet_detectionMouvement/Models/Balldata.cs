// ============================================================
//  BallData.cs  v6 — Ajout correction parallaxe (geo)
// ============================================================

public class BallData
{
    public bool ball_detected { get; set; }
    public float x { get; set; }
    public float y { get; set; }
    public float z { get; set; }
    public int u { get; set; }
    public int v { get; set; }
    public int radius { get; set; }

    // Position TOP (IN/OUT + court_u/v brute)
    public bool is_in { get; set; } = true;
    public float court_u { get; set; } = -1f;
    public float court_v { get; set; } = -1f;

    // Position TOP corrigée (vraie projection au sol sans la parallaxe)
    public float court_u_geo { get; set; } = -1f;
    public float court_v_geo { get; set; } = -1f;

    // Hauteur SIDE
    public float height_m { get; set; } = -1f;
    public bool on_ground { get; set; } = true;

    // Rebond
    public bool bounce { get; set; } = false;
    public float bounce_court_u { get; set; } = -1f;
    public float bounce_court_v { get; set; } = -1f;
}