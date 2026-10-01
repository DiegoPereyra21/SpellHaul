using UnityEngine;

namespace Game.Presentation.Abilities
{
    /// <summary>
    /// Visual local de la detonación del orbe de destello: una copia del orbe que crece de golpe
    /// hasta un tamaño grande mientras deja de emitir, y una luz puntual que se enciende fuerte y
    /// se apaga. Solo cliente; se destruye sola.
    /// </summary>
    public class FlashBurst : MonoBehaviour
    {
        private const float GrowTime = 0.18f;     // segundos hasta el tamaño máximo
        private const float LightTime = 0.6f;     // duración de la luz
        private const float LifeTime = 1.6f;      // tiempo hasta destruir (deja morir las partículas)
        private const float PeakIntensity = 18f;

        private Transform _visual;
        private Light _light;
        private float _startScale;
        private float _endScale;
        private float _lightRange;
        private float _t;
        private bool _stoppedEmitting;

        /// <param name="burstSize">Escala final de la copia del orbe.</param>
        /// <param name="lightRange">Alcance máximo de la luz (radio de efecto).</param>
        /// <param name="template">Visual del orbe a copiar (puede ser null: solo luz).</param>
        public static void Play(Vector3 point, float burstSize, float lightRange, Transform template, float startScale, Color color)
        {
            var go = new GameObject("FlashBurst");
            go.transform.position = point;
            var burst = go.AddComponent<FlashBurst>();

            if (template != null)
            {
                burst._visual = Instantiate(template.gameObject, point, Quaternion.identity, go.transform).transform;
                foreach (var ps in burst._visual.GetComponentsInChildren<ParticleSystem>(true))
                {
                    var main = ps.main;
                    main.scalingMode = ParticleSystemScalingMode.Hierarchy;
                    ps.Play(true);
                }
                foreach (var trail in burst._visual.GetComponentsInChildren<TrailRenderer>(true))
                    trail.enabled = false; // la estela no tiene sentido en una explosión quieta
            }

            burst._light = go.AddComponent<Light>();
            burst._light.type = LightType.Point;
            burst._light.color = color;
            burst._light.range = 1f;
            burst._light.intensity = PeakIntensity;
            burst._light.shadows = LightShadows.None;

            burst._startScale = Mathf.Max(0.05f, startScale);
            burst._endScale = Mathf.Max(burst._startScale, burstSize);
            burst._lightRange = Mathf.Max(1f, lightRange);
            if (burst._visual != null) burst._visual.localScale = Vector3.one * burst._startScale;
        }

        private void Update()
        {
            _t += Time.deltaTime;

            // Crecimiento rápido con frenado al final (ease-out).
            float g = Mathf.Clamp01(_t / GrowTime);
            float eased = 1f - (1f - g) * (1f - g);
            if (_visual != null)
            {
                _visual.localScale = Vector3.one * Mathf.Lerp(_startScale, _endScale, eased);
                if (!_stoppedEmitting && g >= 1f)
                {
                    _stoppedEmitting = true;
                    foreach (var ps in _visual.GetComponentsInChildren<ParticleSystem>(true))
                        ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                }
            }

            if (_light != null)
            {
                float l = Mathf.Clamp01(_t / LightTime);
                _light.range = Mathf.Lerp(1f, _lightRange, eased);
                _light.intensity = PeakIntensity * (1f - l) * (1f - l);
                if (l >= 1f) _light.enabled = false;
            }

            if (_t >= LifeTime) Destroy(gameObject);
        }
    }
}
