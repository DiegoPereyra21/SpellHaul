using FishNet;
using FishNet.Object;
using FishNet.Managing.Timing;
using FishNet.Component.ColliderRollback;
using Game.Core.Abilities;
using Game.Presentation.Combat;
using Game.Presentation.Player;
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
        [Tooltip("Techo de rewind contra JUGADORES (contra enemigos de la IA se usa _maxRewindTicks). Cubre el ping normal sin estirarse a latencias altas. A 60 Hz, 7 ≈ 117 ms. Además, un jugador que en el presente ya está detrás de una pared no recibe el golpe.")]
        [SerializeField] private int _maxPlayerRewindTicks = 7;
        [Tooltip("Qué cuenta como cobertura para la regla de la cobertura. Si queda vacío se usa Ground.")]
        [SerializeField] private LayerMask _coverMask;
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

        // Lag compensation durante TODO el vuelo: el proyectil choca contra las posiciones que el
        // tirador veía (presente - _viewDelayTicks), no contra las del servidor. Antes solo se
        // rebobinaba en el primer tick: contra un enemigo moviéndose de costado, el cliente veía
        // el impacto (su proyectil cosmético pegaba donde él lo veía) y el real pasaba de largo.
        private uint _viewDelayTicks;

        private static readonly RaycastHit[] _castBuffer = new RaycastHit[16];

        // Buffer compartido para el OverlapSphere sin allocations. Los proyectiles corren
        // en el tick del server (single-thread), así que reutilizarlo secuencialmente es seguro.
        private static readonly Collider[] _overlapBuffer = new Collider[16];

        private void Awake()
        {
            // Red de seguridad: si el prefab todavía no tiene la máscara seteada, la resolvemos por nombre.
            if (_hitMask.value == 0)
                _hitMask = LayerMask.GetMask("Hitbox", "Ground");
            if (_coverMask.value == 0)
                _coverMask = LayerMask.GetMask("Ground");
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            // El tirador (owner) ve su proyectil cosmético local; el networked se le oculta.
            // Se setea en cada spawn por el pooling de Fish-Net (no basta con desactivar una vez).
            if (_visual != null)
                _visual.SetActive(!base.IsOwner);
            _clientFlying = false;
        }

        public override void OnStopClient()
        {
            base.OnStopClient();
            _clientFlying = false;
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            base.TimeManager.OnTick += OnTick;

            // Los clientes simulan el vuelo (línea recta) en vez de recibir la posición cada tick:
            // menos tráfico y, sobre todo, lo ven donde está AHORA en el servidor (no ~100 ms atrás
            // por la interpolación), así esquivar los disparos enemigos es justo.
            uint now = base.TimeManager.Tick;
            float lead = 0f;
            if (_fireTick != 0 && now > _fireTick)
                lead = _speed * (float)base.TimeManager.TickDelta * Mathf.Min(now - _fireTick, (uint)Mathf.Max(0, _maxCatchUpTicks));
            FlightObserversRpc(transform.position, _direction, _speed, now, lead);
        }

        // ---------- Vuelo simulado en los clientes (solo visual; el impacto lo decide el servidor) ----------

        private bool _clientFlying;
        private bool _clientStopped;
        private Vector3 _clientOrigin;
        private Vector3 _clientDirection;
        private float _clientSpeed;
        private float _clientLead;
        private float _clientElapsed;

        [ObserversRpc(BufferLast = true, ExcludeServer = true)]
        private void FlightObserversRpc(Vector3 origin, Vector3 direction, float speed, uint spawnTick, float lead)
        {
            // Con NetworkTransform en el prefab la posición ya llega por la red: no pisarla.
            if (TryGetComponent(out FishNet.Component.Transforming.NetworkTransform _)) return;

            _clientOrigin = origin;
            _clientDirection = direction.sqrMagnitude > 0.0001f ? direction.normalized : transform.forward;
            _clientSpeed = speed;
            _clientLead = lead;

            // Lo que ya voló en el servidor desde que nació (latencia), acotado.
            uint now = base.TimeManager.Tick;
            float since = now > spawnTick ? (float)base.TimeManager.TicksToTime(now - spawnTick) : 0f;
            _clientElapsed = Mathf.Clamp(since, 0f, 0.5f);

            _clientStopped = false;
            _clientFlying = true;
            transform.rotation = Quaternion.LookRotation(_clientDirection);
            transform.position = FlightPosition();
        }

        private Vector3 FlightPosition() => _clientOrigin + _clientDirection * (_clientLead + _clientSpeed * _clientElapsed);

        private void Update()
        {
            if (!_clientFlying || _clientStopped || base.IsServerInitialized) return;

            Vector3 from = transform.position;
            _clientElapsed += Time.deltaTime;
            Vector3 to = FlightPosition();

            // Frena visualmente en paredes; el despawn real (y el VFX de impacto) lo manda el servidor.
            if (Physics.Linecast(from, to, out RaycastHit hit, _coverMask, QueryTriggerInteraction.Ignore))
            {
                transform.position = hit.point;
                _clientStopped = true;
                return;
            }

            transform.position = to;
            if (_clientElapsed > _lifetime + 0.5f) _clientStopped = true;
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
            _viewDelayTicks = 0;
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

            if (TryImpactCompensated(startPos, stepDistance)) return;

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

            // Cuánto atrasado ve el mundo el tirador: se mantiene todo el vuelo.
            _viewDelayTicks = now - rewindTick;

            // Overlap en el cañón al tick de disparo (objetivo point-blank / cruzando al disparar).
            if (TryImpactCompensated(transform.position, 0f)) return true;

            // Catch-up: adelantar el proyectil los ticks de retardo, barriendo el tramo contra las
            // posiciones que veía el tirador.
            long rawD = (long)base.TimeManager.Tick - _fireTick;
            int d = (int)Mathf.Clamp(rawD, 0, _maxCatchUpTicks);
            if (d <= 0) return false;

            float catchDist = _speed * tickDelta * d;
            if (TryImpactCompensated(transform.position, catchDist)) return true;

            transform.position += _direction * catchDist;
            return false;
        }

        /// <summary>
        /// Lag compensation híbrida (ver _maxPlayerRewindTicks):
        /// - Contra enemigos de la IA (y paredes) se usa todo el atraso del tirador: si en su
        ///   pantalla le pegó, cuenta.
        /// - Contra jugadores el atraso se acota a _maxPlayerRewindTicks (ping normal) y además rige
        ///   la regla de la cobertura: si en el presente la víctima ya está detrás de una pared, el
        ///   golpe no cuenta. Nadie recibe un hechizo después de haberse cubierto.
        /// El daño y el despawn se resuelven después de volver al presente.
        /// </summary>
        private bool TryImpactCompensated(Vector3 fromPos, float stepDistance)
        {
            RollbackManager rbm = base.NetworkManager != null ? base.NetworkManager.RollbackManager : null;
            if (_fireTick == 0 || _viewDelayTicks == 0 || rbm == null)
                return TryImpact(fromPos, stepDistance);

            uint fullDelay = _viewDelayTicks;
            uint playerDelay = (uint)Mathf.Clamp(_maxPlayerRewindTicks, 0, (int)fullDelay);

            bool found;
            ImpactInfo impact;
            if (playerDelay == fullDelay)
            {
                found = FindImpactAt(rbm, fullDelay, fromPos, stepDistance, HitFilter.Any, out impact);
            }
            else
            {
                // Dos mundos: enemigos donde los veía el tirador; jugadores con el atraso acotado.
                bool e = FindImpactAt(rbm, fullDelay, fromPos, stepDistance, HitFilter.NonPlayers, out ImpactInfo ie);
                bool pl = FindImpactAt(rbm, playerDelay, fromPos, stepDistance, HitFilter.PlayersAndGeometry, out ImpactInfo ip);
                found = e || pl;
                impact = e && pl ? (ip.Distance < ie.Distance ? ip : ie) : (pl ? ip : ie);
            }

            if (!found) return false;

            // Regla de la cobertura (en el presente): si la víctima ya se cubrió, sigue de largo; el
            // proyectil continúa y, si corresponde, choca con la pared en su propio recorrido.
            if (impact.IsPlayer && IsBehindCoverNow(fromPos, impact.Collider)) return false;

            ResolveImpact(impact.Point, impact.Normal, impact.Target);
            return true;
        }

        /// <summary>FindImpact con los objetivos rebobinados delay ticks (0 = presente). Siempre restaura.</summary>
        private bool FindImpactAt(RollbackManager rbm, uint delay, Vector3 fromPos, float stepDistance, HitFilter filter, out ImpactInfo impact)
        {
            if (delay == 0) return FindImpact(fromPos, stepDistance, filter, out impact);

            uint now = base.TimeManager.Tick;
            uint viewTick = now > delay ? now - delay : 0u;
            rbm.Rollback(new PreciseTick(viewTick), RollbackPhysicsType.Physics, false);
            bool found = FindImpact(fromPos, stepDistance, filter, out impact);
            rbm.Return();
            return found;
        }

        /// <summary>True si entre el proyectil y la víctima (posición actual) hay geometría.</summary>
        private bool IsBehindCoverNow(Vector3 fromPos, Collider victimCollider)
        {
            if (victimCollider == null) return false;
            Vector3 victimCenter = victimCollider.bounds.center; // ya en el presente (Return hecho)
            return Physics.Linecast(fromPos, victimCenter, _coverMask, QueryTriggerInteraction.Ignore);
        }

        /// <summary>
        /// Resuelve colisión desde fromPos: primero OverlapSphere (objetivos que ya solapan el origen),
        /// luego SphereCast por el tramo. Devuelve true si impactó (ya aplicó daño y despawneó).
        /// Sin compensación: todo en el presente.
        /// </summary>
        private bool TryImpact(Vector3 fromPos, float stepDistance)
        {
            if (!FindImpact(fromPos, stepDistance, HitFilter.Any, out ImpactInfo impact))
                return false;
            ResolveImpact(impact.Point, impact.Normal, impact.Target);
            return true;
        }

        private enum HitFilter { Any, NonPlayers, PlayersAndGeometry }

        private struct ImpactInfo
        {
            public Vector3 Point, Normal;
            public IDamageable Target;   // null = geometría (Ground)
            public Collider Collider;
            public float Distance;       // a lo largo del tramo (0 = ya solapaba)
            public bool IsPlayer;
        }

        private static bool IsPlayerCollider(Collider col) => col.GetComponentInParent<PlayerAvatarState>() != null;

        private static bool Accepts(HitFilter filter, bool isPlayer, bool isGeometry) => filter switch
        {
            HitFilter.NonPlayers => !isPlayer,
            HitFilter.PlayersAndGeometry => isPlayer || isGeometry,
            _ => true,
        };

        /// <summary>
        /// Busca colisión desde fromPos sin aplicar nada: primero OverlapSphere (objetivos que ya
        /// solapan el origen), luego el impacto más cercano del SphereCast por el tramo. Ignora al
        /// propio proyectil, al caster y lo que el filtro descarte.
        /// </summary>
        private bool FindImpact(Vector3 fromPos, float stepDistance, HitFilter filter, out ImpactInfo impact)
        {
            impact = default;

            int count = Physics.OverlapSphereNonAlloc(fromPos, _radius, _overlapBuffer, _hitMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                Collider col = _overlapBuffer[i];
                if (col.transform.IsChildOf(transform)) continue;               // el propio proyectil

                NetworkObject nob = col.GetComponentInParent<NetworkObject>();   // hittable es hijo; root tiene NObj/Health
                if (nob != null && nob.ObjectId == _casterNetworkId) continue;   // el caster

                IDamageable dmg = nob != null ? nob.GetComponent<IDamageable>() : null; // null = geometría (Ground)
                bool isPlayer = dmg != null && IsPlayerCollider(col);
                if (!Accepts(filter, isPlayer, dmg == null)) continue;

                impact = new ImpactInfo { Point = fromPos, Normal = -_direction, Target = dmg, Collider = col, Distance = 0f, IsPlayer = isPlayer };
                return true;
            }

            if (stepDistance <= 0f) return false;

            int hits = Physics.SphereCastNonAlloc(fromPos, _radius, _direction, _castBuffer, stepDistance, _hitMask, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            bool found = false;
            for (int i = 0; i < hits; i++)
            {
                RaycastHit hit = _castBuffer[i];
                if (hit.distance >= best) continue;
                if (hit.collider.transform.IsChildOf(transform)) continue;

                NetworkObject hitNob = hit.collider.GetComponentInParent<NetworkObject>();
                if (hitNob != null && hitNob.ObjectId == _casterNetworkId) continue; // atraviesa al caster

                IDamageable dmg = hitNob != null ? hitNob.GetComponent<IDamageable>() : null; // null = pared
                bool isPlayer = dmg != null && IsPlayerCollider(hit.collider);
                if (!Accepts(filter, isPlayer, dmg == null)) continue;

                best = hit.distance;
                impact = new ImpactInfo { Point = hit.point, Normal = hit.normal, Target = dmg, Collider = hit.collider, Distance = hit.distance, IsPlayer = isPlayer };
                found = true;
            }
            return found;
        }

        /// <summary>
        /// Aplica el daño (si golpeó algo con vida), reposiciona, notifica al caster
        /// (screenshake + VFX + hitmarker + audio de impacto) y despawnea.
        /// </summary>
        private void ResolveImpact(Vector3 point, Vector3 normal, IDamageable damageable)
        {
            bool hitConfirmed = damageable != null; // false = pegó en geometría (Ground)
            bool isKill = false;
            float dealt = 0f;

            if (damageable != null)
            {
                float before = damageable is Health h0 ? h0.Current : 0f;
                damageable.ApplyDamage(_damage, _casterNetworkId);
                if (damageable is Health health) // único implementador de IDamageable
                {
                    isKill = health.IsDead;
                    dealt = Mathf.Max(0f, before - health.Current); // ya con la protección del objetivo
                }
            }

            transform.position = point;

            if (InstanceFinder.ServerManager.Objects.Spawned.TryGetValue(_casterNetworkId, out NetworkObject casterNob) &&
                casterNob.TryGetComponent(out AbilityController ac))
            {
                ac.NotifyProjectileImpact(point, normal, hitConfirmed, isKill, dealt);
                ac.NotifyAbilityImpactSfx(point, _slot, wallHit: !hitConfirmed);
            }

            base.Despawn();
        }
    }
}