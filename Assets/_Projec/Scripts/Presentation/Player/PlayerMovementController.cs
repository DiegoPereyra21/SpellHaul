using FishNet.Object;
using FishNet.Object.Prediction;
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

            public ReconcileData(Vector3 position, Vector3 verticalVelocity, Quaternion rotation, Vector3 dashVelocity, float dashTimeRemaining) : this()
            {
                Position = position;
                VerticalVelocity = verticalVelocity;
                Rotation = rotation;
                DashVelocity = dashVelocity;
                DashTimeRemaining = dashTimeRemaining;
            }

            private uint _tick;
            public void Dispose() { }
            public uint GetTick() => _tick;
            public void SetTick(uint value) => _tick = value;
        }

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();

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
            Cursor.lockState = CursorLockMode.Locked;
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

            if (!base.IsOwner)
            {
                // Desactivar cámara y input para jugadores que no son nuestros.
                if (_cameraRoot != null) _cameraRoot.SetActive(false);
                enabled = false;
            }
            else if (_cameraRoot != null)
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
                float yawDelta = look.x * _mouseSensitivity;
                float pitchDelta = -look.y * _mouseSensitivity;

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
            if (_inputBlocked) return new ReplicateData(Vector2.zero, false, false, transform.eulerAngles.y);

            Vector2 move = _moveAction.ReadValue<Vector2>();
            bool sprint = _sprintAction.IsPressed();
            bool jump = _jumpQueued;
            _jumpQueued = false;

            return new ReplicateData(move, jump, sprint, transform.eulerAngles.y);
        }

        [Replicate]
        private void RunInputs(ReplicateData data, ReplicateState state = ReplicateState.Invalid, FishNet.Transporting.Channel channel = FishNet.Transporting.Channel.Unreliable)
        {
            float delta = (float)base.TimeManager.TickDelta;
            if (!_controller.enabled)
                return;

            // El owner ya aplicó su rotación en Update (fluidez por frame).
            // Servidor y replays usan el yaw absoluto que vino en el input.
            if (!base.IsOwner || state.ContainsReplayed())
                transform.rotation = Quaternion.Euler(0f, data.Yaw, 0f);

            // Movimiento horizontal relativo a la orientación del jugador.
            float baseSpeed = _stats != null ? _stats.MoveSpeed : _moveSpeed;
            float speed = baseSpeed * (data.Sprint ? _sprintMultiplier : 1f);
            Vector3 horizontal = (transform.right * data.Move.x + transform.forward * data.Move.y) * speed;

            // Gravedad y salto en el eje vertical, integrados aparte del horizontal.
            if (_controller.isGrounded)
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

            // Iniciar dash si hay una solicitud pendiente.
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
                float t = Mathf.Clamp01(_dashTimeRemaining / _dashDuration);
                // Curva ease-out: mantiene velocidad alta al inicio y decae al final.
                float speedFactor = Mathf.SmoothStep(0f, 1f, t);
                dashStep = _dashVelocity * speedFactor;
                _dashTimeRemaining -= delta;
            }

            _controller.Move((horizontal + _verticalVelocity + dashStep) * delta);

            // Trail visible mientras dura el dash (en todos los clientes, dash es estado replicado).
            if (_dashTrail != null)
                _dashTrail.emitting = _dashTimeRemaining > 0f;
        }

        public override void CreateReconcile()
        {
            ReconcileData rd = new ReconcileData(transform.position, _verticalVelocity, transform.rotation, _dashVelocity, _dashTimeRemaining);
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
            _controller.enabled = true;
        }

        /// <summary>
        /// Encola un impulso de dash. Solo debe llamarse en el servidor (el owner lo verá vía reconcile).
        /// </summary>
        /// <summary>Server-only. Solicita un dash; se inicia en el próximo tick replicado.</summary>
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
