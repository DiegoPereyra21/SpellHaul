using FishNet;
using FishNet.Object;
using Game.Core.Items;
using UnityEngine;

namespace Game.Presentation.Combat
{
    /// <summary>
    /// Al morir, spawnea un LootContainer con el resultado de una tabla de loot. Reutilizable
    /// para cualquier enemigo con Health. Escucha el evento OnDied de Health.
    /// </summary>
    [RequireComponent(typeof(Health))]
    public class LootDropper : NetworkBehaviour
    {
        [SerializeField] private LootTableSO _lootTable;
        [SerializeField] private GameObject _lootContainerPrefab;

        private Health _health;

        private void Awake()
        {
            _health = GetComponent<Health>();
        }

        public override void OnStartServer()
        {
            _health.OnDied += HandleDied;
        }

        public override void OnStopServer()
        {
            if (_health != null) _health.OnDied -= HandleDied;
        }

        private void HandleDied(int instigator)
        {
            if (_lootTable == null || _lootContainerPrefab == null) return;
            if (Game.Presentation.Run.PracticeSession.Active) return; // práctica: sin loot

            var loot = _lootTable.Roll();
            if (loot.Count == 0) return; // no cayó nada

            Vector3 pos = transform.position;

            // Raycast al piso, bien largo (200) para que encuentre el suelo aunque el enemigo
            // muera muy alto — sin esto, un raycast corto se quedaba sin tocar nada y el
            // contenedor quedaba flotando en la altura de la muerte.
            if (Physics.Raycast(transform.position + Vector3.up * 0.5f, Vector3.down, out RaycastHit groundHit, 200f, ~0, QueryTriggerInteraction.Ignore))
                pos = groundHit.point + Vector3.up * 0.1f;

            // Pequeño desplazamiento aleatorio para que contenedores simultáneos no queden exactamente encima.
            pos += new Vector3(Random.Range(-0.4f, 0.4f), 0f, Random.Range(-0.4f, 0.4f));

            GameObject obj = Instantiate(_lootContainerPrefab, pos, Quaternion.identity);
            if (obj.TryGetComponent(out LootContainer container))
            {
                InstanceFinder.ServerManager.Spawn(obj);
                container.ServerFill(loot);
            }
        }
    }
}