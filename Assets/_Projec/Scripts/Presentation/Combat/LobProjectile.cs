using FishNet;
using FishNet.Object;
using Game.Core.Abilities;
using Game.Presentation.Abilities;
using UnityEngine;

namespace Game.Presentation.Combat
{
    /// <summary>
    /// Ballistic projectile: follows a parabolic arc toward a target point,
    /// explodes on impact (small area damage). Server-authoritative.
    /// </summary>
    public class LobProjectile : NetworkBehaviour
    {
        [Header("Explosion")]
        [SerializeField] private float _explosionRadius = 2f;
        [SerializeField] private float _lifetime = 8f;

        [Tooltip("Contra qué colisiona. Debe incluir Hitbox (objetivos) y Ground (paredes/piso). Vacío = se resuelve por nombre.")]
        [SerializeField] private LayerMask _hitMask;

        private Vector3 _velocity;
        private float _gravity;
        private float _damage;
        private int _casterNetworkId;
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

        [Server]
        public void Initialize(Vector3 targetPoint, float arcHeight, float flightTime, float damage, int casterNetworkId)
        {
            _damage = damage;
            _casterNetworkId = casterNetworkId;
            _spawnTime = Time.time;

            Vector3 toTarget = targetPoint - transform.position;
            Vector3 flatDelta = new Vector3(toTarget.x, 0f, toTarget.z);

            _gravity = -8f * arcHeight / (flightTime * flightTime);
            float verticalVelocity = (toTarget.y - 0.5f * _gravity * flightTime * flightTime) / flightTime;

            _velocity = flatDelta / flightTime + Vector3.up * verticalVelocity;

            if (_velocity.sqrMagnitude > 0.0001f)
                transform.rotation = Quaternion.LookRotation(_velocity.normalized);

            _initialized = true;
            _firstFrame = true;
        }

        private void Update()
        {
            if (!base.IsServerStarted) return;
            if (!_initialized || _exploded) return;

            if (_firstFrame)
            {
                _firstFrame = false;
                return;
            }

            _velocity.y += _gravity * Time.deltaTime;
            Vector3 step = _velocity * Time.deltaTime;

            // Caso "el objetivo está pegado al proyectil": SphereCast NO detecta colliders que ya
            // solapan la esfera de partida. Un OverlapSphere previo cubre ese hueco (mismo parche
            // que ya tienen el proyectil básico y el orbe cargado).
            int overlapCount = Physics.OverlapSphereNonAlloc(transform.position, 0.3f, _overlapBuffer, _hitMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < overlapCount; i++)
            {
                NetworkObject overlapNob = _overlapBuffer[i].GetComponentInParent<NetworkObject>();
                if (overlapNob != null && overlapNob.ObjectId == _casterNetworkId) continue; // el propio guardián
                Explode(transform.position);
                return;
            }

            if (Physics.SphereCast(transform.position, 0.3f, step.normalized, out RaycastHit hit,
                    step.magnitude, _hitMask, QueryTriggerInteraction.Ignore))
            {
                // El collider golpeado es la Hitbox (hija); NetworkObject vive en la raíz.
                NetworkObject nob = hit.collider.GetComponentInParent<NetworkObject>();
                if (!(nob != null && nob.ObjectId == _casterNetworkId))
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

            // Un solo golpe por entidad: con más de una hitbox (cabeza + cuerpo, etc.) cada collider
            // aplicaba el daño de nuevo.
            _alreadyHit.Clear();
            int count = Physics.OverlapSphereNonAlloc(point, _explosionRadius, _explosionBuffer, _hitMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                NetworkObject nob = _explosionBuffer[i].GetComponentInParent<NetworkObject>();
                if (nob == null) continue; // geometría (Ground)
                if (nob.ObjectId == _casterNetworkId) continue; // no dañar al propio guardián
                if (!_alreadyHit.Add(nob.ObjectId)) continue;  // una vez por entidad

                if (nob.TryGetComponent(out IDamageable damageable))
                    damageable.ApplyDamage(_damage, _casterNetworkId);
            }

            ShowExplosionObserversRpc(point);
            base.Despawn();
        }

        [ObserversRpc]
        private void ShowExplosionObserversRpc(Vector3 point)
        {
            VFXManager.PlayOrbExplosion(point);
        }
    }
}