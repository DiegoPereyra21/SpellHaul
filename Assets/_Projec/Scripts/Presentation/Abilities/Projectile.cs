using FishNet;
using FishNet.Object;
using FishNet.Managing.Timing;
using FishNet.Component.ColliderRollback;
using Game.Core.Abilities;
using Game.Presentation.Combat;
using UnityEngine;

namespace Game.Presentation.Abilities
{
    public class Projectile : NetworkBehaviour
    {
        [SerializeField] private float _lifetime = 4f;

        [Tooltip("Contra qué castea el proyectil. Debe incluir Hitbox (objetivos) y Ground (paredes/piso). " +
                 "Si queda vacío, se resuelve por nombre en Awake.")]
        [SerializeField] private LayerMask _hitMask;

        [Tooltip("Techo de ticks de catch-up (lag comp). A 60 Hz, 12 ≈ 200 ms de ping. Evita teleports enormes.")]
        [SerializeField] private int _maxCatchUpTicks = 12;
        [Tooltip("Techo de ticks de rewind (lag comp). El tick de disparo lo manda el cliente: sin techo, un cliente podía pedir impactos contra posiciones de hasta 1,25 s atrás (máximo del RollbackManager). A 60 Hz, 20 ≈ 333 ms (latencia + interpolación).")]
        [SerializeField] private int _maxRewindTicks = 20;
        [Tooltip("Hijo visual (mesh/trail) que se oculta al tirador (él ve su cosmético local).")]
        [SerializeField] private GameObject _visual;

        private Vector3 _direction;
        private float _speed;
        private float _damage;
        private float _radius;
        private int _casterNetworkId;
        private uint _fireTick;     // tick de disparo del cliente; 0 = server-originado (sin lag comp)
        private int _slot;          // slot casteado; -1 = sin habilidad asociada (enemigos), sin audio
        private float _aliveTime;

        private bool _initialized;
        private bool _caughtUp;

        // Buffer compartido para el OverlapSphere sin allocations. Los proyectiles corren
        // en el tick del server (single-thread), así que reutilizarlo secuencialmente es seguro.
        private static readonly Collider[] _overlapBuffer = new Collider[16];

        private void Awake()
        {
            // Red de seguridad: si el prefab todavía no tiene la máscara seteada, la resolvemos por nombre.
            if (_hitMask.value == 0)
                _hitMask = LayerMask.GetMask("Hitbox", "Ground");
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            // El tirador (owner) ve su proyectil cosmético local; el networked se le oculta.
            // Se setea en cada spawn por el pooling de Fish-Net (no basta con desactivar una vez).
            if (_visual != null)
                _visual.SetActive(!base.IsOwner);
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            base.TimeManager.OnTick += OnTick;
        }

        public override void OnStopServer()
        {
            base.OnStopServer();
            if (base.TimeManager != null)
                base.TimeManager.OnTick -= OnTick;
        }

        public void Initialize(Vector3 direction, float speed, float damage, float radius, int casterNetworkId, uint fireTick = 0, int slot = -1)
        {
            _direction   = direction.normalized;
            _speed       = speed;
            _damage      = damage;
            _radius      = radius;
            _casterNetworkId = casterNetworkId;
            _fireTick    = fireTick;
            _slot        = slot;
            _aliveTime   = 0f;
            _caughtUp    = false;
            _initialized = true;
        }

        private void OnTick()
        {
            if (!_initialized) return;

            // Catch-up una sola vez, en el primer tick tras el spawn (objeto ya spawneado → Despawn seguro).
            if (!_caughtUp)
            {
                _caughtUp = true;
                if (CatchUp()) return; // impactó durante el catch-up
            }

            float delta = (float)base.TimeManager.TickDelta;
            float stepDistance = _speed * delta;
            Vector3 startPos = transform.position;

            if (TryImpact(startPos, stepDistance)) return;

            transform.position = startPos + _direction * stepDistance;

            _aliveTime += delta;
            if (_aliveTime >= _lifetime)
                base.Despawn();
        }

        /// <summary>
        /// Lag compensation (nivel 2): compensa el retardo input→spawn adelantando el proyectil
        /// los ticks de latencia, y hace un rewind único al tick de disparo para el overlap inicial
        /// (objetivo point-blank / cruzando frente al cañón al disparar). Devuelve true si impactó.
        /// </summary>
        private bool CatchUp()
        {
            // Proyectiles de servidor (enemigos) no llevan fireTick → ya están en presente, sin lag comp.
            if (_fireTick == 0) return false;

            float tickDelta = (float)base.TimeManager.TickDelta;

            // Acotar el tick que dijo el cliente a [ahora - _maxRewindTicks, ahora].
            uint now = base.TimeManager.Tick;
            uint maxRewind = (uint)Mathf.Max(0, _maxRewindTicks);
            uint oldestAllowed = now > maxRewind ? now - maxRewind : 0u;
            uint rewindTick = _fireTick < oldestAllowed ? oldestAllowed : (_fireTick > now ? now : _fireTick);

            // Rewind único al tick de disparo: overlap en el cañón contra las posiciones históricas.
            RollbackManager rbm = base.NetworkManager != null ? base.NetworkManager.RollbackManager : null;
            if (rbm != null)
            {
                rbm.Rollback(new PreciseTick(rewindTick), RollbackPhysicsType.Physics, false);
                bool hit = TryImpact(transform.position, 0f); // solo overlap (sin sweep)
                rbm.Return();                                  // SIEMPRE restaurar antes de salir
                if (hit) return true;
            }

            // Catch-up: adelantar el proyectil los ticks de retardo (en presente), barriendo el tramo.
            long rawD = (long)base.TimeManager.Tick - _fireTick;
            int d = (int)Mathf.Clamp(rawD, 0, _maxCatchUpTicks);
            if (d <= 0) return false;

            float catchDist = _speed * tickDelta * d;
            if (TryImpact(transform.position, catchDist)) return true;

            transform.position += _direction * catchDist;
            return false;
        }

        /// <summary>
        /// Resuelve colisión desde fromPos: primero OverlapSphere (objetivos que ya solapan el origen),
        /// luego SphereCast por el tramo. Devuelve true si impactó (ya aplicó daño y despawneó).
        /// </summary>
        private bool TryImpact(Vector3 fromPos, float stepDistance)
        {
            int count = Physics.OverlapSphereNonAlloc(fromPos, _radius, _overlapBuffer, _hitMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                Collider col = _overlapBuffer[i];
                if (col.transform.IsChildOf(transform)) continue;               // el propio proyectil

                NetworkObject nob = col.GetComponentInParent<NetworkObject>();   // hittable es hijo; root tiene NObj/Health
                if (nob != null && nob.ObjectId == _casterNetworkId) continue;   // el caster

                IDamageable dmg = nob != null ? nob.GetComponent<IDamageable>() : null; // null = geometría (Ground)
                ResolveImpact(fromPos, -_direction, dmg);
                return true;
            }

            if (stepDistance > 0f &&
                Physics.SphereCast(fromPos, _radius, _direction, out RaycastHit hit, stepDistance, _hitMask, QueryTriggerInteraction.Ignore))
            {
                NetworkObject hitNob = hit.collider.GetComponentInParent<NetworkObject>();
                if (hitNob != null && hitNob.ObjectId == _casterNetworkId)
                    return false; // atravesar al caster; el que llama avanza el tramo

                IDamageable damageable = hitNob != null ? hitNob.GetComponent<IDamageable>() : null; // null = pared
                ResolveImpact(hit.point, hit.normal, damageable);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Aplica el daño (si golpeó algo con vida), reposiciona, notifica al caster
        /// (screenshake + VFX + hitmarker + audio de impacto) y despawnea.
        /// </summary>
        private void ResolveImpact(Vector3 point, Vector3 normal, IDamageable damageable)
        {
            bool hitConfirmed = damageable != null; // false = pegó en geometría (Ground)
            bool isKill = false;

            if (damageable != null)
            {
                damageable.ApplyDamage(_damage, _casterNetworkId);
                if (damageable is Health health) // único implementador de IDamageable
                    isKill = health.IsDead;
            }

            transform.position = point;

            if (InstanceFinder.ServerManager.Objects.Spawned.TryGetValue(_casterNetworkId, out NetworkObject casterNob) &&
                casterNob.TryGetComponent(out AbilityController ac))
            {
                ac.NotifyProjectileImpact(point, normal, hitConfirmed, isKill);
                ac.NotifyAbilityImpactSfx(point, _slot, wallHit: !hitConfirmed);
            }

            base.Despawn();
        }
    }
}