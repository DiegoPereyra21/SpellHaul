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

        // Jugadores remotos (espectadores): NO se simulan. El input del otro llega con la latencia
        // de dos tramos (owner -> server -> yo), así que simularlo "en vivo" obligaba a re-simular
        // ~14 ticks de golpe cuando llegaba (pop de 2,5 m en saltos, 5-6 m en dashes). Ahora se
        // guardan los estados del servidor y se interpola entre ellos unos ticks en el pasado.
        private struct RemoteSnap
        {
            public float Tick;
            public Vector3 Position;
            public Quaternion Rotation;
            public bool Dashing;
        }
        // Buffer adaptativo: el atraso se ajusta al jitter real de la conexión (3 ticks en redes
        // estables, hasta 8 en redes malas) y cambia despacio para no notarse.
        private const float RemoteMinDelayTicks = 3f;
        private const float RemoteMaxDelayTicks = 8f;
        private const float RemoteDelayChangePerSecond = 1f;   // ticks de atraso que puede cambiar por segundo
        // Extrapolación corta: si dejan de llegar estados, sigue con su última velocidad hasta ~100 ms
        // y después se queda quieto (en vez de congelarse en seco y saltar al volver los paquetes).
        private const float RemoteMaxExtrapolationTicks = 6f;
        // Reloj de render: se acelera/frena un poco para seguir al buffer; solo salta si se fue muy lejos.
        private const float RemoteMaxTimeScaleAdjust = 0.25f;
        private const float RemoteHardResyncTicks = 12f;
        private const float RemoteTeleportDistance = 8f;
        private readonly System.Collections.Generic.List<RemoteSnap> _remoteSnaps = new System.Collections.Generic.List<RemoteSnap>(32);
        private float _remoteRenderTick;
        private bool _remoteRenderInit;
        private float _remoteDelayTicks = 4f;
        private float _remoteJitter;              // desvío medio de llegada de estados, en segundos (EMA)
        private float _remoteLastArrivalTime = -1f;
        private float _remoteLastArrivalTick;

        // Último tick del servidor en que se dibujó a algún jugador remoto (para lag compensation:
        // el owner le dice al servidor dónde veía a los demás cuando disparó).
        private static float _latestRemoteViewTick;
        private static int _latestRemoteViewFrame = -100;

        /// <summary>Cliente. Tick del servidor en que se están dibujando los jugadores remotos ahora. False si no hay remotos.</summary>
        public static bool TryGetRemoteViewTick(out uint tick)
        {
            if (Time.frameCount - _latestRemoteViewFrame > 2 || _latestRemoteViewTick <= 0f)
            {
                tick = 0;
                return false;
            }
            tick = (uint)_latestRemoteViewTick;
            return true;
        }

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
            _remoteSnaps.Clear();
            _remoteRenderInit = false;
            _remoteDelayTicks = 4f;
            _remoteJitter = 0f;
            _remoteLastArrivalTime = -1f;
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
            // El componente queda activo también en remotos: LateUpdate interpola su posición.
            // Update ya ignora a los que no son owner.
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
            // Remotos: no se simulan (ver RemoteSnap). Solo owner (predicción) y servidor (autoridad).
            if (!base.IsOwner && !base.IsServerStarted)
                return;

            float delta = (float)base.TimeManager.TickDelta;
            if (!_controller.enabled)
                return;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (data.Jump || data.Dash)
            {
                string diagRole = base.IsServerStarted ? "SERVER" : (base.IsOwner ? "OWNER" : "SPECTATOR");
                Debug.Log($"[InputDiag] role={diagRole} jump={data.Jump} dash={data.Dash} state={state} grounded={_grounded} tick={data.GetTick()}");
            }
#endif

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

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (!base.IsServerStarted && !state.ContainsReplayed())
                DiagRecord(data.GetTick());
#endif

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
            if (!base.IsOwner && !base.IsServerStarted)
            {
                PushRemoteSnapshot(data);
                return;
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            DiagReconcile(data);
#endif
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

        private void PushRemoteSnapshot(ReconcileData data)
        {
            float tick = data.GetTick();
            if (_remoteSnaps.Count > 0 && tick <= _remoteSnaps[_remoteSnaps.Count - 1].Tick)
                return; // fuera de orden o repetido (canal unreliable)

            // Jitter: cuánto se desvía la llegada real de lo esperado según los ticks transcurridos.
            float now = Time.unscaledTime;
            if (_remoteLastArrivalTime >= 0f)
            {
                float expected = (tick - _remoteLastArrivalTick) * (float)base.TimeManager.TickDelta;
                float deviation = Mathf.Abs((now - _remoteLastArrivalTime) - expected);
                _remoteJitter = Mathf.Lerp(_remoteJitter, Mathf.Min(deviation, 0.25f), 0.1f);
            }
            _remoteLastArrivalTime = now;
            _remoteLastArrivalTick = tick;

            _remoteSnaps.Add(new RemoteSnap
            {
                Tick = tick,
                Position = data.Position,
                Rotation = data.Rotation,
                Dashing = data.DashTimeRemaining > 0f
            });
            if (_remoteSnaps.Count > 64)
                _remoteSnaps.RemoveRange(0, _remoteSnaps.Count - 64);
        }

        private void LateUpdate()
        {
            if (base.IsOwner || base.IsServerStarted || _remoteSnaps.Count == 0) return;

            float dt = Time.deltaTime;
            float tickRate = base.TimeManager.TickRate;

            // Atraso objetivo según el jitter (2 ticks base + 2 desvíos), acotado y suavizado.
            float desiredDelay = Mathf.Clamp(2f + 2f * _remoteJitter * tickRate, RemoteMinDelayTicks, RemoteMaxDelayTicks);
            _remoteDelayTicks = Mathf.MoveTowards(_remoteDelayTicks, desiredDelay, RemoteDelayChangePerSecond * dt);

            RemoteSnap last = _remoteSnaps[_remoteSnaps.Count - 1];
            float target = last.Tick - _remoteDelayTicks;
            if (!_remoteRenderInit || Mathf.Abs(target - _remoteRenderTick) > RemoteHardResyncTicks)
            {
                _remoteRenderTick = target;
                _remoteRenderInit = true;
            }
            else
            {
                // Atrasado → corre un poco más rápido; adelantado (no llegan estados) → más lento.
                float drift = target - _remoteRenderTick;
                float timeScale = 1f + Mathf.Clamp(drift * 0.1f, -RemoteMaxTimeScaleAdjust, RemoteMaxTimeScaleAdjust);
                _remoteRenderTick += dt * tickRate * timeScale;
            }
            _remoteRenderTick = Mathf.Min(_remoteRenderTick, last.Tick + RemoteMaxExtrapolationTicks);
            _latestRemoteViewTick = _remoteRenderTick;
            _latestRemoteViewFrame = Time.frameCount;

            Vector3 pos;
            Quaternion rot;
            bool dashing;
            int a;

            if (_remoteRenderTick > last.Tick && _remoteSnaps.Count >= 2)
            {
                // Sin estado nuevo: extrapolar con la última velocidad (tramo corto, acotado arriba).
                a = _remoteSnaps.Count - 2;
                RemoteSnap prev = _remoteSnaps[a];
                float span = last.Tick - prev.Tick;
                Vector3 delta = last.Position - prev.Position;
                bool teleported = delta.sqrMagnitude > RemoteTeleportDistance * RemoteTeleportDistance;
                pos = (span > 0f && !teleported)
                    ? last.Position + delta / span * (_remoteRenderTick - last.Tick)
                    : last.Position;
                rot = last.Rotation;
                dashing = last.Dashing;
            }
            else
            {
                // Tramo [a, b] que contiene el tick de render.
                int b = 0;
                while (b < _remoteSnaps.Count - 1 && _remoteSnaps[b].Tick < _remoteRenderTick) b++;
                a = Mathf.Max(0, b - 1);
                RemoteSnap sa = _remoteSnaps[a];
                RemoteSnap sb = _remoteSnaps[b];

                if (sb.Tick <= sa.Tick || (sb.Position - sa.Position).sqrMagnitude > RemoteTeleportDistance * RemoteTeleportDistance)
                {
                    pos = sb.Position;
                    rot = sb.Rotation;
                }
                else
                {
                    float t = Mathf.Clamp01((_remoteRenderTick - sa.Tick) / (sb.Tick - sa.Tick));
                    pos = Vector3.Lerp(sa.Position, sb.Position, t);
                    rot = Quaternion.Slerp(sa.Rotation, sb.Rotation, t);
                }
                dashing = sb.Dashing;
            }

            // Red de seguridad: nunca aplicar un transform inválido (NaN/Infinity).
            if (!IsFinite(pos) || !IsFinite(rot)) return;

            // CharacterController activo = colisiona con el owner; para mover el transform hay que apagarlo un instante.
            _controller.enabled = false;
            transform.SetPositionAndRotation(pos, rot);
            _controller.enabled = true;

            if (_dashTrail != null)
                _dashTrail.emitting = dashing;

            if (a > 0)
                _remoteSnaps.RemoveRange(0, a);
        }

        private static bool IsFinite(Vector3 v) =>
            !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) ||
              float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));

        private static bool IsFinite(Quaternion q) =>
            !(float.IsNaN(q.x) || float.IsNaN(q.y) || float.IsNaN(q.z) || float.IsNaN(q.w)) &&
            (q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w) > 0.0001f;

        /// <summary>Owner. Dirección horizontal del dash: hacia donde te movés; sin input, hacia adelante.</summary>
        public Vector3 GetDashDirection()
        {
            Vector2 move = _inputBlocked ? Vector2.zero : _moveAction.ReadValue<Vector2>();
            Quaternion yaw = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
            Vector3 dir = yaw * new Vector3(move.x, 0f, move.y);
            if (dir.sqrMagnitude < 0.0001f) dir = yaw * Vector3.forward;
            return dir.normalized;
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private const int DiagSize = 128;
        private readonly uint[] _diagTick = new uint[DiagSize];
        private readonly Vector3[] _diagPos = new Vector3[DiagSize];
        private readonly float[] _diagVy = new float[DiagSize];

        private void DiagRecord(uint tick)
        {
            int i = (int)(tick % DiagSize);
            _diagTick[i] = tick;
            _diagPos[i] = transform.position;
            _diagVy[i] = _verticalVelocity.y;
        }

        private void DiagReconcile(ReconcileData data)
        {
            if (base.IsServerStarted) return;
            uint tick = data.GetTick();
            int i = (int)(tick % DiagSize);
            if (_diagTick[i] != tick) return;

            float err = Vector3.Distance(_diagPos[i], data.Position);
            float errY = data.Position.y - _diagPos[i].y;
            float minErr = base.IsOwner ? 0.03f : 0.3f;
            if (err < minErr) return;

            string role = base.IsOwner ? "OWNER" : "SPECTATOR";
            Debug.Log($"[ReconcileDiag] {role} tick={tick} err={err:F3} errY={errY:F3} vyPred={_diagVy[i]:F2} vySrv={data.VerticalVelocity.y:F2} groundedSrv={data.Grounded} rtt={base.TimeManager.RoundTripTime}ms");
        }
#endif

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