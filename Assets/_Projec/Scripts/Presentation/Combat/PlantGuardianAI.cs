using System.Collections;
using FishNet;
using FishNet.Object;
using Game.Presentation.Abilities;
using UnityEngine;

namespace Game.Presentation.Combat
{
    /// <summary>
    /// Stationary turret enemy with two attack phases based on distance.
    /// Far: lobs a ballistic projectile at the player's predicted position.
    /// Close: fires bursts of straight projectiles (burst, rest, repeat).
    /// Server-authoritative; does not move or use NavMesh.
    /// </summary>
    [RequireComponent(typeof(Health))]
    public class PlantGuardianAI : NetworkBehaviour
    {
        private enum State { Idle, CloseBurst, FarLob }

        [Header("Detection")]
        [SerializeField] private float _detectionRadius = 20f;
        [SerializeField] private float _closeRangeRadius = 8f; // inside this: burst phase
        [SerializeField] private LayerMask _visionBlockMask;   // qué bloquea la línea de visión (default: Ground)
        [SerializeField] private float _eyeHeight = 1.5f;

        [Header("Close Phase (burst)")]
        [SerializeField] private GameObject _burstProjectilePrefab;
        [SerializeField] private int _burstCount = 3;
        [SerializeField] private float _burstInterval = 0.25f;  // between shots in a burst
        [SerializeField] private float _burstRest = 1.5f;       // pause after a burst
        [SerializeField] private float _burstProjectileSpeed = 14f;
        [SerializeField] private float _burstProjectileDamage = 8f;
        [SerializeField] private float _burstProjectileRadius = 0.25f;

        [Header("Far Phase (lob)")]
        [SerializeField] private GameObject _lobProjectilePrefab;
        [SerializeField] private float _lobCooldown = 3f;
        [SerializeField] private float _lobArcHeight = 6f;      // peak height of the arc
        [SerializeField] private float _lobFlightTime = 1.5f;   // seconds to reach the target
        [SerializeField] private float _lobDamage = 15f;
        [SerializeField] private float _lobLeadFactor = 1f;     // 1 = full velocity prediction

        [Header("Aiming")]
        [SerializeField] private float _turnSpeed = 4f;
        [SerializeField] private float _muzzleHeight = 1.5f;

        private Health _health;
        private Transform _target;
        private State _state = State.Idle;
        private Coroutine _attackRoutine;

        private void Awake()
        {
            _health = GetComponent<Health>();
            if (_visionBlockMask == 0) _visionBlockMask = LayerMask.GetMask("Ground");
        }
        public override void OnStartServer()
        {
            _health.OnDied += HandleDied;
        }

        public override void OnStopServer()
        {
            if (_health != null) _health.OnDied -= HandleDied;
        }

        private void HandleDied(int instigator)
        {
            StopAttack();
            enabled = false;
            Despawn();
        }

        private void Update()
        {
            if (!base.IsServerStarted) return;

            // Cegado (orbe de destello): sin objetivo ni ataques hasta recuperar la vista.
            if (Blindness.IsBlindNow(this))
            {
                _target = null;
                SetState(State.Idle);
                return;
            }

            // Acquire / validate target.
            if (!TargetIsValid())
            {
                _target = FindNearestPlayer(_detectionRadius);
                if (_target == null)
                {
                    SetState(State.Idle);
                    return;
                }
            }

            // Face the target (flat rotation).
            Vector3 dir = _target.position - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.Slerp(transform.rotation,
                    Quaternion.LookRotation(dir), Time.deltaTime * _turnSpeed);

            // Phase by distance.
            float distance = Vector3.Distance(transform.position, _target.position);
            SetState(distance <= _closeRangeRadius ? State.CloseBurst : State.FarLob);
        }

        private void SetState(State next)
        {
            if (_state == next) return;
            _state = next;
            StopAttack();

            switch (_state)
            {
                case State.CloseBurst:
                    _attackRoutine = StartCoroutine(BurstLoop());
                    break;
                case State.FarLob:
                    _attackRoutine = StartCoroutine(LobLoop());
                    break;
            }
        }

        private void StopAttack()
        {
            if (_attackRoutine != null)
            {
                StopCoroutine(_attackRoutine);
                _attackRoutine = null;
            }
        }

        // ---------- Close phase: burst of straight shots ----------

        [Server]
        private IEnumerator BurstLoop()
        {
            while (true)
            {
                for (int i = 0; i < _burstCount; i++)
                {
                    FireStraightShot();
                    yield return new WaitForSeconds(_burstInterval);
                }
                yield return new WaitForSeconds(_burstRest);
            }
        }

        [Server]
        private void FireStraightShot()
        {
            if (_burstProjectilePrefab == null || _target == null) return;

            Vector3 origin = transform.position + Vector3.up * _muzzleHeight;
            Vector3 dir = (_target.position + Vector3.up * 1f - origin).normalized;

            // Pool nativo de Fish-Net (el prefab tiene Default Despawn Type = Pool): reutiliza
            // instancias en vez de Instantiate/Destroy por disparo.
            NetworkObject nob = InstanceFinder.NetworkManager.GetPooledInstantiated(
                _burstProjectilePrefab.GetComponent<NetworkObject>(), origin, Quaternion.LookRotation(dir), true);

            if (nob.TryGetComponent(out Projectile projectile))
            {
                projectile.Initialize(dir, _burstProjectileSpeed, _burstProjectileDamage,
                    _burstProjectileRadius, base.ObjectId);
                InstanceFinder.ServerManager.Spawn(nob);
            }
        }

        // ---------- Far phase: ballistic lob (Step 2) ----------

        [Server]
        private IEnumerator LobLoop()
        {
            while (true)
            {
                FireLob();
                yield return new WaitForSeconds(_lobCooldown);
            }
        }

        [Server]
        private void FireLob()
        {
            if (_lobProjectilePrefab == null || _target == null) return;

            Vector3 predictedPoint = _target.position;
            if (_target.TryGetComponent(out CharacterController cc))
            {
                // Clamp prediction: ignore unrealistic velocities and cap lead distance.
                Vector3 vel = cc.velocity;
                vel.y = 0f; // don't predict vertical (jumps/falls throw the arc off)

                float maxLead = 6f; // meters
                Vector3 lead = vel * _lobFlightTime * _lobLeadFactor;
                if (lead.magnitude > maxLead)
                    lead = lead.normalized * maxLead;

                predictedPoint += lead;
            }

            Vector3 origin = transform.position + Vector3.up * _muzzleHeight;

            NetworkObject nob = InstanceFinder.NetworkManager.GetPooledInstantiated(
                _lobProjectilePrefab.GetComponent<NetworkObject>(), origin, Quaternion.identity, true);

            if (nob.TryGetComponent(out LobProjectile lob))
            {
                InstanceFinder.ServerManager.Spawn(nob);
                lob.Initialize(predictedPoint, _lobArcHeight, _lobFlightTime, _lobDamage, base.ObjectId);
            }
        }

        // ---------- Targeting ----------

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