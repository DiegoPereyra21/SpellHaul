using FishNet;
using FishNet.Transporting.Tugboat;
using Game.Presentation.Bootstrap;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Presentation.UI
{
    /// <summary>
    /// Menú principal. "Find Run" mete al jugador en la cola de matchmaking de PlayFab; cuando
    /// hay match, conecta al servidor dedicado. Ya no existe el modo host: nadie expone su PC.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class MainMenuController : MonoBehaviour
    {
        [SerializeField] private StashScreenController _stashScreen;
        [SerializeField] private MatchmakingService _matchmaking;

        [Header("Servidor (temporal)")]
        [Tooltip("Mientras la queue no tenga server allocation, el match se juega contra esta dirección. Apuntar al LocalMultiplayerAgent para probar.")]
        [SerializeField] private string _fallbackServerAddress = "127.0.0.1";
        [SerializeField] private ushort _fallbackServerPort = 56100;
        [Tooltip("Nombre del puerto de juego en el Build de PlayFab (igual que en NetworkBootstrap).")]
        [SerializeField] private string _gamePortName = "game_port";

        private UIDocument _document;
        private VisualElement _searchPanel;
        private Label _searchStatus;
        private Label _searchTimer;
        private float _searchStartTime;
        private bool _searching;

        // Run en curso sin terminar (ver PlayerLoadoutService.IsInActiveRun).
        private VisualElement _rejoinPanel;
        private Label _rejoinStatus;
        private Button _rejoinReconnect;
        private Button _rejoinAbandon;
        private bool _abandonArmed; // el primer clic en Abandon pide confirmación

        [Header("Run en curso")]
        [Tooltip("Una run marcada como en curso hace más de estos segundos se da por terminada sin el jugador (el equipo se pierde) sin intentar reconectar.")]
        [SerializeField] private long _maxActiveRunAgeSeconds = 3600;

        private const string RejoinDefaultMessage =
            "You left a run in progress. Reconnect to get back to your character, or abandon the run and lose everything you brought.";

        private void OnEnable()
        {
            _document = GetComponent<UIDocument>();
            var root = _document.rootVisualElement;

            // Volviendo de una run (fin, caída o reconexión rechazada) el cursor puede venir bloqueado.
            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;

            root.Q<Button>("find-match-button").clicked += OnFindMatchClicked;
            root.Q<Button>("stash-button").clicked += OnStashClicked;
            root.Q<Button>("quit-button").clicked += () => Application.Quit();
            root.Q<Button>("search-cancel").clicked += OnCancelSearchClicked;

            _searchPanel = root.Q<VisualElement>("search-panel");
            _searchStatus = root.Q<Label>("search-status");
            _searchTimer = root.Q<Label>("search-timer");

            _rejoinPanel = root.Q<VisualElement>("rejoin-panel");
            _rejoinStatus = root.Q<Label>("rejoin-status");
            _rejoinReconnect = root.Q<Button>("rejoin-reconnect");
            _rejoinAbandon = root.Q<Button>("rejoin-abandon");
            if (_rejoinReconnect != null) _rejoinReconnect.clicked += OnRejoinReconnectClicked;
            if (_rejoinAbandon != null) _rejoinAbandon.clicked += OnRejoinAbandonClicked;

            // Jugar y tocar el stash dependen del loadout persistente listo (login a PlayFab
            // resuelto) — entrar antes jugaría contra el backend local descartable.
            SetGameplayButtonsEnabled(PlayFabSession.IsReady);
            PlayFabSession.OnReady += HandleSessionReady;

            if (_matchmaking != null)
            {
                _matchmaking.OnStateChanged += HandleMatchmakingState;
                _matchmaking.OnFailed += HandleMatchmakingFailed;
            }

            NetworkDisconnectHandler.OnUnexpectedDisconnect += HandleUnexpectedDisconnect;

            // Si volvimos acá por una caída de conexión, mostrarlo apenas se abre el menú.
            string disconnectMessage = NetworkDisconnectHandler.LastDisconnectMessage;
            if (!string.IsNullOrEmpty(disconnectMessage))
            {
                NetworkDisconnectHandler.ConsumeDisconnectMessage();
                ShowNotice(disconnectMessage);
            }

            // Con la sesión lista, revisar si quedó una run sin terminar (si no, lo hace HandleSessionReady).
            if (PlayFabSession.IsReady)
                _ = CheckActiveRunAsync(disconnectMessage);
        }

        private void OnDisable()
        {
            PlayFabSession.OnReady -= HandleSessionReady;

            if (_matchmaking != null)
            {
                _matchmaking.OnStateChanged -= HandleMatchmakingState;
                _matchmaking.OnFailed -= HandleMatchmakingFailed;
            }

            NetworkDisconnectHandler.OnUnexpectedDisconnect -= HandleUnexpectedDisconnect;
        }

        private void Update()
        {
            if (!_searching) return;

            float elapsed = Time.time - _searchStartTime;
            _searchTimer.text = $"{(int)(elapsed / 60f)}:{(int)(elapsed % 60f):00}";
        }

        private void HandleSessionReady()
        {
            SetGameplayButtonsEnabled(true);
            _ = CheckActiveRunAsync(null);
        }

        // ---------- Run en curso (reconexión) ----------

        /// <summary>
        /// Si el loadout quedó "adentro" de una run (el juego se cerró o se cortó la conexión), no se
        /// puede jugar otra ni tocar el inventario hasta resolverla: reconectar o abandonar. Una run
        /// demasiado vieja ya terminó seguro: el equipo se pierde sin preguntar.
        /// </summary>
        private async System.Threading.Tasks.Task CheckActiveRunAsync(string contextMessage)
        {
            if (_stashScreen == null) return;
            if (!await _stashScreen.EnsureLoadoutLoadedAsync()) return;
            if (!Game.Presentation.Run.PlayerLoadoutService.IsInActiveRun) return;

            var run = Game.Presentation.Run.PlayerLoadoutService.ActiveRun;
            long age = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds() - run.StartedUnixSeconds;
            if (age > _maxActiveRunAgeSeconds)
            {
                if (await Game.Presentation.Run.PlayerLoadoutService.AbandonActiveRunAsync())
                {
                    ShowNotice("The run ended while you were away. Your gear was lost.");
                    return;
                }
                // No se pudo resolver ahora: queda el panel para reintentar a mano.
            }

            ShowRejoinPanel(string.IsNullOrEmpty(contextMessage) ? RejoinDefaultMessage : $"{contextMessage}\n\n{RejoinDefaultMessage}");
        }

        private void ShowRejoinPanel(string message)
        {
            _searching = false;
            SetSearchPanel(false);
            SetGameplayButtonsEnabled(false);

            _abandonArmed = false;
            if (_rejoinAbandon != null) _rejoinAbandon.text = "Abandon Run";
            if (_rejoinStatus != null) _rejoinStatus.text = message;
            _rejoinReconnect?.SetEnabled(true);
            _rejoinAbandon?.SetEnabled(true);
            if (_rejoinPanel != null) _rejoinPanel.style.display = DisplayStyle.Flex;
        }

        private void HideRejoinPanel()
        {
            if (_rejoinPanel != null) _rejoinPanel.style.display = DisplayStyle.None;
            SetGameplayButtonsEnabled(PlayFabSession.IsReady);
        }

        private async void OnRejoinReconnectClicked()
        {
            var run = Game.Presentation.Run.PlayerLoadoutService.ActiveRun;
            if (!run.Active) { HideRejoinPanel(); return; }

            _rejoinStatus.text = "Reconnecting...";
            _rejoinReconnect.SetEnabled(false);
            _rejoinAbandon.SetEnabled(false);

            if (!await PlayFabSession.EnsureFreshAsync())
            {
                ShowRejoinPanel("Could not refresh your session. Check your connection and try again.");
                return;
            }

            // Si el servidor ya no existe, llega "Could not reach the run server" y vuelve este panel.
            // Si nos rechaza (murió / ya extrajo), aplica el resultado y lo muestra el menú.
            ConnectTo(run.Address, (ushort)run.Port);
        }

        private async void OnRejoinAbandonClicked()
        {
            if (!_abandonArmed)
            {
                _abandonArmed = true;
                _rejoinAbandon.text = "Confirm: lose your gear";
                return;
            }

            _rejoinReconnect.SetEnabled(false);
            _rejoinAbandon.SetEnabled(false);
            _rejoinStatus.text = "Abandoning the run...";

            if (!await Game.Presentation.Run.PlayerLoadoutService.AbandonActiveRunAsync())
            {
                ShowRejoinPanel("Could not abandon the run. Check your connection and try again.");
                return;
            }

            HideRejoinPanel();
            ShowNotice("Run abandoned. Everything you brought was lost.");
        }

        private void SetGameplayButtonsEnabled(bool enabled)
        {
            var root = _document.rootVisualElement;
            root.Q<Button>("find-match-button").SetEnabled(enabled);
            root.Q<Button>("stash-button").SetEnabled(enabled);
        }

        // ---------- Matchmaking ----------

        private async void OnStashClicked()
        {
            if (_stashScreen == null) return;
            if (Game.Presentation.Run.PlayerLoadoutService.IsInActiveRun) { ShowRejoinPanel(RejoinDefaultMessage); return; }
            if (!await _stashScreen.TryShowAsync())
                ShowNotice(InventoryLoadFailedMessage);
        }

        private const float SaveWaitSeconds = 15f;

        private const string InventoryLoadFailedMessage = "Could not load your inventory. Please try again.";

        private async void OnFindMatchClicked()
        {
            if (_matchmaking == null)
            {
                Debug.LogError("[MainMenu] Falta asignar el MatchmakingService en el inspector.");
                return;
            }
            if (_stashScreen == null)
            {
                Debug.LogError("[MainMenu] Falta asignar el StashScreenController en el inspector.");
                return;
            }

            _searchStartTime = Time.time;
            _searching = true;
            _searchStatus.text = "Loading your inventory...";
            _searchTimer.text = "0:00";
            SetSearchPanel(true);

            // El loadout tiene que estar leído antes de entrar: al extraer o morir se persiste
            // encima, y sin la lectura previa se pisaría el loadout real.
            if (!await _stashScreen.EnsureLoadoutLoadedAsync())
            {
                ShowNotice(InventoryLoadFailedMessage);
                return;
            }
            if (!_searching) return; // canceló mientras cargaba
            if (Game.Presentation.Run.PlayerLoadoutService.IsInActiveRun) { ShowRejoinPanel(RejoinDefaultMessage); return; }

            // El servidor de la run lee el loadout de PlayFab: lo último que se tocó en el Stash
            // tiene que estar guardado antes de entrar, o arrancaría con una versión vieja.
            if (Game.Presentation.Run.PlayerLoadoutService.PendingSync)
            {
                _searchStatus.text = "Saving your inventory...";
                float giveUpAt = Time.time + SaveWaitSeconds;
                while (Game.Presentation.Run.PlayerLoadoutService.PendingSync && Time.time < giveUpAt)
                {
                    await System.Threading.Tasks.Task.Delay(100);
                    if (!_searching) return;
                }
                if (Game.Presentation.Run.PlayerLoadoutService.PendingSync)
                {
                    ShowNotice("Could not save your inventory. Check your connection and try again.");
                    return;
                }
            }

            // El servidor verifica el session ticket al entrar: que no esté por vencer.
            if (!await PlayFabSession.EnsureFreshAsync())
            {
                ShowNotice("Could not refresh your session. Check your connection and try again.");
                return;
            }
            if (!_searching) return;

            _searchStatus.text = "Entering the queue...";
            _matchmaking.StartSearch();
        }

        private void OnCancelSearchClicked()
        {
            _matchmaking?.CancelSearch();
            _searching = false;
            SetSearchPanel(false);
        }

        private void HandleMatchmakingState(MatchmakingService.State state)
        {
            switch (state)
            {
                case MatchmakingService.State.Searching:
                    _searchStatus.text = "Looking for other mages...";
                    break;

                case MatchmakingService.State.Matched:
                    _searching = false;
                    _searchStatus.text = "Run found. Connecting...";
                    ConnectToMatchServer();
                    break;

                case MatchmakingService.State.Idle:
                    _searching = false;
                    SetSearchPanel(false);
                    break;
            }
        }

        private void HandleMatchmakingFailed(string reason)
        {
            _searching = false;
            _searchStatus.text = reason;
            // Se deja el panel abierto con el motivo: el jugador cierra con Cancel cuando lo leyó.
        }


                private void HandleUnexpectedDisconnect(string reason)
        {
            NetworkDisconnectHandler.ConsumeDisconnectMessage();
            _matchmaking?.CancelSearch();

            // Falló una reconexión (o se cortó la conexión inicial) con una run todavía en curso.
            if (Game.Presentation.Run.PlayerLoadoutService.IsInActiveRun)
            {
                ShowRejoinPanel($"{reason}\n\n{RejoinDefaultMessage}");
                return;
            }

            ShowNotice(reason);
        }

        /// <summary>Reusa el panel de búsqueda como cartel de aviso: el jugador lo cierra con Cancel.</summary>
        private void ShowNotice(string message)
        {
            _searching = false;
            _searchStatus.text = message;
            _searchTimer.text = string.Empty;
            SetSearchPanel(true);
        }

        /// <summary>
        /// Conecta al servidor de la partida. Mientras la queue no tenga server allocation
        /// (requiere el Build desplegado en MPS), se usa la dirección de fallback del inspector.
        /// Cuando eso exista, ServerDetails trae IP y puerto reales y esto es lo único que cambia.
        /// </summary>
        private void ConnectToMatchServer()
        {
            string address;
            ushort port;

            var details = _matchmaking.ServerDetails;
            if (details != null && !string.IsNullOrEmpty(details.IPV4Address) && TryGetGamePort(details, out port))
            {
                address = details.IPV4Address;
            }
            else
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                // Solo desarrollo: queue sin server allocation → servidor local (LMA).
                address = _fallbackServerAddress;
                port = _fallbackServerPort;
#else
                // En release no hay a dónde ir: conectar a 127.0.0.1 no tiene sentido.
                Debug.LogError("[MainMenu] El match no trajo ServerDetails válidos.");
                ShowNotice("Could not get a server for the run. Please try again.");
                return;
#endif
            }

            ConnectTo(address, port);
        }

        private void ConnectTo(string address, ushort port)
        {
            var tugboat = InstanceFinder.TransportManager.GetTransport<Tugboat>();
            if (tugboat == null)
            {
                Debug.LogError("[MainMenu] No se encontró el transporte Tugboat.");
                return;
            }

            tugboat.SetClientAddress(address);
            tugboat.SetPort(port);
            RunServerEndpoint.Set(address, port);

            Debug.Log($"[MainMenu] Conectando a {address}:{port}");
            InstanceFinder.ClientManager.StartConnection();
            // La escena de run la carga el servidor: llega como escena global al conectar.
        }

        /// <summary>Puerto del servidor asignado, buscado por nombre (el que declara el Build en
        /// PlayFab). Si no está, el primero: compatible con Builds de un solo puerto.</summary>
        private bool TryGetGamePort(PlayFab.MultiplayerModels.ServerDetails details, out ushort port)
        {
            port = 0;
            if (details.Ports == null || details.Ports.Count == 0) return false;

            var chosen = details.Ports[0];
            foreach (var p in details.Ports)
                if (string.Equals(p.Name, _gamePortName, System.StringComparison.OrdinalIgnoreCase)) { chosen = p; break; }

            port = (ushort)chosen.Num;
            return true;
        }

        private void SetSearchPanel(bool visible)
            => _searchPanel.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
    }
}