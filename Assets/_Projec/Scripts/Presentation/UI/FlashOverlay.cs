using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Presentation.UI
{
    /// <summary>
    /// Cegado del jugador local por el orbe de destello, estilo flashbang. Lo calcula cada
    /// cliente para su propia cámara (el servidor solo avisa dónde detonó):
    /// - Sin línea de visión al destello (tapado por geometría Ground): nada.
    /// - Mirándolo de frente: blanco total y largo; de costado, menos; de espaldas, apenas.
    /// - Más lejos, más corto y más tenue.
    /// La pantalla queda blanca un rato y después se desvanece. Lo registra el HUD del dueño.
    /// </summary>
    public class FlashOverlay : MonoBehaviour
    {
        private const float HoldFraction = 0.55f;   // parte del cegado a opacidad máxima antes de desvanecerse
        private const float MinDuration = 0.15f;    // por debajo de esto no se muestra nada

        private static FlashOverlay _local;

        private VisualElement _overlay;
        private Camera _cam;
        private int _occluderMask;

        private float _peak;
        private float _holdEnd;
        private float _end;

        public void Init(VisualElement root, Camera cam)
        {
            _overlay = root.Q<VisualElement>("flash-overlay");
            _cam = cam;
            _occluderMask = LayerMask.GetMask("Ground");
            _local = this;
        }

        private void OnDestroy()
        {
            if (_local == this) _local = null;
        }

        /// <summary>Un destello detonó en 'point'. Cada cliente decide cuánto lo ciega a él.</summary>
        public static void Trigger(Vector3 point, float radius, float maxBlindSeconds)
        {
            if (_local != null) _local.Apply(point, radius, maxBlindSeconds);
        }

        private void Apply(Vector3 point, float radius, float maxBlindSeconds)
        {
            Camera cam = _cam != null && _cam.isActiveAndEnabled ? _cam : Camera.main;
            if (cam == null || radius <= 0f || maxBlindSeconds <= 0f) return;

            Vector3 eye = cam.transform.position;
            Vector3 toFlash = point - eye;
            float distance = toFlash.magnitude;
            if (distance > radius) return;
            if (Physics.Linecast(eye, point, _occluderMask, QueryTriggerInteraction.Ignore)) return; // tapado

            // De frente (dentro de ~60°) pega completo; de costado baja; de espaldas casi nada.
            float facing = distance > 0.01f ? Vector3.Dot(cam.transform.forward, toFlash / distance) : 1f;
            float angleFactor = facing >= 0.5f ? 1f
                : facing >= 0f ? Mathf.Lerp(0.35f, 1f, facing / 0.5f)
                : Mathf.Lerp(0.1f, 0.35f, facing + 1f);
            float distanceFactor = Mathf.Lerp(1f, 0.4f, distance / radius);
            float strength = angleFactor * distanceFactor;

            float duration = maxBlindSeconds * strength;
            if (duration < MinDuration) return;

            // Si ya estaba cegado, se queda con lo más fuerte.
            float now = Time.time;
            float peak = Mathf.Clamp01(0.45f + strength);
            _peak = Mathf.Max(peak, CurrentAlpha(now));
            _holdEnd = Mathf.Max(_holdEnd, now + duration * HoldFraction);
            _end = Mathf.Max(_end, now + duration);
        }

        private float CurrentAlpha(float now)
        {
            if (now >= _end) return 0f;
            if (now <= _holdEnd) return _peak;
            float fade = (now - _holdEnd) / Mathf.Max(0.01f, _end - _holdEnd);
            return _peak * (1f - fade);
        }

        private void Update()
        {
            if (_overlay == null) return;
            float alpha = CurrentAlpha(Time.time);
            bool visible = alpha > 0.001f;
            _overlay.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (visible) _overlay.style.opacity = alpha;
        }
    }
}
