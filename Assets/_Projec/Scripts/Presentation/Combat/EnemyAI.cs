using FishNet.Object;
using UnityEngine;
using UnityEngine.AI;

namespace Game.Presentation.Combat
{
    /// <summary>
    /// IA de enemigo básica, server-authoritative. FSM: Idle (patrulla cerca del spawn), Chase (persigue
    /// al jugador más cercano con línea de visión) y Attack (golpe con windup). El NavMeshAgent solo se mueve en
    /// el servidor; el NetworkTransform replica la posición a los clientes.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public class EnemyAI : NetworkBehaviour
    {
        private enum State { Idle, Chase, Attack }

        [SerializeField] private float _detectionRadius = 12f;
        [SerializeField] private float _loseRadius = 16f;      // un poco mayor: histéresis para no titilar
        [SerializeField] private float _moveSpeed = 3.5f;
        [SerializeField] private float _repathInterval = 0.2f; // cada cuánto recalcula el destino
        [Header("Patrullaje")]
        [SerializeField] private float _patrolRadius = 6f;
        [SerializeField] private float _patrolSpeed = 1.8f;
        [SerializeField] private float _patrolWaitMin = 2f;
        [SerializeField] private float _patrolWaitMax = 5f;
        [Header("Visión")]
        [SerializeField] private LayerMask _visionBlockMask;   // qué bloquea la línea de visión (default: Ground)
        [SerializeField] private float _eyeHeight = 1.6f;
        [Header("Attack")]
        [SerializeField] private float _attackRange = 2f;
        [SerializeField] private float _attackWindup = 0.4f;   // telegrafía antes del golpe
        [SerializeField] private float _attackCooldown = 1.5f; // tiempo entre golpes
        [SerializeField] private float _attackDamage = 15f;

        private NavMeshAgent _agent;
        private State _state = State.Idle;
        private Transform _target;
        private float _repathTimer;
        private Health _health;
        private float _attackTimer;   // cooldown entre ataques
        private float _windupTimer;   // cuenta regresiva del windup
        private bool _winding;        // está preparando un golpe

        private Vector3 _spawnPosition;
        private bool _hasPatrolTarget;
        private float _patrolWaitTimer;
        private void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
            _health = GetComponent<Health>();
            if (_visionBlockMask == 0) _visionBlockMask = LayerMask.GetMask("Ground");
        }

        public override void OnStartServer()
        {
            _spawnPosition = transform.position;
            _agent.speed = _moveSpeed;
            _agent.enabled = true; // el agent solo vive en el servidor
            _health.OnDied += HandleDied;
        }

        public override void OnStartClient()
        {
            // En clientes puros (no host), el agent no debe controlar la posición:
            // la posición llega por NetworkTransform. Desactivarlo si no somos servidor.
            if (!base.IsServerInitialized)
                _agent.enabled = false;
        }

        public override void OnStopServer()
        {
            if (_health != null) _health.OnDied -= HandleDied;
        }

        private void HandleDied(int instigator)
        {
            _state = State.Idle;
            if (_agent != null && _agent.isOnNavMesh) _agent.ResetPath();
            Despawn();
        }

        private void Update()
        {
            if (!base.IsServerInitialized) return; // la IA corre SOLO en el servidor

            switch (_state)
            {
                case State.Idle: TickIdle(); break;
                case State.Chase: TickChase(); break;
                case State.Attack: TickAttack(); break;
            }
        }

        private void TickIdle()
        {
            Transform nearest = FindNearestPlayer(_detectionRadius);
            if (nearest != null)
            {
                _target = nearest;
                _state = State.Chase;
                _agent.speed = _moveSpeed;
                return;
            }

            TickPatrol();
        }

        /// <summary>Deambula cerca del punto de spawn mientras no hay objetivo: camina a un punto
        /// aleatorio dentro de _patrolRadius, espera un rato, repite.</summary>
        private void TickPatrol()
        {
            _agent.speed = _patrolSpeed;

            if (_hasPatrolTarget)
            {
                if (!_agent.pathPending && _agent.remainingDistance <= _agent.stoppingDistance)
                {
                    _hasPatrolTarget = false;
                    _patrolWaitTimer = Random.Range(_patrolWaitMin, _patrolWaitMax);
                }
                return;
            }

            _patrolWaitTimer -= Time.deltaTime;
            if (_patrolWaitTimer > 0f) return;

            Vector3 randomPoint = _spawnPosition + Random.insideUnitSphere * _patrolRadius;
            if (_agent.isOnNavMesh && NavMesh.SamplePosition(randomPoint, out NavMeshHit hit, _patrolRadius, NavMesh.AllAreas))
            {
                _agent.SetDestination(hit.position);
                _hasPatrolTarget = true;
            }
        }

        private void TickChase()
        {
            if (!TargetIsValid() || DistanceTo(_target) > _loseRadius)
            {
                _target = null;
                _state = State.Idle;
                if (_agent.isOnNavMesh) _agent.ResetPath();
                return;
            }

            // Si está en rango de ataque, pasar a Attack.
            if (DistanceTo(_target) <= _attackRange)
            {
                _state = State.Attack;
                _winding = false;
                if (_agent.isOnNavMesh) _agent.ResetPath();
                return;
            }

            _repathTimer -= Time.deltaTime;
            if (_repathTimer <= 0f)
            {
                _repathTimer = _repathInterval;
                if (_agent.isOnNavMesh)
                    _agent.SetDestination(_target.position);
            }
        }

        private void TickAttack()
        {
            // Si el objetivo se fue o se alejó del rango de ataque, volver a perseguir.
            if (!TargetIsValid() || DistanceTo(_target) > _attackRange)
            {
                _winding = false;
                _state = _target != null ? State.Chase : State.Idle;
                return;
            }

            // Mirar hacia el objetivo (rotación plana, sin inclinarse).
            Vector3 dir = _target.position - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(dir), Time.deltaTime * 8f);

            _attackTimer -= Time.deltaTime;

            if (!_winding)
            {
                // Iniciar un golpe si el cooldown lo permite.
                if (_attackTimer <= 0f)
                {
                    _winding = true;
                    _windupTimer = _attackWindup;
                }
            }
            else
            {
                // En windup: contar hasta el impacto.
                _windupTimer -= Time.deltaTime;
                if (_windupTimer <= 0f)
                {
                    _winding = false;
                    _attackTimer = _attackCooldown;
                    TryLandHit();
                }
            }
        }

        private Transform FindNearestPlayer(float radius)
        {
            var players = Game.Presentation.Player.PlayerRegistry.Active;
            Transform nearest = null;
            float best = radius;
            foreach (var p in players)
            {
                if (p.TryGetComponent(out Health h) && h.IsDead) continue;
                if (p.TryGetComponent(out Game.Presentation.Combat.PlayerExtractionState ext) && ext.IsExtracted) continue;

                float d = Vector3.Distance(transform.position, p.transform.position);
                if (d > best) continue;
                if (!HasLineOfSight(p.transform)) continue;

                best = d;
                nearest = p.transform;
            }
            return nearest;
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

        private bool TargetIsValid()
        {
            if (_target == null) return false;
            if (_target.TryGetComponent(out Health h) && h.IsDead) return false;

            // Ignorar jugadores que ya extrajeron (salieron de la run).
            if (_target.TryGetComponent(out Game.Presentation.Combat.PlayerExtractionState ext) && ext.IsExtracted)
                return false;

            return true;
        }

        private void TryLandHit()
        {
            if (!TargetIsValid() || DistanceTo(_target) > _attackRange) return;

            if (_target.TryGetComponent(out Game.Core.Abilities.IDamageable dmg))
                dmg.ApplyDamage(_attackDamage, base.ObjectId);
        }

        private float DistanceTo(Transform t) => Vector3.Distance(transform.position, t.position);
    }
}