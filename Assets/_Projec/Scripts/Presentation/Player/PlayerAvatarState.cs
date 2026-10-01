using FishNet.Object;
using Game.Presentation.Abilities;
using UnityEngine;

namespace Game.Presentation.Player
{
    /// <summary>
    /// Estado del avatar del jugador y lógica común de "desactivar control", compartida
    /// entre muerte y extracción. Evita duplicar la desactivación en cada handler.
    /// </summary>
    public class PlayerAvatarState : NetworkBehaviour
    {
        [SerializeField] private PlayerMovementController _movement;
        [SerializeField] private AbilityController _abilities;
        [SerializeField] private MonoBehaviour[] _extraToDisable; // cámara, etc.
        [SerializeField] private Collider _bodyCollider;
        [SerializeField] private Game.Presentation.UI.InventoryUIController _inventoryUI;
        [Tooltip("Raíz del modelo visible. Si queda vacío se usa este mismo objeto. Al morir o extraer se ocultan sus renderers: el avatar ya no está en la run, dejar el cuerpo parado confunde (el loot cae aparte, en su propio LootContainer).")]
        [SerializeField] private Transform _modelRoot;

        private bool _controlDisabled;

        public override void OnStartClient()
        {
            base.OnStartClient();
            // Pasos y aterrizajes (de todos los jugadores, en 3D). Se agrega acá para no tocar el prefab.
            if (GetComponent<Game.Presentation.Audio.FootstepAudio>() == null)
                gameObject.AddComponent<Game.Presentation.Audio.FootstepAudio>();
        }

        /// <summary>True si el avatar ya no participa de la run (murió o extrajo). En el servidor
        /// se vuelve true en el mismo instante de la muerte/extracción (el RPC es RunLocally), así
        /// que los ServerRpc lo usan para rechazar acciones de un jugador que ya no está.</summary>
        public bool IsControlDisabled => _controlDisabled;

        /// <summary>
        /// Desactiva todo el control del avatar y lo oculta. Idempotente (llamar dos veces no
        /// hace daño). Corre en todas las instancias (via el RPC del handler que lo llame).
        /// </summary>
        public void DisableControl()
        {
            if (_controlDisabled) return;
            _controlDisabled = true;

            if (_movement != null) _movement.DisableMovement();
            if (_abilities != null)
            {
                // enabled = false no frena corrutinas: sin esto un windup en curso disparaba igual.
                _abilities.CancelActiveCasts();
                _abilities.enabled = false;
            }

            if (_extraToDisable != null)
                foreach (var c in _extraToDisable)
                    if (c != null) c.enabled = false;

            if (_bodyCollider != null) _bodyCollider.enabled = false;
            DisableHitboxes();

            if (_inventoryUI != null)
                _inventoryUI.DisableInventory();

            HideModel();
        }

        /// <summary>Apaga las hitboxes (colliders en la capa Hitbox). _bodyCollider es solo el
        /// CharacterController: sin esto, el cuerpo invisible seguía frenando proyectiles y
        /// explosiones y le daba hitmarker falso al que disparaba. Corre en servidor y clientes
        /// (el RPC que llama a DisableControl es RunLocally), así que el hit-reg también lo ve.</summary>
        private void DisableHitboxes()
        {
            int hitboxLayer = LayerMask.NameToLayer("Hitbox");
            if (hitboxLayer < 0) return;

            foreach (var col in GetComponentsInChildren<Collider>(true))
                if (col != null && col.gameObject.layer == hitboxLayer)
                    col.enabled = false;
        }

        /// <summary>Apaga los renderers en vez del GameObject entero: desactivar el objeto se
        /// llevaría puesta la cámara y demás componentes hijos que el dueño todavía necesita
        /// para ver la pantalla de resultados.</summary>
        private void HideModel()
        {
            Transform root = _modelRoot != null ? _modelRoot : transform;
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
                if (r != null) r.enabled = false;
        }
    }
}