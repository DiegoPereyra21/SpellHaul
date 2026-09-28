using FishNet.Object;
using FishNet.Object.Synchronizing;
using Game.Core.Run;
using Game.Presentation.Player;
using UnityEngine;
using Game.Presentation.Run;

namespace Game.Presentation.Combat
{
    /// <summary>
    /// Estado de extracción del jugador (server-authoritative). Cuando el servidor lo marca
    /// extraído, se desactiva el control y se salva el loot (vía IRunInventory si existe).
    /// </summary>
    public class PlayerExtractionState : NetworkBehaviour
    {
        [SerializeField] private PlayerAvatarState _avatar;

        // Progreso de canalización [0..1], seteado por la ExtractionZone en el servidor.
        private readonly SyncVar<float> _extractionProgress = new SyncVar<float>();
        private readonly SyncVar<bool> _isExtracted = new SyncVar<bool>();

        public float ExtractionProgress => _extractionProgress.Value;
        public bool IsExtracted => _isExtracted.Value;

        private Health _health;

        /// <summary>True si todavía puede extraer: ni extraído ni muerto. Al morir se apaga el
        /// CharacterController y Unity no dispara OnTriggerExit, así que la zona no se entera
        /// sola de que el jugador ya no está: lo tiene que preguntar acá.</summary>
        public bool CanExtract => !_isExtracted.Value && (_health == null || !_health.IsDead);

        private void Awake()
        {
            _health = GetComponent<Health>();
        }

        public override void OnStartServer()
        {
            TryRegisterInRunManager();
        }
        public override void OnStopServer()
        {
            // Se fue sin morir ni extraer (desconexión): que no quede contado como vivo.
            if (Game.Presentation.Run.RunManager.Instance != null)
                Game.Presentation.Run.RunManager.Instance.UnregisterPlayer(base.ObjectId);
        }

        private void TryRegisterInRunManager()
        {
            if (Game.Presentation.Run.RunManager.Instance != null)
            {
                Game.Presentation.Run.RunManager.Instance.RegisterPlayer(base.ObjectId);
            }
            else
            {
                // El RunManager todavía no existe (orden de spawn en la transición de escena).
                // Reintentar el próximo frame.
                StartCoroutine(RetryRegister());
            }
        }

        private System.Collections.IEnumerator RetryRegister()
        {
            float timeout = 5f;
            while (Game.Presentation.Run.RunManager.Instance == null && timeout > 0f && base.IsSpawned)
            {
                timeout -= Time.deltaTime;
                yield return null;
            }

            if (Game.Presentation.Run.RunManager.Instance != null)
                Game.Presentation.Run.RunManager.Instance.RegisterPlayer(base.ObjectId);
            else
                Debug.LogWarning("[PlayerExtractionState] No se encontró RunManager para registrar el jugador.");
        }

        /// <summary>Server-only. Llamado por la ExtractionZone cada tick con el progreso actual.</summary>
        public void ServerSetProgress(float value)
        {
            if (!base.IsServerInitialized) return;
            _extractionProgress.Value = Mathf.Clamp01(value);
        }

        /// <summary>Server-only. Marca extracción exitosa.</summary>
        public void ServerCompleteExtraction()
        {
            if (!base.IsServerInitialized) return;
            if (!CanExtract) return;

            _isExtracted.Value = true;
            if (Game.Presentation.Run.RunManager.Instance != null)
                Game.Presentation.Run.RunManager.Instance.SetExtracted(base.ObjectId);
            _extractionProgress.Value = 1f;

            if (TryGetComponent(out IRunInventory inventory))
                inventory.CommitToStash();

            if (TryGetComponent(out Health health))
                health.SetInvulnerable(true);

            ExtractObserversRpc();
        }

        [ObserversRpc(RunLocally = true)]
        private void ExtractObserversRpc()
        {
            if (_avatar != null) _avatar.DisableControl();

            if (base.IsOwner)
            {
                var result = FindFirstObjectByType<Game.Presentation.UI.ResultScreenController>();
                if (result != null) result.Show(true);
            }
        }
    }
}