using System.Collections.Generic;
using FishNet;
using FishNet.Object;
using UnityEngine;

namespace Game.Presentation.Combat
{
    /// <summary>
    /// Puebla el mundo de enemigos al empezar la run y los limpia al reiniciar (server-authoritative).
    /// Consume los EnemySpawnPoint de la escena (fuente temporal; a futuro, generador procedural).
    /// Pieza definitiva: su rol no cambia aunque cambie de dónde vienen las posiciones.
    /// </summary>
    public class EnemySpawner : NetworkBehaviour
    {
        [Tooltip("Segundos hasta que un enemigo muerto reaparece en su punto. 0 = no reaparece (run normal). Para el campo de práctica.")]
        [SerializeField] private float _respawnSeconds = 0f;

        private readonly List<NetworkObject> _spawned = new List<NetworkObject>();

        public override void OnStartServer()
        {
            SpawnAll();
        }

        public override void OnStopServer()
        {
            DespawnAll();
        }

        private void SpawnAll()
        {
            // Recolectar todos los puntos de spawn de la escena.
            var points = FindObjectsByType<EnemySpawnPoint>(FindObjectsSortMode.None);

            foreach (var point in points)
            {
                if (point.EnemyPrefab == null) continue;

                SpawnAt(point);
            }

            Debug.Log($"[EnemySpawner] {_spawned.Count} enemigos generados en la run.");
        }

        private void SpawnAt(EnemySpawnPoint point)
        {
            GameObject enemy = Instantiate(point.EnemyPrefab, point.Position, point.Rotation);
            InstanceFinder.ServerManager.Spawn(enemy);

            _spawned.RemoveAll(n => n == null || !n.IsSpawned); // muertos de respawns anteriores
            if (enemy.TryGetComponent(out NetworkObject nob))
                _spawned.Add(nob);

            if (_respawnSeconds > 0f && enemy.TryGetComponent(out Health health))
                health.OnDied += _ => { if (isActiveAndEnabled) StartCoroutine(RespawnLater(point)); };
        }

        private System.Collections.IEnumerator RespawnLater(EnemySpawnPoint point)
        {
            yield return new WaitForSeconds(_respawnSeconds);
            if (point != null && base.IsServerStarted) SpawnAt(point);
        }

        private void DespawnAll()
        {
            StopAllCoroutines();
            _spawned.RemoveAll(n => n == null || !n.IsSpawned);
            DespawnSpawned();
        }

        private void DespawnSpawned()
        {
            foreach (var nob in _spawned)
            {
                if (nob != null && nob.IsSpawned)
                    nob.Despawn();
            }
            _spawned.Clear();
        }
    }
}