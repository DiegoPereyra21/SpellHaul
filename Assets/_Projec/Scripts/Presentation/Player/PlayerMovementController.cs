using FishNet.Object;
using FishNet.Object.Prediction;
using Game.Presentation.Abilities;
using Game.Presentation.Combat;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Presentation.Player
{
    /// <summary>
    /// Movimiento FPS predicho/reconciliado con Fish-Net Prediction v2 sobre CharacterController.
    /// CharacterController no es un rigidbody, así que NO se usa PredictionRigidbody: el estado
    /// se reconcilia manualmente (posición, velocidad vertical, rotación).
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class PlayerMovementController : NetworkBehaviour
    {
        [Header("Movimiento")]
        [SerializeField] private PlayerStats _stats;
        [SerializeField] private float _moveSpeed = 6f;
        [SerializeField] private float _sprintMultiplier = 1.5f;
        [SerializeField] private float _jumpForce = 6f;
        [SerializeField] private float _gravity = -20f;

        private CharacterController _controller;
        private InputAction _moveAction;
        private InputAction _jumpAction;
        private InputAction _sprintAction;
        private InputAction _lookAction;
        private PlayerControls _controls;
        private Vector3 _verticalVelocity;
        private Vector3 _dashVelocity;
        private float _dashTimeRemaining;
        private float _dashDuration;

        // Solicitud de dash pendiente (seteada por StartDash en el server, aplicada en el próximo tick).
        private Vector3 _pendingDashDir;
        private float _pendingDashSpeed;
        private float _pendingDashDuration;
        private bool _dashRequested;
        private bool _jumpQueued;

        // Dash como input predicho (ver QueueDashInput): viaja en ReplicateData.
        private AbilityController _abilities;
        private bool _dashInputQueued;
        private byte _queuedDashSlot;
        private Vector3 _queuedDashDirection;

        // isGrounded del CharacterController no sobrevive a un reconcile (al reactivarlo queda en
        // false hasta el próximo Move), así que el replay del primer tick veía "en el aire":
        // saltos que no salían y gravedad de más → tirones al despegar y al aterrizar. Se guarda
        // acá y viaja en el ReconcileData.
        private bool _grounded;
        
        [Header("Mirada")]
        [SerializeField] private float _mouseSensitivity = 0.65f;
        [SerializeField] private CameraLookController _cameraLook;
        [SerializeField] private GameObject _cameraRoot; // el GameObject "Camera" hijo del Player
        [SerializeField] private CameraEffects _cameraEffects;
        [SerializeField] private TrailRenderer _dashTrail;
        //para al abrir el inventario no siga moviendo la vista
        private bool _inputBlocked;
        /// <summary>Bloquea/desbloquea la lectura de input (para cuando se abre UI como el inventario).</summary>
        public void SetInputBlocked(bool blocked) => _inputBlocked = blocked;

        public struct ReplicateData : FishNet.Object.Prediction.IReplicateData
        {
            public Vector2 Move;
            public bool Jump;
            public bool Sprint;
            public float Yaw; // rotación absoluta, no delta
            public bool Dash;             // pidió dash este tick (lo valida el servidor)
            public byte DashSlot;         // slot de la habilidad de dash
            public Vector3 DashDirection; // dirección de mirada al pedirlo

            public ReplicateData(Vector2 move, bool jump, bool sprint, float yaw) : this()
            {
                Move = move;
                Jump = jump;
                Sprint = sprint;
                Yaw = yaw;
            }

            private uint _tick;
            public void Dispose() { }
            public uint GetTick() => _tick;
            public void SetTick(uint value) => _tick = value;
        }

        public struct ReconcileData : FishNet.Object.Prediction.IReconcileData
        {
            public Vector3 Position;
            public Vector3 VerticalVelocity;
            public Quaternion Rotation;
            public Vector3 DashVelocity;      // velocidad de dash restante
            public float DashTimeRemaining;   // tiempo de dash restante
            public float DashDuration;        // duración total del dash en curso (curva de decaimiento)
            public bool Grounded;

            public ReconcileData(Vector3 position, Vector3 verticalVelocity, Quaternion rotation, Vector3 dashVelocity, float dashTimeRemaining, float dashDuration, bool grounded) : this()
            {
                Position = position;
                VerticalVelocity = verticalVelocity;
                Rotation = rotation;
                DashVelocity = dashVelocity;
                DashTimeRemaining = dashTimeRemaining;
                DashDuration = dashDuration;
                Grounded = grounded;
            }

            private uint _tick;
            public void Dispose() { }
            public uint GetTick() => _tick;
            public void SetTick(uint value) => _tick = value;
        }

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
            _abilities = GetComponent<AbilityController>();

            _controls = new PlayerControls();
            _moveAction = _controls.Player.Move;
            _jumpAction = _controls.Player.Jump;
            _sprintAction = _controls.Player.Sprint;
            _lookAction = _controls.Player.Look;
        }

        private void OnDestroy()
        {
            _controls?.Dispose();
        }

        private void OnEnable()
        {
            _moveAction.Enable();
            _jumpAction.Enable();
            _sprintAction.Enable();
            _lookAction.Enable();
        }

        private void OnDisable()
        {
            _moveAction.Disable();
            _jumpAction.Disable();
            _sprintAction.Disable();
            _lookAction.Disable();
        }
        public override void OnStartServer()
        {
            PlayerRegistry.Register(this);
        }

        public override void OnStopServer()
        {
            PlayerRegistry.Unregister(this);
        }

        public override void OnStartNetwork()
        {
            base.TimeManager.OnTick += TimeManager_OnTick;
            base.TimeManager.OnPostTick += TimeManager_OnPostTick;
        }

        public override void OnStopNetwork()
        {
            base.TimeManager.OnTick -= TimeManager_OnTick;
            base.TimeManager.OnPostTick -= TimeManager_OnPostTick;
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            ApplyOwnershipClient();
        }

        // Se reevalúa al cambiar de dueño: al reconectar, el cliente recibe su personaje primero
        // sin dueño (el servidor le devuelve el control un instante después) y OnStartClient ya
        // corrió como si fuera de otro jugador.
        public override void OnOwnershipClient(FishNet.Connection.NetworkConnection prevOwner)
        {
            base.OnOwnershipClient(prevOwner);
            ApplyOwnershipClient();
        }

        private void ApplyOwnershipClient()
        {
            bool owner = base.IsOwner;

            // Cámara e input solo para el personaje propio.
            if (_cameraRoot != null) _cameraRoot.SetActive(owner);
            enabled = owner;
            if (!owner) return;

            // Solo el personaje propio bloquea el cursor. Antes lo hacía OnEnable en cualquier
            // Player (también el de otro jugador o uno rechazado al reconectar) y el cursor
            // quedaba bloqueado al volver al menú.
            Cursor.lockState = CursorLockMode.Locked;

            if (_cameraRoot != null)
            {
                var shake = _cameraRoot.GetComponentInChildren<Game.Presentation.Combat.ScreenShake>(true);
                if (shake != null)
                    Game.Presentation.Combat.ScreenShake.ClaimAsLocal(shake);
            }
        }

        private void Update()
        {
            if (!base.IsOwner) return;

            if (_jumpAction.WasPressedThisFrame())
                _jumpQueued = true;

            // Mirada aplicada cada frame de render (fluidez tipo CS:GO).
            if (!_inputBlocked)
            {
                Vector2 look = _lookAction.ReadValue<Vector2>();
                // Sensibilidad del jugador (opciones) sobre la base del prefab. Solo escala la mirada
                // local antes de convertirla en input: prediction/reconcile no cambian.
                float sensitivity = _mouseSensitivity * LookSettings.Sensitivity;
                float yawDelta = look.x * sensitivity;
                float pitchDelta = (LookSettings.InvertY ? look.y : -look.y) * sensitivity;

                transform.Rotate(Vector3.up, yawDelta);
                if (_cameraLook != null)
                    _cameraLook.AddPitch(pitchDelta);
            }
        }

        private void TimeManager_OnTick()
        {
            RunInputs(CreateReplicateData());
        }

        private void TimeManager_OnPostTick()
        {
            CreateReconcile();
        }

        private ReplicateData CreateReplicateData()
        {
            if (!base.IsOwner) return default;

            ReplicateData data;
            if (_inputBlocked)
            {
                data = new ReplicateData(Vector2.zero, false, false, transform.eulerAngles.y);
            }
            else
            {
                Vector2 move = _moveAction.ReadValue<Vector2>();
                bool sprint = _sprintAction.IsPressed();
                bool jump = _jumpQueued;
                _jumpQueued = false;
                data = new ReplicateData(move, jump, sprint, transform.eulerAngles.y);
            }

            if (_dashInputQueued)
            {
                data.Dash = true;
                data.DashSlot = _queuedDashSlot;
                data.DashDirection = _queuedDashDirection;
                _dashInputQueued = false;
            }

            return data;
        }

        [Replicate]
        private void RunInputs(ReplicateData data, ReplicateState state = ReplicateState.Invalid, FishNet.Transporting.Channel channel = FishNet.Transporting.Channel.Unreliable)
        {
            float delta = (float)base.TimeManager.TickDelta;
            if (!_controller.enabled)
                return;

            // La dirección de movimiento sale SIEMPRE del yaw del input, no del transform.
            // Servidor y espectadores además aplican esa rotación al cuerpo. El owner NO: su
            // rotación la maneja Update (mouse por frame), y en los replays tras un reconcile
            // pisarla dejaba la mirada en el yaw de un input viejo: el jugador quedaba mirando
            // (y caminando) hacia otro lado.
            Quaternion yawRotation = Quaternion.Euler(0f, data.Yaw, 0f);
            if (!base.IsOwner)
                transform.rotation = yawRotation;

            // Movimiento horizontal relativo a la orientación del input.
            float baseSpeed = _stats != null ? _stats.MoveSpeed : _moveSpeed;
            float speed = baseSpeed * (data.Sprint ? _sprintMultiplier : 1f);
            Vector3 horizontal = (yawRotation * Vector3.right * data.Move.x + yawRotation * Vector3.forward * data.Move.y) * speed;

            // Gravedad y salto en el eje vertical, integrados aparte del horizontal.
            if (_grounded)
            {
                _verticalVelocity.y = -1f;

                float jumpForce = _stats != null ? _stats.JumpForce : _jumpForce;
                if (data.Jump)
                    _verticalVelocity.y = jumpForce;
            }
            else
            {
                _verticalVelocity.y += _gravity * delta;
            }

            // Dash pedido como input: cliente y servidor lo aplican en el mismo tick.
            if (data.Dash)
                TryStartDashFromInput(data, state);

            // Dash pedido por el servidor fuera del input (AbilityExecutor.StartDash). Hoy el
            // camino normal es el input; esto queda para efectos que empujen sin input del jugador.
            if (_dashRequested)
            {
                _dashVelocity = _pendingDashDir * _pendingDashSpeed;
                _dashTimeRemaining = _pendingDashDuration;
                _dashDuration = _pendingDashDuration;
                _dashRequested = false;

                // Feedback del dash: una sola vez, nunca en replays. Ahora corre también en el
                // owner cliente porque el owner predice el dash (setea _dashRequested localmente).
                if (!state.ContainsReplayed())
                {
                    if (base.IsOwner && _cameraEffects != null)
                    {
                        _cameraEffects.FovKick(6f, 0.08f, 0.3f);
                        Game.Presentation.Combat.ScreenShake.Shake(0.35f, 0.2f);
                    }
                }
            }

            // Integrar dash con decaimiento suave (ease-out).
            Vector3 dashStep = Vector3.zero;
            if (_dashTimeRemaining > 0f)
            {
                float t = Mathf.Clamp01(_dashTimeRemaining / Mathf.Max(0.0001f, _dashDuration));
                // Curva ease-out: mantiene velocidad alta al inicio y decae al final.
                float speedFactor = Mathf.SmoothStep(0f, 1f, t);
                dashStep = _dashVelocity * speedFactor;
                _dashTimeRemaining -= delta;
            }

            _controller.Move((horizontal + _verticalVelocity + dashStep) * delta);
            _grounded = _controller.isGrounded;

            // Trail visible mientras dura el dash (en todos los clientes, dash es estado replicado).
            if (_dashTrail != null)
                _dashTrail.emitting = _dashTimeRemaining > 0f;
        }

        public override void CreateReconcile()
        {
            ReconcileData rd = new ReconcileData(transform.position, _verticalVelocity, transform.rotation, _dashVelocity, _dashTimeRemaining, _dashDuration, _grounded);
            ReconcileState(rd);
        }

        [Reconcile]
        private void ReconcileState(ReconcileData data, FishNet.Transporting.Channel channel = FishNet.Transporting.Channel.Unreliable)
        {
            _controller.enabled = false;

            if (base.IsOwner)
                transform.position = data.Position;
            else
                transform.SetPositionAndRotation(data.Position, data.Rotation);

            _verticalVelocity = data.VerticalVelocity;
            _dashVelocity = data.DashVelocity;
            _dashTimeRemaining = data.DashTimeRemaining;
            _dashDuration = data.DashDuration;
            _grounded = data.Grounded;
            _controller.enabled = true;
        }

        /// <summary>
        /// Owner-only. Pide un dash en el próximo tick como parte del input predicho: el cliente lo
        /// aplica al instante y en sus replays, el servidor lo aplica al procesar ese mismo input
        /// (validando cooldown y maná). Antes el dash se predecía por fuera del input y el servidor
        /// lo aplicaba recién al llegar el RPC, en otro tick: al reconciliar, el jugador volvía atrás.
        /// </summary>
        public void QueueDashInput(int slot, Vector3 direction)
        {
            if (slot < 0 || slot > byte.MaxValue) return;
            _queuedDashSlot = (byte)slot;
            _queuedDashDirection = direction;
            _dashInputQueued = true;
        }

        private void TryStartDashFromInput(ReplicateData data, ReplicateState state)
        {
            if (_abilities == null) return;

            Vector3 direction = data.DashDirection;
            if (direction.sqrMagnitude < 0.0001f) return;
            direction.Normalize();

            float speed, duration;
            if (base.IsServerStarted)
            {
                // El servidor nunca hace replays: esto corre una vez por input y es la validación real.
                if (!_abilities.ServerTryConsumeDash(data.DashSlot, direction, out speed, out duration)) return;
            }
            else if (!_abilities.TryGetDashParams(data.DashSlot, out speed, out duration))
            {
                return;
            }

            _dashVelocity = direction * speed;
            _dashTimeRemaining = duration;
            _dashDuration = duration;

            // Feedback una sola vez (nunca en replays) y solo para el que dashea.
            if (!state.ContainsReplayed() && base.IsOwner && _cameraEffects != null)
            {
                _cameraEffects.FovKick(6f, 0.08f, 0.3f);
                Game.Presentation.Combat.ScreenShake.Shake(0.35f, 0.2f);
            }
        }

        /// <summary>Server-only. Solicita un dash fuera del input; se inicia en el próximo tick replicado.</summary>
        public void StartDash(Vector3 direction, float speed, float duration)
        {
            _pendingDashDir = direction.normalized;
            _pendingDashSpeed = speed;
            _pendingDashDuration = duration;
            _dashRequested = true;
        }

        public void DisableMovement()
        {
            if (base.TimeManager != null)
            {
                base.TimeManager.OnTick -= TimeManager_OnTick;
                base.TimeManager.OnPostTick -= TimeManager_OnPostTick;
            }
            enabled = false;
        }
    }
}
