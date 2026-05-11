// ============================================================
//  PowerPointService.cs  —  Contrôle PowerPoint via COM Interop
//
//  FONCTIONNEMENT :
//    1. Ouvre le fichier .pptx en arrière-plan (PowerPoint invisible)
//    2. Exporte chaque diapositive en PNG 1280×720 dans un dossier temp
//    3. Le WPF affiche ces images directement → contrôle total sur
//       les transitions 3D, le zoom, l'opacité, etc.
//
//  AVANTAGE vs fenêtre PPT externe :
//    • La fenêtre PowerPoint n'est jamais visible
//    • Le flux caméra reste visible en arrière-plan
//    • Les transitions 3D WPF (PlaneProjection) sont possibles
//
//  PRÉ-REQUIS :
//    NuGet : Microsoft.Office.Interop.PowerPoint  (v15.x)
//    OU     référence COM → "Microsoft PowerPoint 16.0 Object Library"
//
//  NOTE : Microsoft PowerPoint doit être installé sur la machine.
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Office.Core;
using Microsoft.Office.Interop.PowerPoint;

public class PowerPointService : IDisposable
{
    // ── Objets COM (libérés dans Dispose) ────────────────────────────────────
    private Application? _app;
    private Presentation? _pres;

    // ── Dossier temporaire pour les PNG exportés ──────────────────────────────
    private string _tempFolder = string.Empty;

    // ── Propriétés publiques ──────────────────────────────────────────────────

    /// <summary>Nombre total de diapositives dans la présentation ouverte.</summary>
    public int SlideCount { get; private set; }

    /// <summary>
    /// Chemins vers les PNG exportés (slide_0001.png … slide_NNNN.png).
    /// Même ordre que les diapositives PowerPoint.
    /// </summary>
    public List<string> SlideImagePaths { get; } = new();

    /// <summary>True si une présentation est actuellement ouverte.</summary>
    public bool IsOpen => _pres != null;

    // ── Ouverture et export ───────────────────────────────────────────────────

    /// <summary>
    /// Ouvre le fichier PowerPoint, exporte toutes les diapositives en PNG
    /// et remplit SlideImagePaths.
    ///
    /// À appeler depuis un thread de fond (Task.Run) car l'export peut
    /// prendre plusieurs secondes selon le nombre de diapositives.
    ///
    /// Retourne true si succès, false si PowerPoint n'est pas installé
    /// ou si le fichier est invalide.
    /// </summary>
    public bool Open(string filePath)
    {
        try
        {
            // Lance PowerPoint en arrière-plan (fenêtre cachée)
            _app = new Application();

            // WithWindow = msoFalse → PowerPoint reste INVISIBLE
            // ReadOnly  = msoTrue  → pas de verrouillage du fichier
            _pres = _app.Presentations.Open(
                filePath,
                ReadOnly: MsoTriState.msoTrue,
                Untitled: MsoTriState.msoFalse,
                WithWindow: MsoTriState.msoFalse
            );

            SlideCount = _pres.Slides.Count;
            if (SlideCount == 0) return false;

            // Crée un dossier temporaire unique pour cette session
            _tempFolder = Path.Combine(
                Path.GetTempPath(),
                "ppt_" + Guid.NewGuid().ToString("N")[..8]
            );
            Directory.CreateDirectory(_tempFolder);

            // ── Export individuel de chaque diapositive ───────────────────────
            //
            //  On exporte slide par slide (pas Presentation.Export global)
            //  pour avoir un nom de fichier prévisible et trié correctement.
            //  1280×720 = bon compromis qualité / vitesse de chargement WPF.
            //
            SlideImagePaths.Clear();
            for (int i = 1; i <= SlideCount; i++)
            {
                string path = Path.Combine(_tempFolder, $"slide_{i:D4}.png");
                _pres.Slides[i].Export(path, "PNG", 1280, 720);
                SlideImagePaths.Add(path);
            }

            return SlideImagePaths.Count == SlideCount;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PowerPointService] Erreur ouverture : {ex.Message}");
            return false;
        }
    }

    // ── Fermeture propre ─────────────────────────────────────────────────────

    /// <summary>
    /// Ferme PowerPoint, libère les objets COM et supprime les PNG temporaires.
    /// </summary>
    public void Close()
    {
        try { _pres?.Close(); } catch { }
        _pres = null;

        if (_app != null)
        {
            try { _app.Quit(); } catch { }
            try { Marshal.ReleaseComObject(_app); } catch { }
            _app = null;
        }

        // Nettoyage des PNG temporaires
        if (Directory.Exists(_tempFolder))
            try { Directory.Delete(_tempFolder, recursive: true); } catch { }

        SlideImagePaths.Clear();
        SlideCount = 0;
        _tempFolder = string.Empty;
    }

    public void Dispose() => Close();
}