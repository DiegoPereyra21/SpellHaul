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

        private void OnEnable()
        {
            _document = GetComponent<UIDocument>();
            var root = _document.rootVisualElement;

            root.Q<Button>("find-match-button").clicked += OnFindMatchClicked;
            root.Q<Button>("stash-button").clicked += OnStashClicked;
            root.Q<Button>("quit-button").clicked += () => Application.Quit();
            root.Q<Button>("search-cancel").clicked += OnCancelSearchClicked;

            _searchPanel = root.Q<VisualElement>("search-panel");
            _searchStatus = root.Q<Label>("search-status");
            _searchTimer = root.Q<Label>("search-timer");

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

        private void HandleSessionReady() => SetGameplayButtonsEnabled(true);

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
            if (!await _stashScreen.TryShowAsync())
                ShowNotice(InventoryLoadFailedMessage);
        }

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

            var tugboat = InstanceFinder.TransportManager.GetTransport<Tugboat>();
            if (tugboat == null)
            {
                Debug.LogError("[MainMenu] No se encontró el transporte Tugboat.");
                return;
            }

            tugboat.SetClientAddress(address);
            tugboat.SetPort(port);

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