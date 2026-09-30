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
        private VisualElement _menuRoot;
        private Button _findMatchButton;
        private Button _stashButton;
        private Button _quitButton;

        // Búsqueda de partida: banner arriba (no bloquea; se puede ordenar el stash mientras tanto).
        private VisualElement _searchBanner;
        private Label _searchStatus;
        private Label _searchTimer;
        private float _searchStartTime;
        private bool _searching;       // búsqueda viva: los pasos async cortan si pasa a false
        private bool _searchActive;    // banner visible y loadout bloqueado (hasta conectar o cancelar)

        // Paneles emergentes (aviso / run en curso): bloquean el menú con un fondo.
        private VisualElement _backdrop;
        private VisualElement _noticePanel;
        private Label _noticeMessage;
        private VisualElement _optionsPanel;
        private Button _optionsButton;
        private float _lastVolumePreview;

        // El menú se dibuja encima del Stash (para que el banner y los avisos se vean con el Stash
        // abierto); mientras el Stash está abierto el menú queda en "modo overlay" (ver USS).
        private const int SortingOrderAboveStash = 10;

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

            _menuRoot = root.Q<VisualElement>("menu-root");
            _findMatchButton = root.Q<Button>("find-match-button");
            _stashButton = root.Q<Button>("stash-button");
            _quitButton = root.Q<Button>("quit-button");
            _findMatchButton.clicked += OnFindMatchClicked;
            _stashButton.clicked += OnStashClicked;
            _quitButton.clicked += () => Application.Quit();
            root.Q<Button>("search-cancel").clicked += OnCancelSearchClicked;

            _searchBanner = root.Q<VisualElement>("search-banner");
            _searchStatus = root.Q<Label>("search-status");
            _searchTimer = root.Q<Label>("search-timer");

            _backdrop = root.Q<VisualElement>("modal-backdrop");
            _noticePanel = root.Q<VisualElement>("notice-panel");
            _noticeMessage = root.Q<Label>("notice-message");
            root.Q<Button>("notice-close").clicked += HideNotice;

            _optionsPanel = root.Q<VisualElement>("options-panel");
            _optionsButton = root.Q<Button>("options-button");
            if (_optionsButton != null) _optionsButton.clicked += ShowOptions;
            var optionsClose = root.Q<Button>("options-close");
            if (optionsClose != null) optionsClose.clicked += HideOptions;
            BindVolumeSlider(root.Q<Slider>("volume-master"), () => Game.Presentation.Audio.AudioVolumes.Master, v => Game.Presentation.Audio.AudioVolumes.Master = v);
            BindVolumeSlider(root.Q<Slider>("volume-effects"), () => Game.Presentation.Audio.AudioVolumes.Effects, v => Game.Presentation.Audio.AudioVolumes.Effects = v);
            BindVolumeSlider(root.Q<Slider>("volume-interface"), () => Game.Presentation.Audio.AudioVolumes.Interface, v => Game.Presentation.Audio.AudioVolumes.Interface = v);

            _document.sortingOrder = SortingOrderAboveStash;
            Game.Presentation.Audio.GameAudio.AttachButtonSounds(root);
            if (_stashScreen != null)
            {
                _stashScreen.Shown += RefreshMenuState;
                _stashScreen.Hidden += RefreshMenuState;
            }

            _rejoinPanel = root.Q<VisualElement>("rejoin-panel");
            _rejoinStatus = root.Q<Label>("rejoin-status");
            _rejoinReconnect = root.Q<Button>("rejoin-reconnect");
            _rejoinAbandon = root.Q<Button>("rejoin-abandon");
            if (_rejoinReconnect != null) _rejoinReconnect.clicked += OnRejoinReconnectClicked;
            if (_rejoinAbandon != null) _rejoinAbandon.clicked += OnRejoinAbandonClicked;

            // Jugar y tocar el stash dependen del loadout persistente listo (login a PlayFab
            // resuelto) — entrar antes jugaría contra el backend local descartable.
            RefreshMenuState();
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
            if (_stashScreen != null)
            {
                _stashScreen.Shown -= RefreshMenuState;
                _stashScreen.Hidden -= RefreshMenuState;
                _stashScreen.SetLoadoutLocked(false);
            }

            if (_matchmaking != null)
            {
                _matchmaking.OnStateChanged -= HandleMatchmakingState;
                _matchmaking.OnFailed -= HandleMatchmakingFailed;
            }

            NetworkDisconnectHandler.OnUnexpectedDisconnect -= HandleUnexpectedDisconnect;
        }

        private void Update()
        {
            if (!_searching || _searchTimer == null) return;

            float elapsed = Time.time - _searchStartTime;
            _searchTimer.text = $"{(int)(elapsed / 60f)}:{(int)(elapsed % 60f):00}";
        }

        private void HandleSessionReady()
        {
            RefreshMenuState();
            _ = CheckActiveRunAsync(null);
        }

        // ---------- Estado del menú ----------

        private bool NoticeVisible => _noticePanel != null && _noticePanel.style.display == DisplayStyle.Flex;
        private bool OptionsVisible => _optionsPanel != null && _optionsPanel.style.display == DisplayStyle.Flex;
        private bool RejoinVisible => _rejoinPanel != null && _rejoinPanel.style.display == DisplayStyle.Flex;

        /// <summary>
        /// Único lugar que decide qué se puede tocar. Con un panel emergente abierto, nada del menú
        /// (el fondo lo tapa y los botones se deshabilitan). Buscando partida: no se puede volver a
        /// buscar y el loadout del Stash queda bloqueado (el stash sí se ordena). Con el Stash
        /// abierto, el menú queda en modo overlay: solo el banner y los avisos, encima del Stash.
        /// </summary>
        private void RefreshMenuState()
        {
            if (_document == null) return;

            bool modal = NoticeVisible || RejoinVisible || OptionsVisible;
            bool ready = PlayFabSession.IsReady;

            if (_backdrop != null) _backdrop.style.display = modal ? DisplayStyle.Flex : DisplayStyle.None;
            if (_searchBanner != null) _searchBanner.style.display = _searchActive && !modal ? DisplayStyle.Flex : DisplayStyle.None;

            _findMatchButton?.SetEnabled(ready && !modal && !_searchActive);
            _stashButton?.SetEnabled(ready && !modal);
            _quitButton?.SetEnabled(!modal);
            _optionsButton?.SetEnabled(!modal);

            _stashScreen?.SetLoadoutLocked(_searchActive);

            bool overlay = _stashScreen != null && _stashScreen.IsVisible;
            if (_menuRoot != null)
            {
                _menuRoot.EnableInClassList("overlay-mode", overlay);
                // En overlay los clics sobre el fondo del menú pasan al Stash de abajo.
                _menuRoot.pickingMode = overlay ? PickingMode.Ignore : PickingMode.Position;
            }
        }

        private void ShowSearchBanner(string status)
        {
            _searchActive = true;
            _searchStatus.text = status;
            RefreshMenuState();
        }

        /// <summary>Termina la búsqueda en la UI (banner oculto, loadout libre).</summary>
        private void EndSearchUi()
        {
            _searching = false;
            _searchActive = false;
            RefreshMenuState();
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
            _searchActive = false;
            if (_noticePanel != null) _noticePanel.style.display = DisplayStyle.None;
            _stashScreen?.Hide();

            _abandonArmed = false;
            if (_rejoinAbandon != null) _rejoinAbandon.text = "Abandon Run";
            if (_rejoinStatus != null) _rejoinStatus.text = message;
            _rejoinReconnect?.SetEnabled(true);
            _rejoinAbandon?.SetEnabled(true);
            if (_rejoinPanel != null) _rejoinPanel.style.display = DisplayStyle.Flex;
            RefreshMenuState();
        }

        private void HideRejoinPanel()
        {
            if (_rejoinPanel != null) _rejoinPanel.style.display = DisplayStyle.None;
            RefreshMenuState();
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
            _searchTimer.text = "0:00";
            ShowSearchBanner("Loading your inventory...");

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
            EndSearchUi();
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
                    Game.Presentation.Audio.GameAudio.Ui(l => l.MatchFound);
                    _ = ConnectToMatchServerAsync();
                    break;

                case MatchmakingService.State.Idle:
                    EndSearchUi();
                    break;
            }
        }

        private void HandleMatchmakingFailed(string reason)
        {
            ShowNotice(reason); // termina la búsqueda y muestra el motivo hasta que lo cierre
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

        /// <summary>Aviso emergente: termina cualquier búsqueda y bloquea el menú hasta que lo cierre.</summary>
        private void ShowNotice(string message)
        {
            _searching = false;
            _searchActive = false;
            if (_noticeMessage != null) _noticeMessage.text = message;
            if (_noticePanel != null) _noticePanel.style.display = DisplayStyle.Flex;
            Game.Presentation.Audio.GameAudio.Ui(l => l.UiNotice);
            RefreshMenuState();
        }

        // ---------- Opciones ----------

        private void ShowOptions()
        {
            if (_optionsPanel != null) _optionsPanel.style.display = DisplayStyle.Flex;
            RefreshMenuState();
        }

        private void HideOptions()
        {
            Game.Presentation.Audio.AudioVolumes.Save();
            if (_optionsPanel != null) _optionsPanel.style.display = DisplayStyle.None;
            RefreshMenuState();
        }

        /// <summary>Slider de volumen: arranca con el valor guardado y lo aplica al moverlo (con un
        /// sonido de prueba, espaciado para no saturar mientras se arrastra).</summary>
        private void BindVolumeSlider(Slider slider, System.Func<float> get, System.Action<float> set)
        {
            if (slider == null) return;
            slider.SetValueWithoutNotify(get());
            slider.RegisterValueChangedCallback(evt =>
            {
                set(evt.newValue);
                if (Time.unscaledTime - _lastVolumePreview < 0.15f) return;
                _lastVolumePreview = Time.unscaledTime;
                Game.Presentation.Audio.GameAudio.Ui(l => l.UiClick);
            });
        }

        private void HideNotice()
        {
            if (_noticePanel != null) _noticePanel.style.display = DisplayStyle.None;
            RefreshMenuState();
        }

        /// <summary>
        /// Conecta al servidor de la partida. Mientras la queue no tenga server allocation
        /// (requiere el Build desplegado en MPS), se usa la dirección de fallback del inspector.
        /// Cuando eso exista, ServerDetails trae IP y puerto reales y esto es lo único que cambia.
        /// </summary>
        /// <summary>
        /// Hay partida: se cierra el Stash y, antes de conectar, se espera a que se guarde lo que se
        /// haya ordenado en él mientras se buscaba (el servidor marca la run al entrar, y un cambio
        /// que llegara después quedaría rechazado).
        /// </summary>
        private async System.Threading.Tasks.Task ConnectToMatchServerAsync()
        {
            _stashScreen?.Hide();

            float giveUpAt = Time.time + SaveWaitSeconds;
            while (Game.Presentation.Run.PlayerLoadoutService.PendingSync && Time.time < giveUpAt)
                await System.Threading.Tasks.Task.Delay(100);
            if (this == null) return; // cambió la escena mientras tanto

            ConnectToMatchServer();
        }

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
    }
}