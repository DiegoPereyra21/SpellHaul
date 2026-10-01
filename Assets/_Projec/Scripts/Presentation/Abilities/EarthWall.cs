using FishNet.Object;
using FishNet.Object.Synchronizing;
using Game.Presentation.Combat;
using UnityEngine;

namespace Game.Presentation.Abilities
{
    /// <summary>
    /// Muro de tierra (EarthWallAbilitySO). Server-authoritative: dura un tiempo y se rompe al
    /// quedarse sin vida (Health en el mismo objeto). Sube del suelo al aparecer y se hunde al
    /// terminar; esa animación la hace cada lado localmente a partir de cuándo empezó.
    ///
    /// Estructura del prefab: root (NetworkObject + EarthWall + Health) y un hijo "Body" con el
    /// mesh y el collider sólido en la capa Ground (frena movimiento, proyectiles y visión), con un
    /// hijo en la capa Hitbox algo más grande (recibe el daño). Lo que sube y baja es Body.
    /// </summary>
    [RequireComponent(typeof(Health))]
    public class EarthWall : NetworkBehaviour
    {
        [Tooltip("Hijo que sube del suelo (mesh + colliders).")]
        [SerializeField] private Transform _body;
        [Tooltip("Segundos que tarda en subir del suelo.")]
        [SerializeField] private float _riseTime = 0.25f;
        [Tooltip("Segundos que tarda en hundirse al terminar o romperse.")]
        [SerializeField] private float _sinkTime = 0.2f;
        [Tooltip("Sonido al levantarse (opcional).")]
        [SerializeField] private AudioClip _riseClip;
        [Tooltip("Sonido al romperse (opcional).")]
        [SerializeField] private AudioClip _breakClip;

        // True desde que el muro empieza a hundirse (venció o se rompió): todos animan el hundimiento.
        private readonly SyncVar<bool> _sinking = new SyncVar<bool>(false);

        private Health _health;
        private Vector3 _bodyRestPosition;
        private float _bodyHeight = 2.5f;
        private float _endTime;
        private float _riseStart;
        private float _sinkStart;
        private bool _initialized;
        private bool _ending;

        private void Awake()
        {
            _health = GetComponent<Health>();
            if (_body == null && transform.childCount > 0) _body = transform.GetChild(0);
            if (_body != null)
            {
                _bodyRestPosition = _body.localPosition;
                // Alto del muro: lo que hay que bajarlo para que quede enterrado del todo.
                var rend = _body.GetComponentInChildren<Renderer>();
                if (rend != null) _bodyHeight = Mathf.Max(0.5f, rend.bounds.size.y);

                // Los NavMeshAgent ignoran los colliders: sin obstáculo que talle el NavMesh, los
                // enemigos melee atravesaban el muro. Se agrega solo si el prefab no lo trae.
                if (!_body.TryGetComponent(out UnityEngine.AI.NavMeshObstacle obstacle))
                {
                    obstacle = _body.gameObject.AddComponent<UnityEngine.AI.NavMeshObstacle>();
                    obstacle.shape = UnityEngine.AI.NavMeshObstacleShape.Box;
                    obstacle.center = Vector3.zero;
                    obstacle.size = Vector3.one; // en espacio local del Body (cubo escalado)
                }
                obstacle.carving = true;
                obstacle.carveOnlyStationary = false;
            }
        }

        public override void OnStartNetwork()
        {
            base.OnStartNetwork();
            // Instancia nueva o reutilizada del pool: arranca enterrado y sube.
            _riseStart = Time.time;
            _sinkStart = 0f;
            SetBodyOffset(-_bodyHeight);
            _sinking.OnChange += OnSinkingChanged;
            if (_riseClip != null) VFXManager.PlaySfx(_riseClip, transform.position);
        }

        public override void OnStopNetwork()
        {
            base.OnStopNetwork();
            _sinking.OnChange -= OnSinkingChanged;
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            _ending = false;
            _sinking.Value = false;
            if (_health != null) _health.OnDied += HandleBroken;
        }

        public override void OnStopServer()
        {
            base.OnStopServer();
            if (_health != null) _health.OnDied -= HandleBroken;
            _initialized = false;
        }

        [Server]
        public void ServerInitialize(float health, float duration)
        {
            _health?.ServerSetMaxHealth(health);
            _endTime = Time.time + Mathf.Max(0.5f, duration);
            _initialized = true;
        }

        private void HandleBroken(int instigator)
        {
            BrokenObserversRpc();
            BeginEnd();
        }

        [ObserversRpc]
        private void BrokenObserversRpc()
        {
            if (_breakClip != null) VFXManager.PlaySfx(_breakClip, transform.position + Vector3.up);
            VFXManager.PlayOrbExplosion(transform.position + Vector3.up);
        }

        [Server]
        private void BeginEnd()
        {
            if (_ending) return;
            _ending = true;
            _sinking.Value = true;
            _sinkStart = Time.time;
        }

        private void OnSinkingChanged(bool prev, bool next, bool asServer)
        {
            if (next && !asServer) _sinkStart = Time.time;
        }

        private void Update()
        {
            // Animación (servidor y clientes): sube, se queda, se hunde.
            if (_sinking.Value)
            {
                float k = _sinkTime > 0f ? Mathf.Clamp01((Time.time - _sinkStart) / _sinkTime) : 1f;
                SetBodyOffset(-_bodyHeight * k);
            }
            else
            {
                float k = _riseTime > 0f ? Mathf.Clamp01((Time.time - _riseStart) / _riseTime) : 1f;
                float eased = 1f - (1f - k) * (1f - k);
                SetBodyOffset(-_bodyHeight * (1f - eased));
            }

            if (!base.IsServerStarted || !_initialized) return;
            if (!_ending && Time.time >= _endTime) BeginEnd();
            if (_ending && Time.time - _sinkStart >= _sinkTime + 0.05f)
            {
                _initialized = false;
                base.Despawn();
            }
        }

        private void SetBodyOffset(float y)
        {
            if (_body != null) _body.localPosition = _bodyRestPosition + Vector3.up * y;
        }
    }
}
