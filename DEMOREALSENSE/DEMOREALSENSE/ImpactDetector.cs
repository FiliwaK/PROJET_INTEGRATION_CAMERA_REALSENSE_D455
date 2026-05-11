using System;

namespace DEMOREALSENSE
{
    /// <summary>
    /// Détecte les rebonds de balle par analyse de la vélocité Y.
    ///
    /// PRINCIPE :
    ///   1. IDLE    : on attend que la vélocité Y lissée dépasse FallVyThresh
    ///               (balle qui chute clairement, Y augmente en coords image).
    ///   2. FALLING : on suit le pic Y (point le plus bas en image = sol).
    ///               Dès que la vélocité Y lissée repasse sous -RiseVyThresh
    ///               (balle qui remonte clairement) → rebond confirmé.
    ///
    /// Avantages vs seuils de déplacement cumulé :
    ///   - Insensible aux dérives lentes (balle qui roule horizontalement).
    ///   - Détecte uniquement les inversions franches de direction verticale.
    ///   - Fonctionne de la même façon pour le mode IA et le mode algo.
    ///
    /// Paramètres :
    ///   FallVyThresh — vélocité Y minimum (px/frame) pour entrer en mode chute.
    ///   RiseVyThresh — vélocité Y upward minimum (px/frame) pour confirmer rebond.
    ///   CooldownMs   — délai minimum entre deux détections consécutives.
    /// </summary>
    public sealed class ImpactDetector
    {
        public float FallVyThresh { get; set; } = 6f;
        public float RiseVyThresh { get; set; } = 5f;
        public int   CooldownMs   { get; set; } = 700;

        /// <summary>Position X exacte de la balle au moment du rebond (pic Y).</summary>
        public float LastBounceX { get; private set; }

        /// <summary>Position Y exacte de la balle au moment du rebond (pic Y).</summary>
        public float LastBounceY { get; private set; }

        private enum State { Idle, Falling }

        private State _state        = State.Idle;
        private float _prevY        = float.MinValue;
        private float _smoothVy     = 0f;           // vélocité Y lissée EMA
        private float _peakY        = float.MinValue;
        private float _peakX        = 0f;
        private long  _lastFireTicks = 0;

        private const float VyAlpha = 0.5f;         // lissage EMA de la vélocité

        public void Reset()
        {
            _state        = State.Idle;
            _prevY        = float.MinValue;
            _smoothVy     = 0f;
            _peakY        = float.MinValue;
            _peakX        = 0f;
            _lastFireTicks = 0;
            LastBounceX   = LastBounceY = 0f;
        }

        /// <summary>
        /// Appeler chaque frame avec la position (x, y) du bas de la balle.
        /// Retourne true quand un rebond est confirmé.
        /// </summary>
        public bool UpdateBounce(float x, float y, long nowTicks)
        {
            // Première frame : initialise sans déclencher
            if (_prevY == float.MinValue) { _prevY = y; return false; }

            // Vélocité Y lissée : positif = balle descend, négatif = balle monte
            float rawVy = y - _prevY;
            _smoothVy = _smoothVy * (1f - VyAlpha) + rawVy * VyAlpha;
            _prevY = y;

            switch (_state)
            {
                case State.Idle:
                    // La balle chute clairement → on entre en surveillance du pic
                    if (_smoothVy >= FallVyThresh)
                    {
                        _state = State.Falling;
                        _peakY = y;
                        _peakX = x;
                    }
                    break;

                case State.Falling:
                    // Mise à jour du pic (Y maximum = point le plus bas en image)
                    if (y >= _peakY) { _peakY = y; _peakX = x; }

                    // La balle remonte franchement → rebond confirmé
                    if (_smoothVy <= -RiseVyThresh)
                    {
                        LastBounceX = _peakX;
                        LastBounceY = _peakY;
                        _state    = State.Idle;
                        _smoothVy = 0f;
                        return TryFire(nowTicks);
                    }
                    break;
            }
            return false;
        }

        private bool TryFire(long nowTicks)
        {
            long cooldown = CooldownMs * TimeSpan.TicksPerMillisecond;
            if (_lastFireTicks != 0 && (nowTicks - _lastFireTicks) < cooldown)
                return false;
            _lastFireTicks = nowTicks;
            return true;
        }
    }
}
