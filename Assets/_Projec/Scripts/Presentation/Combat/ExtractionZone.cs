using System.Collections.Generic;
using FishNet.Object;
using UnityEngine;

namespace Game.Presentation.Combat
{
    /// <summary>
    /// Zona de extracción fija (trigger). Server-authoritative: mientras un jugador vivo
    /// permanece dentro, acumula progreso de canalización; si sale o muere, se reinicia.
    /// Al completar, marca al jugador como extraído. Cada jugador canaliza independiente.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class ExtractionZone : NetworkBehaviour
    {
        [SerializeField] private float _channelTime = 5f;

        // Progreso por jugador presente en la zona (solo servidor).
        private readonly Dictionary<PlayerExtractionState, float> _channeling = new();
        private readonly List<PlayerExtractionState> _iterationBuffer = new(); // evita una lista nueva por frame

        private void OnTriggerEnter(Collider other)
        {
            if (!base.IsServerInitialized) return;

            if (other.TryGetComponent(out PlayerExtractionState ext) && ext.CanExtract)
            {
                if (!_channeling.ContainsKey(ext))
                    _channeling[ext] = 0f;
            }
        }

        private void OnTriggerExit(Collider other)
        {
            if (!base.IsServerInitialized) return;

            if (other.TryGetComponent(out PlayerExtractionState ext))
            {
                if (_channeling.Remove(ext))
                    ext.ServerSetProgress(0f); // cancelado: reinicia la barra
            }
        }

        private void Update()
        {
            if (!base.IsServerInitialized) return;
            if (_channeling.Count == 0) return;

            // Copia de keys para poder modificar el diccionario mientras iteramos.
            _iterationBuffer.Clear();
            _iterationBuffer.AddRange(_channeling.Keys);
            foreach (var ext in _iterationBuffer)
            {
                // Sin dueño (desconectado): no canaliza. Se queda en la lista con la barra en 0 para
                // retomar si vuelve estando todavía adentro (OnTriggerEnter no se repite).
                // Antes seguía canalizando solo y "extraía" sin nadie a quien guardarle el loot.
                if (ext != null && ext.CanExtract && !ext.Owner.IsActive)
                {
                    _channeling[ext] = 0f;
                    ext.ServerSetProgress(0f);
                    continue;
                }

                if (ext == null || !ext.CanExtract)
                {
                    _channeling.Remove(ext);
                    // Murió canalizando: la barra vuelve a 0 (si ya extrajo, la deja llena).
                    if (ext != null && !ext.IsExtracted) ext.ServerSetProgress(0f);
                    continue;
                }

                float elapsed = _channeling[ext] + Time.deltaTime;
                _channeling[ext] = elapsed;

                float progress = Mathf.Clamp01(elapsed / _channelTime);
                ext.ServerSetProgress(progress);

                if (progress >= 1f)
                {
                    ext.ServerCompleteExtraction();
                    _channeling.Remove(ext);
                }
            }
        }
    }
}