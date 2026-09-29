using System;
using System.Collections;
using PlayFab;
using PlayFab.MultiplayerModels;
using UnityEngine;

namespace Game.Presentation.Bootstrap
{
    /// <summary>
    /// Cola de matchmaking de PlayFab. Emite un ticket, lo poletea hasta que hay match (o falla),
    /// y expone el estado para que la UI lo dibuje. No sabe nada de UI ni de red de juego: quien
    /// escuche OnStateChanged decide qué hacer cuando el estado llega a Matched.
    /// </summary>
    public class MatchmakingService : MonoBehaviour
    {
        public enum State
        {
            Idle = 0,
            Searching = 1,
            Matched = 2,
            Failed = 3,
        }

        [SerializeField] private string _queueName = "SpellHaulRaid";
        [Tooltip("Segundos que PlayFab intenta emparejar el ticket antes de darlo por vencido.")]
        [SerializeField] private int _giveUpAfterSeconds = 120;
        [Tooltip("PlayFab permite hasta 10 consultas por minuto: no bajar de 6 segundos.")]
        [SerializeField] private float _pollIntervalSeconds = 6f;

    
        [Tooltip("Latencia estimada (ms) hacia la única región desplegada hoy (East US). Placeholder hasta medir con QoS beacons reales.")]
        [SerializeField] private int _estimatedLatencyMs = 150;

        public State CurrentState { get; private set; } = State.Idle;

        /// <summary>Id del match encontrado. Solo válido en estado Matched.</summary>
        public string MatchId { get; private set; }

        /// <summary>Detalles del servidor asignado. Null si la queue no tiene server allocation
        /// (hoy: siempre null, se resuelve al desplegar el Build en MPS).</summary>
        public ServerDetails ServerDetails { get; private set; }

        public event Action<State> OnStateChanged;

        /// <summary>Motivo del último fallo, para mostrarle algo útil al jugador.</summary>
        public event Action<string> OnFailed;

        private string _ticketId;
        private Coroutine _pollRoutine;
        private bool _recoveringFromStaleTicket;
        private int _consecutivePollErrors;

        // Errores seguidos de consulta tolerados antes de abandonar: un corte puntual o un 429
        // (límite de 10 consultas/min) no debería tirar abajo toda la búsqueda.
        private const int MaxConsecutivePollErrors = 3;

        // Textos para el jugador (en inglés). El detalle técnico va solo al log.
        private const string GenericFailMessage = "Matchmaking failed. Please try again.";

        /// <summary>Entra en la cola. No hace nada si ya está buscando.</summary>
        public void StartSearch()
        {
            if (CurrentState == State.Searching) return;

            if (string.IsNullOrEmpty(PlayFabSession.EntityId))
            {
                Fail("Not connected to the game services yet. Please wait a moment.", "Sesión de PlayFab todavía no lista.");
                return;
            }

            MatchId = null;
            ServerDetails = null;
            _ticketId = null;
            _consecutivePollErrors = 0;
            SetState(State.Searching);

            var request = new CreateMatchmakingTicketRequest
            {
                Creator = new MatchmakingPlayer
                {
                    Entity = new EntityKey
                    {
                        Id = PlayFabSession.EntityId,
                        Type = PlayFabSession.EntityType,
                    },
                    // La región selection rule de la queue exige esto. Latencia fija estimada
                    // hasta integrar QoS beacons reales (solo hay una región desplegada por
                    // ahora, así que no cambia nada medir de verdad todavía).
                    Attributes = new MatchmakingPlayerAttributes
                    {
                        DataObject = new
                        {
                            Latencies = new object[]
                            {
                                new { region = "EastUs", latency = _estimatedLatencyMs }
                            }
                        }
                    },
                },
                GiveUpAfterSeconds = _giveUpAfterSeconds,
                QueueName = _queueName,
            };

            PlayFabMultiplayerAPI.CreateMatchmakingTicket(request, OnTicketCreated, OnCreateTicketError);
        }

        /// <summary>
        /// Si el error es "ya sos miembro de otro ticket" (ej. un cancel anterior que no llegó a
        /// completarse del lado del servidor), la recuperación documentada por PlayFab es cancelar
        /// TODOS los tickets del jugador y reintentar. Solo se reintenta una vez, para no entrar
        /// en loop si el problema fuera otro.
        /// </summary>
        private void OnCreateTicketError(PlayFabError error)
        {
            bool isStaleTicket = error.Error == PlayFabErrorCode.MatchmakingTicketMembershipLimitExceeded;

            if (isStaleTicket && !_recoveringFromStaleTicket)
            {
                _recoveringFromStaleTicket = true;
                Debug.LogWarning("[Matchmaking] Ticket perdido de un intento anterior, limpiando y reintentando...");

                PlayFabMultiplayerAPI.CancelAllMatchmakingTicketsForPlayer(
                    new CancelAllMatchmakingTicketsForPlayerRequest
                    {
                        Entity = new EntityKey { Id = PlayFabSession.EntityId, Type = PlayFabSession.EntityType },
                        QueueName = _queueName,
                    },
                    _ =>
                    {
                        _recoveringFromStaleTicket = false;
                        StartSearch();
                    },
                    cancelError =>
                    {
                        _recoveringFromStaleTicket = false;
                        OnApiError(cancelError);
                    });
                return;
            }

            _recoveringFromStaleTicket = false;
            OnApiError(error);
        }

        /// <summary>Sale de la cola. Cancela el ticket en PlayFab si había uno emitido.</summary>
        public void CancelSearch()
        {
            if (CurrentState != State.Searching) return;

            StopPolling();

            if (!string.IsNullOrEmpty(_ticketId))
                CancelTicket(_ticketId);

            _ticketId = null;
            SetState(State.Idle);
        }

        private void CancelTicket(string ticketId)
        {
            PlayFabMultiplayerAPI.CancelMatchmakingTicket(
                new CancelMatchmakingTicketRequest
                {
                    QueueName = _queueName,
                    TicketId = ticketId,
                },
                _ => { },
                error => Debug.LogWarning($"[Matchmaking] No se pudo cancelar el ticket: {error.GenerateErrorReport()}"));
        }

        private void OnTicketCreated(CreateMatchmakingTicketResult result)
        {
            // Canceló mientras se creaba el ticket: CancelSearch no tenía id para cancelar, así que
            // el ticket quedaba vivo en PlayFab y podía matchear (servidor asignado a alguien que no va).
            if (CurrentState != State.Searching)
            {
                CancelTicket(result.TicketId);
                return;
            }

            _ticketId = result.TicketId;
            Debug.Log($"[Matchmaking] Ticket creado: {_ticketId}");
            _pollRoutine = StartCoroutine(PollTicketRoutine());
        }

        private IEnumerator PollTicketRoutine()
        {
            var wait = new WaitForSeconds(_pollIntervalSeconds);

            while (CurrentState == State.Searching)
            {
                yield return wait;
                if (CurrentState != State.Searching) yield break;

                PlayFabMultiplayerAPI.GetMatchmakingTicket(
                    new GetMatchmakingTicketRequest
                    {
                        QueueName = _queueName,
                        TicketId = _ticketId,
                    },
                    OnTicketPolled,
                    OnPollError);
            }
        }

        private void OnTicketPolled(GetMatchmakingTicketResult result)
        {
            if (CurrentState != State.Searching) return;
            _consecutivePollErrors = 0;

            switch (result.Status)
            {
                // Estados intermedios: seguir esperando.
                case "WaitingForPlayers":
                case "WaitingForMatch":
                case "WaitingForServer":
                    break;

                case "Matched":
                    MatchId = result.MatchId;
                    StopPolling();
                    FetchMatchDetails();
                    break;

                case "Canceled":
                    StopPolling();
                    // Sin razón explícita = se agotó GiveUpAfterSeconds (no había con quién emparejar).
                    Fail("No match found. Please try again.",
                        string.IsNullOrEmpty(result.CancellationReasonString) ? "Timeout (GiveUpAfterSeconds)." : result.CancellationReasonString);
                    break;

                default:
                    Debug.LogWarning($"[Matchmaking] Estado de ticket no contemplado: {result.Status}");
                    break;
            }
        }

        /// <summary>Con el match confirmado, pide los detalles (incluye el servidor asignado si la
        /// queue tiene server allocation).</summary>
        private void FetchMatchDetails()
        {
            PlayFabMultiplayerAPI.GetMatch(
                new GetMatchRequest
                {
                    MatchId = MatchId,
                    QueueName = _queueName,
                },
                result =>
                {
                    ServerDetails = result.ServerDetails;
                    int playerCount = result.Members?.Count ?? 0;
                    Debug.Log($"[Matchmaking] Match encontrado. MatchId: {MatchId} | Jugadores: {playerCount} | ServerDetails: {(ServerDetails == null ? "null (sin server allocation)" : ServerDetails.IPV4Address)}");
                    SetState(State.Matched);
                },
                OnApiError);
        }

        private void StopPolling()
        {
            if (_pollRoutine != null)
            {
                StopCoroutine(_pollRoutine);
                _pollRoutine = null;
            }
        }

        private void OnApiError(PlayFabError error)
        {
            StopPolling();
            Fail(GenericFailMessage, error.GenerateErrorReport());
        }

        /// <summary>Un error al consultar el ticket no corta la búsqueda salvo que se repita.</summary>
        private void OnPollError(PlayFabError error)
        {
            if (CurrentState != State.Searching) return;

            _consecutivePollErrors++;
            if (_consecutivePollErrors < MaxConsecutivePollErrors)
            {
                Debug.LogWarning($"[Matchmaking] Falló la consulta del ticket ({_consecutivePollErrors}/{MaxConsecutivePollErrors}), sigo esperando: {error.GenerateErrorReport()}");
                return;
            }

            OnApiError(error);
        }

        /// <summary>playerMessage se muestra en el menú (inglés); logDetail va solo a la consola.</summary>
        private void Fail(string playerMessage, string logDetail)
        {
            Debug.LogError($"[Matchmaking] {playerMessage} | {logDetail}");
            SetState(State.Failed);
            OnFailed?.Invoke(playerMessage);
        }

        private void SetState(State newState)
        {
            if (CurrentState == newState) return;
            CurrentState = newState;
            OnStateChanged?.Invoke(newState);
        }

        private void OnDisable()
        {
            StopPolling();
        }
    }
}