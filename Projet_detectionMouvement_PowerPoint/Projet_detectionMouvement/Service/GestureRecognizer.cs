using System;
using System.Collections.Generic;

public class GestureRecognizer
{
    // ════════════════════════════════════════════════════════════════════════
    //  CONSTANTES DE SENSIBILITÉ
    // ════════════════════════════════════════════════════════════════════════

    // Main Gauche (Swipe)
    private const float SWIPE_DELTA_MIN = 0.12f;
    private const double SWIPE_COOLDOWN_MS = 500;
    private const int SWIPE_HISTORY_MS = 250;
    private const int SWIPE_MIN_SAMPLES = 3;

    // Main Droite (Manipulation)
    private const double RIGHT_OPEN_HOLD_S = 3.0;
    private const float ZOOM_SENSITIVITY = 4.0f;
    private const float ZOOM_DEADZONE = 0.005f;

    // ════════════════════════════════════════════════════════════════════════
    //  ÉTATS INTERNES
    // ════════════════════════════════════════════════════════════════════════

    // Main Gauche
    private readonly Queue<(float x, long ms)> _leftWristHistory = new();
    private DateTime _lastSwipeAt = DateTime.MinValue;

    // Main Droite
    private DateTime _rightOpenSince = DateTime.MinValue;
    private bool _rightOpenFired = false;
    private float _prevRightX = -1f;
    private float _prevRightY = -1f;
    private float _prevRightPinch = -1f;

    // Arrêt global
    private DateTime _bothOpenSince = DateTime.MinValue;

    // ════════════════════════════════════════════════════════════════════════
    //  ÉVÉNEMENTS
    // ════════════════════════════════════════════════════════════════════════

    public event Action? SwipedRight;
    public event Action? SwipedLeft;

    public event Action? RightHandToggledSelection;
    public event Action<float, float>? RightHandMoved;
    public event Action<float>? RightHandZoomed;
    public event Action? BothHandsOpenHeld;

    // ════════════════════════════════════════════════════════════════════════
    //  MÉTHODE PRINCIPALE
    // ════════════════════════════════════════════════════════════════════════

    public void Update(HandData data)
    {
        long nowMs = DateTimeOffset.Now.ToUnixTimeMilliseconds();

        // ── 0. Quitter avec 2 mains ouvertes ──────────────────────────────────
        if (data.gestures.Count >= 2 && data.gestures[0] == "open" && data.gestures[1] == "open")
        {
            if (_bothOpenSince == DateTime.MinValue) _bothOpenSince = DateTime.Now;
            if ((DateTime.Now - _bothOpenSince).TotalSeconds >= 1.5) BothHandsOpenHeld?.Invoke();
            return; // Bloque les autres gestes
        }
        else _bothOpenSince = DateTime.MinValue;

        // ── 1. Identifier la main Gauche (X < 0.5) et Droite (X >= 0.5) ───────
        int leftIdx = -1, rightIdx = -1;

        if (data.hands.Count == 2)
        {
            if (data.hands[0][0].x < data.hands[1][0].x) { leftIdx = 0; rightIdx = 1; }
            else { leftIdx = 1; rightIdx = 0; }
        }
        else if (data.hands.Count == 1)
        {
            if (data.hands[0][0].x < 0.5f) leftIdx = 0;
            else rightIdx = 0;
        }

        // ── 2. MAIN GAUCHE : Gestion du Slide (Swipe) UNIQUEMENT SI OUVERTE ───
        if (leftIdx != -1)
        {
            string leftGesture = data.gestures.Count > leftIdx ? data.gestures[leftIdx] : "other";

            // Règle stricte : la main gauche doit être "open" pour agir sur les slides
            if (leftGesture == "open")
            {
                float wristX = data.hands[leftIdx][0].x;
                _leftWristHistory.Enqueue((wristX, nowMs));

                while (_leftWristHistory.Count > 0 && nowMs - _leftWristHistory.Peek().ms > SWIPE_HISTORY_MS)
                    _leftWristHistory.Dequeue();

                if (_leftWristHistory.Count >= SWIPE_MIN_SAMPLES && (DateTime.Now - _lastSwipeAt).TotalMilliseconds > SWIPE_COOLDOWN_MS)
                {
                    float dx = wristX - _leftWristHistory.Peek().x;
                    if (dx > SWIPE_DELTA_MIN) { SwipedRight?.Invoke(); _lastSwipeAt = DateTime.Now; _leftWristHistory.Clear(); }
                    else if (dx < -SWIPE_DELTA_MIN) { SwipedLeft?.Invoke(); _lastSwipeAt = DateTime.Now; _leftWristHistory.Clear(); }
                }
            }
            else
            {
                _leftWristHistory.Clear(); // On annule le geste si la main se ferme
            }
        }
        else _leftWristHistory.Clear();

        // ── 3. MAIN DROITE : Sélection, Déplacement, Zoom ─────────────────────
        if (rightIdx != -1)
        {
            string gesture = data.gestures.Count > rightIdx ? data.gestures[rightIdx] : "other";

            // A. Sélection (Main ouverte 3 secondes)
            if (gesture == "open")
            {
                if (_rightOpenSince == DateTime.MinValue) _rightOpenSince = DateTime.Now;

                if (!_rightOpenFired && (DateTime.Now - _rightOpenSince).TotalSeconds >= RIGHT_OPEN_HOLD_S)
                {
                    RightHandToggledSelection?.Invoke();
                    _rightOpenFired = true;
                }
            }
            else
            {
                _rightOpenSince = DateTime.MinValue;
                _rightOpenFired = false;
            }

            // B. Déplacement (Index seul "pointing")
            if (gesture == "pointing")
            {
                float cx = data.hands[rightIdx][8].x;
                float cy = data.hands[rightIdx][8].y;

                if (_prevRightX >= 0 && _prevRightY >= 0)
                {
                    float dx = cx - _prevRightX;
                    float dy = cy - _prevRightY;
                    RightHandMoved?.Invoke(dx, dy);
                }
                _prevRightX = cx;
                _prevRightY = cy;
            }
            else
            {
                _prevRightX = -1f;
                _prevRightY = -1f;
            }

            // C. Zoom (Pincement Pouce-Index)
            float currentPinch = data.pinch_scales.Count > rightIdx ? data.pinch_scales[rightIdx] : -1f;
            if (currentPinch > 0f)
            {
                if (_prevRightPinch > 0f)
                {
                    float rawDelta = currentPinch - _prevRightPinch;
                    if (MathF.Abs(rawDelta) > ZOOM_DEADZONE)
                    {
                        float zoomFactor = 1.0f + (rawDelta * ZOOM_SENSITIVITY);
                        zoomFactor = Math.Clamp(zoomFactor, 0.9f, 1.1f);
                        RightHandZoomed?.Invoke(zoomFactor);
                    }
                }
                _prevRightPinch = currentPinch;
            }
        }
        else
        {
            _rightOpenSince = DateTime.MinValue;
            _rightOpenFired = false;
            _prevRightX = -1f;
            _prevRightY = -1f;
            _prevRightPinch = -1f;
        }
    }

    public void Reset()
    {
        _leftWristHistory.Clear();
        _lastSwipeAt = DateTime.MinValue;
        _rightOpenSince = DateTime.MinValue;
        _rightOpenFired = false;
        _prevRightX = -1f; _prevRightY = -1f; _prevRightPinch = -1f;
        _bothOpenSince = DateTime.MinValue;
    }
}