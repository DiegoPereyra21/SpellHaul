using FishNet;
using FishNet.Object;
using Game.Core.Abilities;
using Game.Presentation.Combat;
using UnityEngine;

namespace Game.Presentation.Abilities
{
    /// <summary>
    /// Orbe cargado con vuelo balístico (arco + caída por gravedad) hacia el punto de mira.
    /// Radio, daño y tamaño vienen definidos por cuánto se cargó. Server-authoritative;
    /// la escala visual se replica a los clientes por SyncVar.
    /// </summary>
    public class ChargedOrbProjectile : NetworkBehaviour
    {
        [SerializeField] private float _lifetime = 8f;
        [Tooltip("Radio del sweep de colisión en vuelo.")]
        [SerializeField] private float _castRadius = 0.3f;
        [Tooltip("Contra qué colisiona en vuelo. Hitbox + Ground. Vacío = se resuelve por nombre.")]
        [SerializeField] private LayerMask _hitMask;
        [Tooltip("Hijo visual que se escala según la carga (dejar vacío para escalar el root).")]
        [SerializeField] private Transform _visual;

        private readonly FishNet.Object.Synchronizing.SyncVar<float> _visualScale = new FishNet.Object.Synchronizing.SyncVar<float>(1f);

        private Vector3 _velocity;
        private float _gravity;
        private float _damage;
        private float _explosionRadius;
        private int _casterNetworkId;
        private int _slot = -1;
        private float _spawnTime;
        private bool _initialized;
        private bool _exploded;
        private bool _firstFrame;
        // Buffer compartido para el OverlapSphere sin allocations. El proyectil corre en Update
        // del server (single-thread), así que reutilizarlo secuencialmente es seguro.
        private static readonly Collider[] _overlapBuffer = new Collider[16];
        private static readonly Collider[] _explosionBuffer = new Collider[64];
        private static readonly System.Collections.Generic.HashSet<int> _alreadyHit = new();

        private void Awake()
        {
            if (_hitMask.value == 0)
                _hitMask = LayerMask.GetMask("Hitbox", "Ground");
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            ApplyVisualScale(_visualScale.Value);
            _visualScale.OnChange += (prev, next, asServer) => ApplyVisualScale(next);
        }

        private void ApplyVisualScale(float scale)
        {
            Transform target = _visual != null ? _visual : transform;
            target.localScale = Vector3.one * scale;
        }

        [Server]
        public void Initialize(Vector3 aimPoint, float damage, float explosionRadius, float visualScale, float launchSpeed, float gravity, int casterNetworkId, int slot)
        {
            _damage = damage;
            _explosionRadius = explosionRadius;
            _casterNetworkId = casterNetworkId;
            _slot = slot;
            _spawnTime = Time.time;
            _visualScale.Value = visualScale;
            ApplyVisualScale(visualScale);

            // Lanzamiento balístico REAL (ya no resuelto hacia atrás para garantizar el impacto
            // exacto): sale hacia el punto de mira a launchSpeed, y la gravedad lo va curvando
            // desde ahí. Con poca carga (velocidad baja) cae rápido y no llega lejos; con carga
            // completa vuela más recto y más lejos antes de que la gravedad se note — como
            // tirar una piedra con más o menos fuerza, no un misil calculado.
            Vector3 direction = aimPoint - transform.position;
            direction = direction.sqrMagnitude > 0.0001f ? direction.normalized : transform.forward;

            _gravity = gravity;
            _velocity = direction * launchSpeed;

            transform.rotation = Quaternion.LookRotation(direction);

            _initialized = true;
            _firstFrame = true;
        }

        private void Update()
        {
            if (!base.IsServerStarted) return;
            if (!_initialized || _exploded) return;

            // El frame del spawn tiene deltaTime poco confiable: saltearlo evita un primer paso feo.
            if (_firstFrame) { _firstFrame = false; return; }

            _velocity.y += _gravity * Time.deltaTime;
            Vector3 step = _velocity * Time.deltaTime;

            // Caso "el enemigo está pegado al origen": SphereCast NO detecta colliders que ya
            // solapan la esfera de partida. Un OverlapSphere previo cubre ese hueco (mismo
            // parche que ya tiene el proyectil básico).
            int overlapCount = Physics.OverlapSphereNonAlloc(transform.position, _castRadius, _overlapBuffer, _hitMask, QueryTriggerInteraction.Ignore);
            bool resolved = false;
            for (int i = 0; i < overlapCount; i++)
            {
                NetworkObject overlapNob = _overlapBuffer[i].GetComponentInParent<NetworkObject>();
                if (overlapNob != null && overlapNob.ObjectId == _casterNetworkId) continue; // el propio caster

                Explode(transform.position);
                resolved = true;
                break;
            }
            if (resolved) return;

            if (step.sqrMagnitude > 0.0001f &&
                Physics.SphereCast(transform.position, _castRadius, step.normalized, out RaycastHit hit,
                    step.magnitude, _hitMask, QueryTriggerInteraction.Ignore))
            {
                // El collider golpeable es un hijo (capa Hitbox); el NetworkObject está en el root.
                NetworkObject nob = hit.collider.GetComponentInParent<NetworkObject>();
                if (nob == null || nob.ObjectId != _casterNetworkId) // atravesar al caster
                {
                    Explode(hit.point);
                    return;
                }
            }

            transform.position += step;

            if (step.sqrMagnitude > 0.0001f)
                transform.rotation = Quaternion.LookRotation(step.normalized);

            if (Time.time - _spawnTime >= _lifetime)
                Explode(transform.position);
        }

        [Server]
        private void Explode(Vector3 point)
        {
            _exploded = true;

            bool hitConfirmed = false;
            bool isKill = false;

            // Un solo golpe por entidad: con más de una hitbox (cabeza + cuerpo, etc.) cada collider
            // aplicaba el daño de nuevo.
            _alreadyHit.Clear();
            int count = Physics.OverlapSphereNonAlloc(point, _explosionRadius, _explosionBuffer, _hitMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                NetworkObject nob = _explosionBuffer[i].GetComponentInParent<NetworkObject>();
                if (nob == null) continue;                              // geometría (Ground)
                if (nob.ObjectId == _casterNetworkId) continue;         // no dañar al caster
                if (!_alreadyHit.Add(nob.ObjectId)) continue;  // una vez por entidad

                if (nob.TryGetComponent(out IDamageable damageable))
                {
                    damageable.ApplyDamage(_damage, _casterNetworkId);
                    hitConfirmed = true;
                    if (damageable is Health health && health.IsDead)
                        isKill = true;
                }
            }

            ShowExplosionObserversRpc(point);

            if (InstanceFinder.ServerManager.Objects.Spawned.TryGetValue(_casterNetworkId, out NetworkObject casterNob) &&
                casterNob.TryGetComponent(out AbilityController ac))
            {
                ac.NotifyProjectileImpact(point, Vector3.up, hitConfirmed, isKill);
                ac.NotifyAbilityImpactSfx(point, _slot); // la explosión suena siempre
            }

            base.Despawn();
        }

        [ObserversRpc]
        private void ShowExplosionObserversRpc(Vector3 point)
        {
            VFXManager.PlayOrbExplosion(point);
        }
    }
}