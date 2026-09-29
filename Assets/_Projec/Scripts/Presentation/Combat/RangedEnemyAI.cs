using FishNet;
using FishNet.Object;
using Game.Presentation.Abilities;
using UnityEngine;

namespace Game.Presentation.Combat
{
    /// <summary>
    /// Enemigo a distancia simple: detecta al jugador más cercano, lo mira y dispara
    /// proyectiles a intervalos. No se mueve. Server-authoritative.
    /// </summary>
    [RequireComponent(typeof(Health))]
    public class RangedEnemyAI : NetworkBehaviour
    {
        [Header("Detección")]
        [SerializeField] private float _detectionRadius = 15f;
        [SerializeField] private LayerMask _visionBlockMask;   // qué bloquea la línea de visión (default: Ground)
        [SerializeField] private float _eyeHeight = 1.4f;

        [Header("Disparo")]
        [SerializeField] private GameObject _projectilePrefab;
        [SerializeField] private float _fireRate = 2f;
        [SerializeField] private float _projectileSpeed = 12f;
        [SerializeField] private float _projectileDamage = 10f;
        [SerializeField] private float _projectileRadius = 0.25f;

        private Health _health;
        private Transform _target;
        private float _fireTimer;

        private void Awake()
        {
            _health = GetComponent<Health>();
            if (_visionBlockMask == 0) _visionBlockMask = LayerMask.GetMask("Ground");
        }

        public override void OnStartServer()
        {
            _health.OnDied += _ => enabled = false;
            _fireTimer = _fireRate; // esperar un ciclo antes del primer disparo
        }

        private void Update()
        {
            if (!base.IsServerStarted) return;

            // Buscar target si no tenemos.
            if (!TargetIsValid())
                _target = FindNearestPlayer(_detectionRadius);

            if (_target == null) return;

            // Mirar al target.
            Vector3 dir = (_target.position - transform.position);
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.Slerp(transform.rotation,
                    Quaternion.LookRotation(dir), Time.deltaTime * 5f);

            // Disparo con cooldown.
            _fireTimer -= Time.deltaTime;
            if (_fireTimer <= 0f)
            {
                _fireTimer = _fireRate;
                FireProjectile();
            }
        }

        [Server]
        private void FireProjectile()
        {
            if (_projectilePrefab == null) return;

            // Dirección hacia el target con algo de altura para que no vaya al suelo.
            Vector3 origin = transform.position + Vector3.up * 1.2f;
            Vector3 dir = (_target.position + Vector3.up * 1f - origin).normalized;

            NetworkObject nob = InstanceFinder.NetworkManager.GetPooledInstantiated(
                _projectilePrefab.GetComponent<NetworkObject>(), origin, Quaternion.LookRotation(dir), true);

            if (nob.TryGetComponent(out Projectile projectile))
            {
                projectile.Initialize(dir, _projectileSpeed, _projectileDamage,
                    _projectileRadius, base.ObjectId);
                InstanceFinder.ServerManager.Spawn(nob);
            }
        }

        private Transform FindNearestPlayer(float radius)
        {
            var players = Player.PlayerRegistry.Active;
            Transform nearest = null;
            float best = radius;
            foreach (var p in players)
            {
                if (p.TryGetComponent(out Health h) && h.IsDead) continue;
                if (p.TryGetComponent(out PlayerExtractionState ext) && ext.IsExtracted) continue;
                float d = Vector3.Distance(transform.position, p.transform.position);
                if (d > best) continue;
                if (!HasLineOfSight(p.transform)) continue;
                best = d;
                nearest = p.transform;
            }
            return nearest;
        }

        private bool TargetIsValid()
        {
            if (_target == null) return false;
            if (_target.TryGetComponent(out Health h) && h.IsDead) return false;
            if (_target.TryGetComponent(out PlayerExtractionState ext) && ext.IsExtracted) return false;
            if (Vector3.Distance(transform.position, _target.position) > _detectionRadius) return false;
            if (!HasLineOfSight(_target)) return false;
            return true;
        }
        
        /// <summary>Raycast entre los "ojos" de este enemigo y los del objetivo. True si no hay
        /// nada de por medio (Ground: paredes, cajas, piso).</summary>
        private bool HasLineOfSight(Transform target)
        {
            Vector3 origin = transform.position + Vector3.up * _eyeHeight;
            Vector3 targetPoint = target.position + Vector3.up * _eyeHeight;
            Vector3 offset = targetPoint - origin;
            float distance = offset.magnitude;
            if (distance < 0.01f) return true;

            return !Physics.Raycast(origin, offset / distance, distance, _visionBlockMask, QueryTriggerInteraction.Ignore);
        }
    }
}