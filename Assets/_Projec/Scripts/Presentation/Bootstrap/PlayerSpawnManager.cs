using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Managing.Scened;
using FishNet.Object;
using FishNet.Transporting;
using Game.Core.Items;
using Game.Presentation.Combat;
using Game.Presentation.Player;
using UnityEngine;

namespace Game.Presentation.Bootstrap
{
    /// <summary>
    /// Spawns each connecting player at a random registered PlayerSpawnPoint.
    /// Server-authoritative. Spawn points register themselves from the Run scene.
    /// </summary>
    public class PlayerSpawnManager : MonoBehaviour
    {
        [SerializeField] private NetworkManager _networkManager;
        [SerializeField] private NetworkObject _playerPrefab;

        private static readonly List<PlayerSpawnPoint> _points = new();
        private readonly List<int> _bag = new();

        public static void Register(PlayerSpawnPoint p)
        {
            if (!_points.Contains(p)) _points.Add(p);
        }

        public static void Unregister(PlayerSpawnPoint p)
        {
            _points.Remove(p);
        }

        // Conexiones que ya terminaron de cargar sus escenas iniciales pero todavía no están en la
        // escena de la run (llegaron mientras el servidor la cargaba). Se spawnean al entrar.
        private readonly HashSet<NetworkConnection> _pending = new();

        // Server-only: personaje de cada jugador en esta run, por clave de identidad
        // (PlayerIdentityAuthenticator). Permite volver a tomar el mismo personaje al reconectar.
        private readonly Dictionary<string, NetworkObject> _bodiesByKey = new();

        /// <summary>Instancia activa (vive con el NetworkManager).</summary>
        public static PlayerSpawnManager Instance { get; private set; }

        private void Start()
        {
            Instance = this;
            if (_networkManager == null)
                _networkManager = FishNet.InstanceFinder.NetworkManager;

            _networkManager.SceneManager.OnClientLoadedStartScenes += OnClientLoadedStartScenes;
            _networkManager.SceneManager.OnClientPresenceChangeEnd += OnClientPresenceChangeEnd;
            _networkManager.ServerManager.OnRemoteConnectionState += OnRemoteConnectionState;
            _networkManager.ServerManager.OnServerConnectionState += OnServerConnectionState;
        }

        /// <summary>
        /// Este componente vive con el NetworkManager (persiste entre escenas). Al apagar el servidor
        /// se olvida todo lo de esa partida: si no, la segunda vez que se levanta un host en el mismo
        /// proceso (ej. volver a entrar al campo de práctica) el jugador "ya tenía" un personaje de la
        /// sesión anterior (despawneado) y se lo rechazaba como si hubiera abandonado la run.
        /// </summary>
        private void OnServerConnectionState(ServerConnectionStateArgs args)
        {
            if (args.ConnectionState != LocalConnectionState.Stopped) return;
            _bodiesByKey.Clear();
            _pending.Clear();
            _bag.Clear();
            StopAllCoroutines(); // respawns de práctica pendientes
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_networkManager == null) return;
            _networkManager.SceneManager.OnClientLoadedStartScenes -= OnClientLoadedStartScenes;
            _networkManager.SceneManager.OnClientPresenceChangeEnd -= OnClientPresenceChangeEnd;
            _networkManager.ServerManager.OnRemoteConnectionState -= OnRemoteConnectionState;
            _networkManager.ServerManager.OnServerConnectionState -= OnServerConnectionState;
        }

        /// <summary>
        /// OnClientLoadedStartScenes se dispara con lo que el servidor tenía cargado AL CONECTAR.
        /// Si la conexión llegó mientras la run se seguía cargando (típico en host o conexión
        /// directa apenas arranca el servidor), la run no está entre esas escenas: spawnear ahí
        /// dejaba al jugador en (0,0,0) sin suelo, o con el suelo todavía sin cargar en su cliente,
        /// y caía del mundo. Por eso se spawnea recién cuando la conexión está en la escena de la run.
        /// </summary>
        private void OnClientLoadedStartScenes(NetworkConnection conn, bool asServer)
        {
            if (!asServer) return;
            if (_playerPrefab == null) return;

            if (IsInRunScene(conn))
                SpawnPlayer(conn);
            else
                _pending.Add(conn);
        }

        /// <summary>Server-only. La conexión entró (o salió) de una escena: si estaba esperando la run, spawnear.</summary>
        private void OnClientPresenceChangeEnd(ClientPresenceChangeEventArgs args)
        {
            if (!args.Added) return;
            if (!_pending.Contains(args.Connection)) return;
            if (!IsInRunScene(args.Connection)) return;

            _pending.Remove(args.Connection);
            SpawnPlayer(args.Connection);
        }

        private void OnRemoteConnectionState(NetworkConnection conn, RemoteConnectionStateArgs args)
        {
            if (args.ConnectionState != RemoteConnectionState.Stopped) return;

            _pending.Remove(conn);

            // El personaje se queda en el mundo sin dueño (el prefab tiene Prevent Despawn On
            // Disconnect): sigue siendo vulnerable y cuenta como vivo para la run hasta que muera,
            // extraiga o su dueño vuelva a tomarlo.
            foreach (var body in _bodiesByKey.Values)
            {
                if (body == null || !body.IsSpawned || body.OwnerId != conn.ClientId) continue;
                body.RemoveOwnership();
                Debug.Log($"[PlayerSpawnManager] Conexión {conn.ClientId} se fue: su personaje queda en la run esperando que vuelva.");
            }
        }

        /// <summary>La escena de la run es la de los spawn points (se registran al habilitarse en ella).</summary>
        private static bool IsInRunScene(NetworkConnection conn)
        {
            if (_points.Count == 0 || _points[0] == null) return false;
            return conn.Scenes.Contains(_points[0].gameObject.scene);
        }

        private void SpawnPlayer(NetworkConnection conn)
        {
            if (!conn.IsActive) return;

            PlayerIdentityAuthenticator.TryGetPlayerKey(conn, out string key);
            if (key != null && _bodiesByKey.TryGetValue(key, out NetworkObject body))
            {
                TryReclaimBody(conn, key, body);
                return;
            }

            Transform point = PickSpawnPoint();
            Vector3 pos = point != null ? point.position : Vector3.zero;
            Quaternion rot = point != null ? point.rotation : Quaternion.identity;

            NetworkObject nob = _networkManager.GetPooledInstantiated(_playerPrefab, pos, rot, true);
            _networkManager.ServerManager.Spawn(nob, conn);

            if (key != null) _bodiesByKey[key] = nob;
        }

        /// <summary>
        /// Server-only. El jugador ya tuvo un personaje en esta run. Si sigue vivo y en juego, vuelve
        /// a ser suyo (reconexión). Si murió o extrajo, se le avisa el resultado y se lo desconecta.
        /// Si el personaje ya no existe, no se le da uno nuevo: sería entrar de nuevo con el loadout.
        /// </summary>
        private void TryReclaimBody(NetworkConnection conn, string key, NetworkObject body)
        {
            if (body == null || !body.IsSpawned)
            {
                PlayerIdentityAuthenticator.RejectWithOutcome(_networkManager, conn, RunOutcome.LeftRun);
                return;
            }

            if (body.TryGetComponent(out PlayerAvatarState avatar) && avatar.IsControlDisabled)
            {
                bool extracted = body.TryGetComponent(out PlayerExtractionState ext) && ext.IsExtracted;
                // Si extrajo, se reenvía lo extraído: el guardado original pudo no llegar si se
                // desconectó justo al extraer.
                InventorySnapshot loot = extracted && body.TryGetComponent(out RunInventory inv) ? inv.TakeSnapshot() : null;
                PlayerIdentityAuthenticator.RejectWithOutcome(_networkManager, conn,
                    extracted ? RunOutcome.Extracted : RunOutcome.DiedWhileAway, loot);
                return;
            }

            if (body.Owner.IsActive && body.OwnerId != conn.ClientId)
                body.RemoveOwnership(); // conexión vieja todavía sin cerrar del todo

            body.GiveOwnership(conn);
            Debug.Log($"[PlayerSpawnManager] {key} volvió a la run: recupera su personaje.");
        }

        /// <summary>
        /// Server-only. Campo de práctica: tras 'delay' segundos reemplaza el personaje muerto por uno
        /// nuevo en un spawn. El dueño vuelve a mandar su loadout al spawnear (RunInventory), que en
        /// práctica nunca cambió.
        /// </summary>
        public void ServerRespawnAfter(NetworkObject oldBody, float delay)
        {
            if (oldBody != null) StartCoroutine(RespawnRoutine(oldBody, delay));
        }

        private System.Collections.IEnumerator RespawnRoutine(NetworkObject oldBody, float delay)
        {
            yield return new WaitForSeconds(delay);
            if (oldBody == null || !oldBody.IsSpawned) yield break;

            NetworkConnection conn = oldBody.Owner;
            if (conn != null && PlayerIdentityAuthenticator.TryGetPlayerKey(conn, out string key) && key != null)
                _bodiesByKey.Remove(key);

            oldBody.Despawn();
            if (conn != null && conn.IsActive) SpawnPlayer(conn);
        }

        private Transform PickSpawnPoint()
        {
            if (_points.Count == 0) return null;

            if (_bag.Count == 0)
                for (int i = 0; i < _points.Count; i++)
                    _bag.Add(i);

            int pick = Random.Range(0, _bag.Count);
            int index = _bag[pick];
            _bag.RemoveAt(pick);

            index = Mathf.Clamp(index, 0, _points.Count - 1); // safety if points changed
            return _points[index].transform;
        }
    }
}