using System.Drawing;

namespace DEMOREALSENSE
{
    /// <summary>
    /// Résultat produit par CameraPipeline à chaque frame.
    /// Transmis au thread UI pour l'affichage.
    /// </summary>
    public sealed class FrameResult
    {
        /// <summary>False si la caméra n'a pas fourni de frame (timeout ou arrêt).</summary>
        public bool HasFrame { get; set; }

        /// <summary>Bitmap prêt à afficher — issu du double-buffer de CameraPipeline.</summary>
        public Bitmap? BitmapToShow { get; set; }

        /// <summary>False si le tracker manuel a perdu la cible cette frame.</summary>
        public bool ManualTrackingOk { get; set; } = true;

        /// <summary>Profondeur brute au centre de la balle (unités capteur).</summary>
        public ushort RawDepth { get; set; }

        /// <summary>Facteur de conversion : RawDepth × DepthUnits = mètres.</summary>
        public float DepthUnits { get; set; }

        /// <summary>Temps de traitement total de la frame en millisecondes.</summary>
        public double FrameMs { get; set; }

        /// <summary>Timestamp UTC de la frame (DateTime.UtcNow.Ticks).</summary>
        public long NowTicks { get; set; }

        // ── IN / OUT ─────────────────────────────────────────────────────

        /// <summary>Latch IN/OUT courant (maintenu 5s après un OUT).</summary>
        public InOutLatch Latch { get; set; } = new InOutLatch();

        /// <summary>Verdict live calculé cette frame (Unknown si pas de ligne).</summary>
        public InOutSide LiveSide { get; set; } = InOutSide.Unknown;

        /// <summary>True si le verdict OUT est maintenu après le rebond.</summary>
        public bool VerdictHeld { get; set; }

        /// <summary>Timestamp du dernier verdict OUT (pour afficher le compte à rebours).</summary>
        public long VerdictHeldTicks { get; set; }
    }
}
