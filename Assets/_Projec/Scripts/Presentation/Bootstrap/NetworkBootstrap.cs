using System;
using System.Collections;
using System.IO;
using System.Linq;
using FishNet;
using FishNet.Managing.Scened;
using FishNet.Transporting;
using FishNet.Transporting.Tugboat;
using UnityEngine;
#if ENABLE_PLAYFABSERVER_API
using PlayFab;
#endif

namespace Game.Presentation.Bootstrap
{
    /// <summary>
    /// Arranca la red según el rol del proceso, usando Tugboat (IP directa, sin Steam).
    ///
    /// Tres caminos posibles:
    /// - Build de servidor dedicado bajo PlayFab MPS: el puerto y el ciclo de vida los manda el
    ///   GSDK (ver StartServerWithGsdk). El puerto NUNCA se hardcodea, lo asigna la plataforma.
    /// - Build de servidor dedicado suelto (sin GSDK): usa el puerto de línea de comandos. Sirve
    ///   para probar el .exe de server a mano, sin Docker.
    /// - Cliente / host: rol por línea de comandos (-client / -host) o por _editorRole en el editor.
    ///
    /// IMPORTANTE: requiere "Start On Headless" DESACTIVADO en el ServerManager. Si queda activo,
    /// FishNet arranca el servidor solo con el puerto por defecto de Tugboat antes de que podamos
    /// leer el que asigna PlayFab.
    /// </summary>
    public class NetworkBootstrap : MonoBehaviour
    {
        [Header("Escena")]
        [SerializeField] private string _runSceneName = "Run";

        [Header("Editor (solo en el editor; ignorado si hay argumentos de rol y en builds)")]
        [SerializeField] private NetworkRole _editorRole = NetworkRole.None;
        [SerializeField] private string _editorAddress = "127.0.0.1";
        [SerializeField] private ushort _editorPort = 7770;

        [Header("PlayFab MPS")]
        [Tooltip("Nombre del puerto declarado en la configuración del Build de PlayFab. Debe coincidir exactamente.")]
        [SerializeField] private string _gamePortName = "game_port";
        [Tooltip("Segundos desde que PlayFab pasa el servidor a Active sin que se registre ningún jugador antes de cerrar el proceso (ej. el match se armó pero nadie llegó a conectar). Evita instancias Active vacías para siempre.")]
        [SerializeField] private float _noPlayersShutdownSeconds = 120f;
        [Tooltip("Solo dejar entrar (y reconectar) a los jugadores que PlayFab asignó a este servidor (InitialPlayers). Destildar solo si la nube rechaza jugadores válidos: el log de [Auth] muestra la lista.")]
        [SerializeField] private bool _requireMatchMembership = true;

        private const string GsdkConfigEnvVar = "GSDK_CONFIG_FILE";

        private void Start()
        {
            NetworkRole role = LaunchArgs.Role;
            string address = LaunchArgs.Address;
            ushort port = LaunchArgs.Port;

#if UNITY_EDITOR
            // Sin argumentos de rol en el editor: usar la configuración del inspector. Solo en el
            // editor: un build sin argumentos siempre arranca en el menú, aunque la escena se haya
            // guardado con _editorRole en Host o Client.
            if (role == NetworkRole.None)
            {
                role = _editorRole;
                address = _editorAddress;
                port = _editorPort;
            }
#endif

#if UNITY_SERVER
            // Un build de servidor dedicado no puede ser otra cosa, con o sin argumentos.
            role = NetworkRole.Server;
#endif

            if (role == NetworkRole.None)
                return; // arranque normal por menú, la red la levanta otra cosa

#if ENABLE_PLAYFABSERVER_API
            if (role == NetworkRole.Server && IsGsdkAvailable())
            {
                StartCoroutine(StartServerWithGsdk());
                return;
            }
#endif

            ConfigureTransport(address, port);
            StartRole(role);
        }

        /// <summary>El GSDK solo sirve si la plataforma nos dejó su archivo de configuración. Hay
        /// que chequearlo antes de llamar a Start(): si falta, el GSDK cierra el proceso.</summary>
        private static bool IsGsdkAvailable()
        {
            string configPath = Environment.GetEnvironmentVariable(GsdkConfigEnvVar);
            return !string.IsNullOrEmpty(configPath) && File.Exists(configPath);
        }

        /// <summary>Devuelve el transporte activo, o null si no es Tugboat.</summary>
        private static Tugboat GetTugboat()
        {
            var tugboat = InstanceFinder.TransportManager.GetTransport<Tugboat>();
            if (tugboat == null)
                Debug.LogError("[NetworkBootstrap] No se encontró el transporte Tugboat en el NetworkManager.");
            return tugboat;
        }

        /// <summary>Aplica dirección y puerto al transporte activo antes de conectar.</summary>
        private void ConfigureTransport(string address, ushort port)
        {
            var tugboat = GetTugboat();
            if (tugboat == null) return;

            tugboat.SetPort(port);
            tugboat.SetClientAddress(address);
        }

        private void StartRole(NetworkRole role)
        {
            switch (role)
            {
                case NetworkRole.Server:
                    InstanceFinder.ServerManager.StartConnection();
                    StartCoroutine(LoadRunWhenServerReady());
                    break;

                case NetworkRole.Client:
                    InstanceFinder.ClientManager.StartConnection();
                    break;

                case NetworkRole.Host:
                    InstanceFinder.ServerManager.StartConnection();
                    InstanceFinder.ClientManager.StartConnection();
                    StartCoroutine(LoadRunWhenServerReady());
                    break;
            }
        }

        /// <summary>Carga la run como escena global apenas el servidor está levantado. Esperar
        /// únicamente por IsServerStarted es deliberado: agregar IsClientStarted acá ya causó
        /// spawns por debajo del mapa.</summary>
        private IEnumerator LoadRunWhenServerReady()
        {
            while (!InstanceFinder.IsServerStarted) yield return null;

            SceneLoadData sld = new SceneLoadData(_runSceneName);
            sld.ReplaceScenes = ReplaceOption.All;
            InstanceFinder.SceneManager.LoadGlobalScenes(sld);
        }

#if ENABLE_PLAYFABSERVER_API
        /// <summary>
        /// Arranque bajo PlayFab MPS. El orden importa: primero avisamos que estamos inicializando
        /// (Start), después levantamos el servidor en el puerto que nos asignaron, y recién cuando
        /// la escena está cargada avisamos que puede entrar gente (ReadyForPlayers). Anunciar
        /// disponibilidad antes de tiempo haría que lleguen jugadores a un servidor a medio armar.
        /// </summary>
        private IEnumerator StartServerWithGsdk()
        {
            PlayFabMultiplayerAgentAPI.Start();

            if (_requireMatchMembership)
                PlayerIdentityAuthenticator.AllowedKeysProvider = () => PlayFabMultiplayerAgentAPI.GetInitialPlayers();
            PlayFabMultiplayerAgentAPI.OnShutDownCallback += OnGsdkShutdown;
            PlayFabMultiplayerAgentAPI.OnServerActiveCallback += OnGsdkServerActive;
            PlayFabMultiplayerAgentAPI.OnAgentErrorCallback += OnGsdkAgentError;

            // Un frame para que el GSDK termine de armar su agente en escena.
            yield return null;

            if (!TryGetGsdkPort(out ushort listeningPort))
            {
                Debug.LogError($"[NetworkBootstrap] No se encontró el puerto '{_gamePortName}' en la configuración del GSDK. Revisar que el nombre coincida con el del Build en PlayFab / MultiplayerSettings.json.");
                Application.Quit(1);
                yield break;
            }

            var tugboat = GetTugboat();
            if (tugboat == null) yield break;

            tugboat.SetPort(listeningPort);
            // Dentro del container hay que escuchar en todas las interfaces, no en localhost.
            tugboat.SetServerBindAddress("0.0.0.0", IPAddressType.IPv4);

            Debug.Log($"[NetworkBootstrap] GSDK OK. Escuchando en el puerto {listeningPort}.");

            InstanceFinder.ServerManager.StartConnection();

            yield return LoadRunWhenServerReady();

            // LoadGlobalScenes es asíncrono: la escena (y con ella el RunManager) todavía no
            // existe cuando la corrutina anterior termina. Sin esta espera, la suscripción se
            // pierde en silencio y el servidor nunca se entera de que la run terminó.
            float runManagerTimeout = 30f;
            while (Game.Presentation.Run.RunManager.Instance == null && runManagerTimeout > 0f)
            {
                runManagerTimeout -= Time.deltaTime;
                yield return null;
            }

            if (Game.Presentation.Run.RunManager.Instance != null)
            {
                Game.Presentation.Run.RunManager.Instance.OnRunEnded += HandleRunEndedOnDedicatedServer;
                // Cuántos jugadores trae el match: el RunManager no deja terminar la run hasta que
                // lleguen todos (o venza su gracia). Se lee cuando hace falta: se completa al pasar a Active.
                Game.Presentation.Run.RunManager.Instance.SetExpectedPlayersProvider(
                    () => PlayFabMultiplayerAgentAPI.GetInitialPlayers().Count);
                Debug.Log("[NetworkBootstrap] Suscripto a OnRunEnded: el proceso se cerrará al terminar la run.");
            }
            else
            {
                Debug.LogError("[NetworkBootstrap] El RunManager nunca apareció. El servidor NO se auto-cerrará al terminar la run.");
            }

            PlayFabMultiplayerAgentAPI.ReadyForPlayers();
            Debug.Log("[NetworkBootstrap] ReadyForPlayers enviado: el servidor queda en StandingBy.");
        }

        /// <summary>Busca el puerto de juego por nombre en la config que mandó la plataforma.</summary>
        private bool TryGetGsdkPort(out ushort listeningPort)
        {
            listeningPort = 0;

            var connectionInfo = PlayFabMultiplayerAgentAPI.GetGameServerConnectionInfo();
            if (connectionInfo?.GamePortsConfiguration == null) return false;

            var gamePort = connectionInfo.GamePortsConfiguration
                .FirstOrDefault(p => string.Equals(p.Name, _gamePortName, StringComparison.OrdinalIgnoreCase));

            if (gamePort == null) return false;

            listeningPort = (ushort)gamePort.ServerListeningPort;
            return true;
        }

        /// <summary>PlayFab pasó el servidor a Active: ya hay jugadores asignados en camino.</summary>
        private void OnGsdkServerActive()
        {
            Debug.Log($"[NetworkBootstrap] Servidor Active: aceptando jugadores ({PlayFabMultiplayerAgentAPI.GetInitialPlayers().Count} esperados).");
            StartCoroutine(ShutdownIfNobodyJoins());
        }

        /// <summary>Watchdog: si después de pasar a Active no se registra ningún jugador en
        /// _noPlayersShutdownSeconds, el proceso se cierra (PlayFab nunca devuelve solo un servidor
        /// Active a StandingBy). No toca el timer de la run ni el cierre normal por OnRunEnded.</summary>
        private IEnumerator ShutdownIfNobodyJoins()
        {
            yield return new WaitForSeconds(_noPlayersShutdownSeconds);

            var run = Game.Presentation.Run.RunManager.Instance;
            if (run != null && run.HasRunStarted) yield break;

            Debug.LogWarning($"[NetworkBootstrap] Ningún jugador entró en {_noPlayersShutdownSeconds:0}s desde Active. Cerrando el proceso.");
            InstanceFinder.ServerManager.StopConnection(true);
            Application.Quit();
        }

        /// <summary>PlayFab pide terminar. Hay que cerrar el proceso o la plataforma lo marca como
        /// cierre sucio.</summary>
        private void OnGsdkShutdown()
        {
            Debug.Log("[NetworkBootstrap] Shutdown pedido por PlayFab, cerrando.");
            InstanceFinder.ServerManager.StopConnection(true);
            Application.Quit();
        }

        /// <summary>La run terminó (todos extrajeron o murieron). Bajo MPS, este proceso solo
        /// sirve para UNA partida — hay que cerrarlo para que PlayFab libere el cupo y arranque
        /// uno genuinamente nuevo para la próxima. Un margen antes de cerrar para que los
        /// clientes terminen de ver la pantalla de resultados y desconecten en paz.</summary>
        private void HandleRunEndedOnDedicatedServer()
        {
            Debug.Log("[NetworkBootstrap] Run terminada. Cerrando el proceso en unos segundos para liberar la instancia.");
            StartCoroutine(ShutdownAfterRunEnded());
        }

        private IEnumerator ShutdownAfterRunEnded()
        {
            yield return new WaitForSeconds(8f);
            InstanceFinder.ServerManager.StopConnection(true);
            Application.Quit();
        }

        private void OnGsdkAgentError(string error)
        {
            Debug.LogError($"[NetworkBootstrap] Error de comunicación con el agente de PlayFab: {error}");
        }

        private void OnDestroy()
        {
            PlayFabMultiplayerAgentAPI.OnShutDownCallback -= OnGsdkShutdown;
            PlayFabMultiplayerAgentAPI.OnServerActiveCallback -= OnGsdkServerActive;
            PlayFabMultiplayerAgentAPI.OnAgentErrorCallback -= OnGsdkAgentError;
        }
#endif
    }
}