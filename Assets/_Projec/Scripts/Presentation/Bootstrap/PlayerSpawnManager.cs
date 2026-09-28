using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Managing.Scened;
using FishNet.Object;
using FishNet.Transporting;
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

        private void Start()
        {
            if (_networkManager == null)
                _networkManager = FishNet.InstanceFinder.NetworkManager;

            _networkManager.SceneManager.OnClientLoadedStartScenes += OnClientLoadedStartScenes;
            _networkManager.SceneManager.OnClientPresenceChangeEnd += OnClientPresenceChangeEnd;
            _networkManager.ServerManager.OnRemoteConnectionState += OnRemoteConnectionState;
        }

        private void OnDestroy()
        {
            if (_networkManager == null) return;
            _networkManager.SceneManager.OnClientLoadedStartScenes -= OnClientLoadedStartScenes;
            _networkManager.SceneManager.OnClientPresenceChangeEnd -= OnClientPresenceChangeEnd;
            _networkManager.ServerManager.OnRemoteConnectionState -= OnRemoteConnectionState;
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
            if (args.ConnectionState == RemoteConnectionState.Stopped)
                _pending.Remove(conn);
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

            Transform point = PickSpawnPoint();
            Vector3 pos = point != null ? point.position : Vector3.zero;
            Quaternion rot = point != null ? point.rotation : Quaternion.identity;

            NetworkObject nob = _networkManager.GetPooledInstantiated(_playerPrefab, pos, rot, true);
            _networkManager.ServerManager.Spawn(nob, conn);
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