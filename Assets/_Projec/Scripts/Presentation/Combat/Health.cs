using FishNet;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using Game.Core.Abilities;
using UnityEngine;

namespace Game.Presentation.Combat
{
    /// <summary>
    /// Salud server-authoritative. El daño/cura solo se aplica en el servidor; el valor
    /// actual se sincroniza a los clientes vía SyncVar. Reusable para jugadores y enemigos.
    /// </summary>
    public class Health : NetworkBehaviour, IDamageable
    {
        [SerializeField] private PlayerStats _stats;
        [SerializeField] private float _maxHealth = 100f;
        [Tooltip("Parpadeo de daño; se dispara a todos los clientes cuando esta entidad recibe daño.")]
        [SerializeField] private DamageFlash _damageFlash;

        // Cada tick (por defecto FishNet junta cambios cada 0,1 s): la vida tiene que verse al instante.
        private readonly SyncVar<float> _current = new SyncVar<float>(new SyncTypeSettings(Game.Presentation.Combat.NetSyncRates.EveryTick));

        public float Current => _current.Value;
        public float Max => _maxHealth;
        public bool IsDead => _current.Value <= 0f;

        private bool _invulnerable;

        public override void OnStartServer()
        {
            _current.Value = _maxHealth;
        }

        /// <summary>Server-only. Vuelve la entidad inmune a daño (ej. tras extraer).</summary>
        public void SetInvulnerable(bool value)
        {
            if (!base.IsServerStarted) return;
            _invulnerable = value;
        }

        /// <summary>
        /// amount positivo = daño, negativo = cura. Solo corre en servidor.
        /// </summary>
        public void ApplyDamage(float amount, int instigatorNetworkId)
        {
            if (!base.IsServerStarted) return;
            if (IsDead) return;
            if (_invulnerable && amount > 0f) return; // inmune a daño (no a curas, por si acaso)

            bool isDamage = amount > 0f;

            // Protección: reduce solo el daño entrante (no afecta curas).
            if (isDamage && _stats != null)
                amount *= (1f - _stats.ProtectionPercent);

            float newValue = Mathf.Clamp(_current.Value - amount, 0f, _maxHealth);
            _current.Value = newValue;

            // Feedback visible para todos: parpadeo en la entidad golpeada.
            if (isDamage)
            {
                FlashObserversRpc();
                NotifyDamageDirection(instigatorNetworkId);
            }

            if (newValue <= 0f)
                OnDied?.Invoke(instigatorNetworkId);
        }

        /// <summary>Server-only. Le avisa al dueño de dónde vino el golpe (indicador direccional del
        /// HUD). No hace nada si esta entidad no tiene dueño (ej. un enemigo) o si el instigador es
        /// ella misma (ej. daño de caída: no hay dirección que mostrar).</summary>
        private void NotifyDamageDirection(int instigatorNetworkId)
        {
            if (!base.Owner.IsValid) return;
            if (instigatorNetworkId == base.ObjectId) return;

            if (InstanceFinder.ServerManager.Objects.Spawned.TryGetValue(instigatorNetworkId, out NetworkObject instigatorNob))
                DamageDirectionTargetRpc(base.Owner, instigatorNob.transform.position);
        }

        [TargetRpc]
        private void DamageDirectionTargetRpc(NetworkConnection conn, Vector3 instigatorPosition)
        {
            OnDamagedWithDirection?.Invoke(instigatorPosition);
        }

        /// <summary>Client-only (dueño). Se dispara al recibir daño, con la posición mundial de quien
        /// lo causó (si se pudo resolver). Usado por el HUD para el indicador direccional.</summary>
        public event System.Action<Vector3> OnDamagedWithDirection;

        /// <summary>Reproduce el parpadeo de daño en todos los clientes que observan la entidad.</summary>
        [ObserversRpc]
        private void FlashObserversRpc()
        {
            if (_damageFlash != null)
                _damageFlash.Play();
        }

        /// <summary>Se dispara solo en el servidor cuando la vida llega a 0. El instigador es quién causó la muerte.</summary>
        public event System.Action<int> OnDied;
    }
}