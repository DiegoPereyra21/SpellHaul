using System.Collections.Generic;
using FishNet;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using Game.Core.Run;
using Game.Presentation.Combat;
using UnityEngine;

namespace Game.Presentation.Run
{
    /// <summary>
    /// Gestor central de la partida (server-authoritative). Lleva el estado de cada jugador
    /// y determina el fin de la run. Único en la escena. Los clientes solo leen los contadores
    /// y la fase vía SyncVars.
    /// </summary>
    public class RunManager : NetworkBehaviour
    {
        /// <summary>Instancia accesible desde cualquier lado una vez que el RunManager está en la red (cliente o servidor).</summary>
        public static RunManager Instance { get; private set; }

        // Estado autoritativo por jugador (solo servidor).
        private readonly Dictionary<int, PlayerRunStatus> _statuses = new();

        // Estado sincronizado a clientes.
        private readonly SyncVar<int> _aliveCount = new SyncVar<int>();
        private readonly SyncVar<int> _extractedCount = new SyncVar<int>();
        private readonly SyncVar<int> _deadCount = new SyncVar<int>();
        private readonly SyncVar<RunPhase> _phase = new SyncVar<RunPhase>(RunPhase.InProgress);
        [SerializeField] private float _runDuration = 60f; // duración antes de la fase de peligro
        [Header("Fase de peligro")]
        [Tooltip("Daño aplicado a cada jugador vivo por cada tick, mientras dure la fase de peligro.")]
        [SerializeField] private float _dangerPhaseDamagePerTick = 120f;
        [Tooltip("Segundos entre cada tick de daño de la fase de peligro.")]
        [SerializeField] private float _dangerPhaseTickInterval = 1f;

        private float _dangerPhaseTickTimer;

        /// <summary>El timer de la run no corre hasta que entra el primer jugador. Bajo MPS el
        /// proceso arranca mucho antes de que el matchmaking le asigne gente (queda en StandingBy
        /// un rato indeterminado): si el reloj corriera desde el boot, los jugadores entrarían a
        /// una run ya vencida.</summary>
        private bool _runStarted;

        /// <summary>Solo servidor. True desde que se registró el primer jugador.</summary>
        public bool HasRunStarted => _runStarted;

        [Header("Fin de la run")]
        [Tooltip("Segundos desde el primer jugador registrado durante los cuales la run NO puede terminar aunque no quede nadie vivo: da tiempo a que conecten los demás del match. Se corta antes si ya se registraron todos los esperados (GSDK InitialPlayers). Sin ese dato (conexión directa) aplica solo este tiempo.")]
        [SerializeField] private float _endGraceSeconds = 75f;

        private System.Func<int> _expectedPlayersProvider;
        private int _everRegistered;      // registros totales: no baja al desconectarse
        private float _firstRegisterTime;
        private bool _endGateOpen;        // una vez abierta, la run ya puede terminar
        private bool _endBlocked;

        private readonly SyncVar<float> _timeRemaining = new SyncVar<float>();
        public float TimeRemaining => _timeRemaining.Value;
        /// <summary>Solo servidor. Se dispara al entrar en la fase de peligro (timer a 0).</summary>
        public event System.Action OnDangerPhaseStarted;


        public int AliveCount => _aliveCount.Value;
        public int ExtractedCount => _extractedCount.Value;
        public int DeadCount => _deadCount.Value;
        public RunPhase Phase => _phase.Value;

        /// <summary>Solo servidor. Se dispara cuando la run termina.</summary>
        public event System.Action OnRunEnded;

        public override void OnStartNetwork()
        {
            Instance = this;
        }

        public override void OnStopNetwork()
        {
            if (Instance == this) Instance = null;
        }

        public override void OnStartServer()
        {
            _statuses.Clear();
            _everRegistered = 0;
            _endGateOpen = false;
            _endBlocked = false;
            _timeRemaining.Value = _runDuration;
            _phase.Value = RunPhase.InProgress;
            _runStarted = false;
            _aliveCount.Value = 0;
            _extractedCount.Value = 0;
            _deadCount.Value = 0;
        }

        public override void OnStopServer()
        {
            // La instancia se limpia en OnStopNetwork; acá no hace falta nada.
        }


        private void Update()
        {
            if (!base.IsServerInitialized) return;
            if (!_runStarted) return; // esperando al primer jugador

            // Si la run quedó sin vivos mientras el fin estaba bloqueado, termina apenas se abre.
            // Solo se consulta tras un intento bloqueado (el provider del GSDK aloca una lista).
            if (_endBlocked && !_endGateOpen && _phase.Value != RunPhase.Ended && CanEnd())
                CheckRunEnd();

            if (_phase.Value == RunPhase.InProgress)
            {
                _timeRemaining.Value -= Time.deltaTime;

                if (_timeRemaining.Value <= 0f)
                {
                    _timeRemaining.Value = 0f;
                    EnterDangerPhase();
                }
            }
            else if (_phase.Value == RunPhase.DangerPhase)
            {
                TickDangerPhaseDamage();
            }
        }

        /// <summary>Server-only. Daño ambiental parejo a todo jugador vivo, cada
        /// _dangerPhaseTickInterval segundos. Versión simplificada de la fase de peligro hasta que
        /// haya enemigos (hunters) para spawnear — ver el TODO de EnterDangerPhase.</summary>
        private void TickDangerPhaseDamage()
        {
            _dangerPhaseTickTimer += Time.deltaTime;
            if (_dangerPhaseTickTimer < _dangerPhaseTickInterval) return;
            _dangerPhaseTickTimer -= _dangerPhaseTickInterval;

            // Copia de los IDs antes de iterar: ApplyDamage puede matar al jugador y disparar en
            // cadena SetStatus() sobre este mismo diccionario, lo cual invalida el enumerador si
            // se está recorriendo _statuses directamente.
            var aliveIds = new List<int>();
            foreach (var kvp in _statuses)
                if (kvp.Value == PlayerRunStatus.Alive)
                    aliveIds.Add(kvp.Key);

            foreach (int playerObjectId in aliveIds)
            {
                if (!InstanceFinder.ServerManager.Objects.Spawned.TryGetValue(playerObjectId, out NetworkObject nob))
                    continue;

                var health = nob.GetComponent<Health>();
                if (health == null) continue;

                health.ApplyDamage(_dangerPhaseDamagePerTick, playerObjectId);
            }
        }

        private void EnterDangerPhase()
        {
            if (_phase.Value != RunPhase.InProgress) return;

            _phase.Value = RunPhase.DangerPhase;
            _dangerPhaseTickTimer = 0f;
            OnDangerPhaseStarted?.Invoke();
            // TODO: cuando haya IA de enemigos, suscribirse a OnDangerPhaseStarted y spawnear
            //       hunters cerca de cada jugador vivo, como escalada sobre este daño ambiental
            //       (que se queda como piso mínimo de presión, no se reemplaza).
        }
        /// <summary>Server-only. Fuente de "cuántos jugadores tiene el match" (GSDK InitialPlayers,
        /// la inyecta NetworkBootstrap). Se lee en cada chequeo; 0 o sin provider = sin dato.</summary>
        public void SetExpectedPlayersProvider(System.Func<int> provider) => _expectedPlayersProvider = provider;

        /// <summary>
        /// Server-only. La run solo puede terminar cuando ya se registraron todos los esperados o
        /// venció la gracia desde el primer registro (lo que ocurra primero). Evita que el primero
        /// en entrar, si muere/extrae/se va enseguida, cierre el proceso antes de que conecte el
        /// resto del match. No toca el timer de la run ni la fase de peligro.
        /// </summary>
        private bool CanEnd()
        {
            if (_endGateOpen) return true;
            if (!_runStarted) return false;

            int expected = _expectedPlayersProvider?.Invoke() ?? 0;
            bool allArrived = expected > 0 && _everRegistered >= expected;
            bool graceOver = Time.time - _firstRegisterTime >= _endGraceSeconds;

            if (allArrived || graceOver)
            {
                _endGateOpen = true;
                Debug.Log(allArrived
                    ? $"[RunManager] Llegaron los {expected} jugadores esperados: la run ya puede terminar."
                    : $"[RunManager] Venció la gracia de {_endGraceSeconds:0}s: la run ya puede terminar.");
            }
            return _endGateOpen;
        }

        /// <summary>Server-only. Registra un jugador como vivo al entrar a la run.</summary>
        public void RegisterPlayer(int playerObjectId)
        {
            if (!base.IsServerInitialized) return;
            if (_statuses.ContainsKey(playerObjectId)) return;

            _statuses[playerObjectId] = PlayerRunStatus.Alive;
            _everRegistered++;

            if (!_runStarted)
            {
                _runStarted = true;
                _firstRegisterTime = Time.time;
                _timeRemaining.Value = _runDuration;
                Debug.Log("[RunManager] Primer jugador en la run: arranca el timer.");
            }

            RecountAndSync();
        }

        /// <summary>Server-only. Saca a un jugador que se fue sin morir ni extraer (desconexión,
        /// cierre del juego). Sin esto queda contado como vivo para siempre y la run nunca
        /// termina, con lo cual el proceso del servidor tampoco se cierra.</summary>
        public void UnregisterPlayer(int playerObjectId)
        {
            if (!base.IsServerInitialized) return;
            if (!_statuses.Remove(playerObjectId)) return;

            RecountAndSync();
            CheckRunEnd();
        }

        /// <summary>Server-only. Marca a un jugador como extraído.</summary>
        public void SetExtracted(int playerObjectId)
        {
            SetStatus(playerObjectId, PlayerRunStatus.Extracted);
        }

        /// <summary>Server-only. Marca a un jugador como muerto.</summary>
        public void SetDead(int playerObjectId)
        {
            SetStatus(playerObjectId, PlayerRunStatus.Dead);
        }

        private void SetStatus(int playerObjectId, PlayerRunStatus status)
        {
            if (!base.IsServerInitialized) return;

            if (!_statuses.ContainsKey(playerObjectId))
            {
                Debug.LogWarning($"[RunManager] SetStatus({status}) para {playerObjectId} pero NO está registrado. Registrados: {string.Join(",", _statuses.Keys)}");
                return;
            }

            if (_statuses[playerObjectId] != PlayerRunStatus.Alive)
            {
                Debug.LogWarning($"[RunManager] SetStatus({status}) para {playerObjectId} pero ya está en {_statuses[playerObjectId]}");
                return;
            }

            _statuses[playerObjectId] = status;
            RecountAndSync();
            CheckRunEnd();
        }

        private void RecountAndSync()
        {
            int alive = 0, extracted = 0, dead = 0;
            foreach (var s in _statuses.Values)
            {
                switch (s)
                {
                    case PlayerRunStatus.Alive: alive++; break;
                    case PlayerRunStatus.Extracted: extracted++; break;
                    case PlayerRunStatus.Dead: dead++; break;
                }
            }
            _aliveCount.Value = alive;
            _extractedCount.Value = extracted;
            _deadCount.Value = dead;
        }

        private void CheckRunEnd()
        {
            if (_phase.Value == RunPhase.Ended) return; // solo evita re-terminar
            if (!_runStarted) return;                   // nunca entró nadie: no hay run que terminar

            // Si ya no queda nadie registrado (todos se desconectaron), la run también terminó.
            if (_statuses.Count == 0 || _aliveCount.Value <= 0)
            {
                if (!CanEnd())
                {
                    if (!_endBlocked)
                    {
                        _endBlocked = true;
                        int expected = _expectedPlayersProvider?.Invoke() ?? 0;
                        Debug.Log($"[RunManager] Fin bloqueado: {_everRegistered}/{(expected > 0 ? expected.ToString() : "?")} registrados; se espera al resto o a que venza la gracia de {_endGraceSeconds:0}s.");
                    }
                    return;
                }

                _phase.Value = RunPhase.Ended;
                OnRunEnded?.Invoke();
            }
        }
    }
}