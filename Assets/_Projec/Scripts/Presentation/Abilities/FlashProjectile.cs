using FishNet;
using FishNet.Object;
using Game.Presentation.Combat;
using Game.Presentation.Player;
using Game.Presentation.UI;
using UnityEngine;

namespace Game.Presentation.Abilities
{
    /// <summary>
    /// Orbe de destello (FlashOrbAbilitySO). Server-authoritative: vuela en línea recta y detona
    /// al chocar con cualquier cosa (Hitbox o Ground, menos el caster), al terminar su vida o
    /// cuando el caster lo re-activa (IRecastable). La detonación no hace daño:
    /// - IA alcanzada con línea de visión: queda ciega (Blindness) por un tiempo.
    /// - Jugadores: el servidor solo avisa dónde fue; cada cliente calcula su propio cegado según
    ///   hacia dónde mira y si lo ve (FlashOverlay). El caster también puede cegarse.
    /// Los clientes ven el vuelo por el NetworkTransform del prefab.
    /// </summary>
    public class FlashProjectile : ClientFlightBehaviour, IRecastable
    {
        [Tooltip("Radio del sweep de colisión en vuelo.")]
        [SerializeField] private float _castRadius = 0.25f;
        [Tooltip("Capas extra contra las que choca en vuelo. Hitbox y Ground se agregan siempre (sin Hitbox atravesaba a los enemigos).")]
        [SerializeField] private LayerMask _hitMask;
        [Tooltip("Qué tapa la vista del destello. Vacío = Ground.")]
        [SerializeField] private LayerMask _occluderMask;

        [Header("Visual")]
        [Tooltip("Hijo con las partículas del orbe. Vacío = el primer hijo con ParticleSystem.")]
        [SerializeField] private Transform _visual;
        [Tooltip("Escala del orbe en vuelo (chico a propósito: el destello es lo que impacta).")]
        [SerializeField] private float _flightScale = 0.35f;
        [Tooltip("Cuánto crece el destello al detonar, como fracción del radio de efecto.")]
        [SerializeField] private float _burstSizeOfRadius = 0.45f;
        [Tooltip("Color de la luz del destello.")]
        [SerializeField] private Color _burstLightColor = new Color(0.92f, 0.88f, 1f);

        private Vector3 _velocity;
        private float _lifetime;
        private float _flashRadius;
        private float _playerBlindSeconds;
        private float _aiBlindSeconds;
        private int _casterNetworkId;
        private int _slot = -1;
        private float _spawnTime;
        private bool _initialized;
        private bool _detonated;
        private bool _firstFrame;

        private static readonly Collider[] _overlapBuffer = new Collider[16];
        private static readonly Collider[] _flashBuffer = new Collider[64];
        private static readonly System.Collections.Generic.HashSet<int> _alreadyHit = new();

        private void Awake()
        {
            _hitMask |= LayerMask.GetMask("Hitbox", "Ground");
            if (_occluderMask.value == 0) _occluderMask = LayerMask.GetMask("Ground");

            // Colisión resuelta a mano: un collider sólido haría que la gente choque con el orbe.
            foreach (var col in GetComponentsInChildren<Collider>(true))
                col.enabled = false;

            ApplyFlightScale();
        }

        /// <summary>Achica el orbe en vuelo (todas las instancias, sin sincronizar: es fijo).</summary>
        private void ApplyFlightScale()
        {
            if (_visual == null)
            {
                foreach (Transform child in transform)
                    if (child.GetComponentInChildren<ParticleSystem>(true) != null) { _visual = child; break; }
            }
            if (_visual == null) return;

            // Las partículas solo respetan la escala del transform en modo Hierarchy.
            foreach (var ps in _visual.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = ps.main;
                main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            }
            foreach (var trail in _visual.GetComponentsInChildren<TrailRenderer>(true))
                trail.widthMultiplier *= _flightScale;
            _visual.localScale = Vector3.one * _flightScale;
        }

        [Server]
        public void Initialize(Vector3 direction, float speed, float lifetime, float flashRadius,
            float playerBlindSeconds, float aiBlindSeconds, int casterNetworkId, int slot)
        {
            _velocity = direction.normalized * speed;
            _lifetime = lifetime;
            _flashRadius = flashRadius;
            _playerBlindSeconds = playerBlindSeconds;
            _aiBlindSeconds = aiBlindSeconds;
            _casterNetworkId = casterNetworkId;
            _slot = slot;
            _spawnTime = Time.time;
            _initialized = true;
            _detonated = false; // instancia reutilizada del pool
            _firstFrame = true;
            if (direction.sqrMagnitude > 0.0001f) transform.rotation = Quaternion.LookRotation(direction);

            RecastRegistry.Register(casterNetworkId, slot, this);
            BroadcastFlight(transform.position, _velocity, 0f);
        }

        public override void OnStopServer()
        {
            base.OnStopServer();
            RecastRegistry.Unregister(_casterNetworkId, _slot, this);
        }

        /// <summary>Server. El caster volvió a apretar el botón: detona donde esté.</summary>
        public void Recast()
        {
            if (_initialized && !_detonated) Detonate(transform.position);
        }

        private void Update()
        {
            if (!base.IsServerStarted || !_initialized || _detonated) return;
            if (_firstFrame) { _firstFrame = false; return; } // deltaTime del frame del spawn no es confiable

            Vector3 step = _velocity * Time.deltaTime;

            // Pegado a algo al arrancar: el SphereCast no ve lo que ya solapa la esfera inicial.
            int overlapCount = Physics.OverlapSphereNonAlloc(transform.position, _castRadius, _overlapBuffer, _hitMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < overlapCount; i++)
            {
                if (IsCaster(_overlapBuffer[i])) continue;
                Detonate(transform.position);
                return;
            }

            if (step.sqrMagnitude > 0.0001f &&
                Physics.SphereCast(transform.position, _castRadius, step.normalized, out RaycastHit hit,
                    step.magnitude, _hitMask, QueryTriggerInteraction.Ignore) && !IsCaster(hit.collider))
            {
                // Un poco antes del punto de choque: así el destello no queda "dentro" de la pared.
                Detonate(hit.point - step.normalized * 0.3f);
                return;
            }

            transform.position += step;

            if (Time.time - _spawnTime >= _lifetime)
                Detonate(transform.position);
        }

        private bool IsCaster(Collider col)
        {
            NetworkObject nob = col.GetComponentInParent<NetworkObject>();
            return nob != null && nob.ObjectId == _casterNetworkId;
        }

        [Server]
        private void Detonate(Vector3 point)
        {
            _detonated = true;
            RecastRegistry.Unregister(_casterNetworkId, _slot, this);

            BlindEnemies(point);
            FlashObserversRpc(point, _flashRadius, _playerBlindSeconds);

            if (InstanceFinder.ServerManager.Objects.Spawned.TryGetValue(_casterNetworkId, out NetworkObject casterNob) &&
                casterNob.TryGetComponent(out AbilityController ac))
            {
                ac.NotifyAbilityImpactSfx(point, _slot); // sonido de la detonación (impact clip del SO)
                ac.NotifyRecastEnded(_slot);
            }

            base.Despawn();
        }

        /// <summary>Server. Ciega a la IA dentro del radio que tenga línea de visión al destello.</summary>
        private void BlindEnemies(Vector3 point)
        {
            _alreadyHit.Clear();
            int count = Physics.OverlapSphereNonAlloc(point, _flashRadius, _flashBuffer, LayerMask.GetMask("Hitbox"), QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                NetworkObject nob = _flashBuffer[i].GetComponentInParent<NetworkObject>();
                if (nob == null || !_alreadyHit.Add(nob.ObjectId)) continue;
                if (nob.GetComponent<PlayerAvatarState>() != null) continue; // jugadores: lo resuelve su cliente

                Vector3 eye = _flashBuffer[i].bounds.center;
                if (Physics.Linecast(point, eye, _occluderMask, QueryTriggerInteraction.Ignore)) continue; // tapado

                Blindness.ApplyTo(nob.gameObject, _aiBlindSeconds);
            }
        }

        [ObserversRpc]
        private void FlashObserversRpc(Vector3 point, float radius, float maxBlindSeconds)
        {
            VFXManager.PlayOrbExplosion(point);
            FlashBurst.Play(point, radius * _burstSizeOfRadius, radius, _visual, _flightScale, _burstLightColor);
            FlashOverlay.Trigger(point, radius, maxBlindSeconds);
        }
    }
}
