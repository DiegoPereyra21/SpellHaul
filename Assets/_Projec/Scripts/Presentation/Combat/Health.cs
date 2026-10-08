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
        [Tooltip("Si destruirla cuenta como kill (kill marker, contador de kills). Falso para objetos como el muro de tierra.")]
        [SerializeField] private bool _countsAsKill = true;

        public bool CountsAsKill => _countsAsKill;

        // Cada tick (por defecto FishNet junta cambios cada 0,1 s): la vida tiene que verse al instante.
        private readonly SyncVar<float> _current = new SyncVar<float>(new SyncTypeSettings(Game.Presentation.Combat.NetSyncRates.EveryTick));

        // Vida máxima real (base + equipo): sincronizada para que el HUD de cada cliente la muestre.
        // 0 = todavía no la fijó el servidor (se usa la del prefab).
        private readonly SyncVar<float> _syncedMax = new SyncVar<float>(new SyncTypeSettings(Game.Presentation.Combat.NetSyncRates.EveryTick));
        private float _baseMax;

        public float Current => _current.Value;
        public float Max => _syncedMax.Value > 0f ? _syncedMax.Value : _maxHealth;
        public bool IsDead => _current.Value <= 0f;

        private bool _invulnerable;

        public override void OnStartServer()
        {
            if (_baseMax <= 0f) _baseMax = _maxHealth; // con pooling, OnStartServer se repite: la base es la del prefab
            _maxHealth = _baseMax;
            _syncedMax.Value = _maxHealth;
            _current.Value = _maxHealth;
            if (_stats != null)
            {
                _stats.OnStatsChanged += ApplyStatsMax;
                ApplyStatsMax();
            }
            LagCompHistory.EnsureOn(this); // historial de posiciones para lag compensation de los proyectiles
        }

        public override void OnStopServer()
        {
            if (_stats != null) _stats.OnStatsChanged -= ApplyStatsMax;
            LagCompHistory.StopOn(gameObject);
        }

        /// <summary>Server. El equipo cambió (ej. un guante de Nature): nueva vida máxima, manteniendo
        /// el mismo porcentaje de vida actual.</summary>
        private void ApplyStatsMax()
        {
            if (!base.IsServerStarted || _stats == null) return;
            float newMax = Mathf.Max(1f, _baseMax + _stats.MaxHealthBonus);
            float oldMax = _maxHealth;
            if (Mathf.Approximately(newMax, oldMax)) return;
            _maxHealth = newMax;
            _syncedMax.Value = newMax;
            if (!IsDead && oldMax > 0f)
                _current.Value = Mathf.Clamp(_current.Value / oldMax * newMax, 1f, newMax);
        }

        /// <summary>Server-only. Cambia la vida máxima y la llena (ej. muro de tierra según la rareza del guante).
        /// Se sincroniza a los clientes (Max).</summary>
        public void ServerSetMaxHealth(float max)
        {
            if (!base.IsServerStarted || max <= 0f) return;
            _maxHealth = max;
            _baseMax = max;
            _syncedMax.Value = max;
            _current.Value = max;
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