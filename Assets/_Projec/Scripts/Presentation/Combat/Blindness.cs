using UnityEngine;

namespace Game.Presentation.Combat
{
    /// <summary>
    /// Server-only. Ceguera de la IA (orbe de destello): mientras dure, el enemigo pierde el
    /// objetivo y no ataca. Se agrega en runtime al primer cegado, no hace falta ponerlo en los
    /// prefabs. La ceguera de los jugadores es visual y la resuelve cada cliente (FlashOverlay).
    /// </summary>
    public class Blindness : MonoBehaviour
    {
        private float _blindUntil;

        public bool IsBlind => Time.time < _blindUntil;

        /// <summary>Ciega por 'seconds' (si ya estaba ciego, se queda con lo que dure más).</summary>
        public void Apply(float seconds)
        {
            if (seconds <= 0f) return;
            _blindUntil = Mathf.Max(_blindUntil, Time.time + seconds);
        }

        public static void ApplyTo(GameObject target, float seconds)
        {
            if (target == null) return;
            if (!target.TryGetComponent(out Blindness b)) b = target.AddComponent<Blindness>();
            b.Apply(seconds);
        }

        public static bool IsBlindNow(Component c) => c != null && c.TryGetComponent(out Blindness b) && b.IsBlind;
    }
}
