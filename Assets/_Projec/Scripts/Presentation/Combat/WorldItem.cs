using FishNet.Object;
using FishNet.Object.Synchronizing;
using Game.Core.Items;
using UnityEngine;

namespace Game.Presentation.Combat
{
    /// <summary>
    /// Un item tirado en el mundo (NetworkObject). Guarda qué item es y cuánto (ItemStack),
    /// sincronizado. Server-authoritative: el servidor decide cuándo se recoge y despawnea.
    /// Al spawnear en el cliente, instancia el mesh del item (o el genérico si no tiene).
    /// </summary>
    public class WorldItem : NetworkBehaviour
    {
        [SerializeField] private ItemDatabase _database;
        [SerializeField] private GameObject _defaultWorldPrefab; // cubo placeholder

        private readonly SyncVar<string> _itemId   = new SyncVar<string>();
        private readonly SyncVar<int>    _quantity  = new SyncVar<int>();
        private readonly SyncVar<float>  _durability = new SyncVar<float>();

        public string ItemId   => _itemId.Value;
        public int    Quantity => _quantity.Value;

        private GameObject _spawnedMesh;

        [Server]
        public void ServerSetItem(ItemStack stack)
        {
            _itemId.Value    = stack.ItemId;
            _quantity.Value  = stack.Quantity;
            _durability.Value = stack.Durability;
        }

        public override void OnStartClient()
        {
            base.OnStartClient();

            // La física la simula solo el servidor; en los clientes la posición llega por
            // NetworkTransform. Con el Rigidbody activo en los dos, la simulación local peleaba
            // con la replicada (items temblando o deslizándose).
            if (!base.IsServerInitialized && TryGetComponent(out Rigidbody body))
                body.isKinematic = true;

            ApplyMesh();
            // Si el SyncVar llega después del OnStartClient (raro pero posible), re-aplicar.
            _itemId.OnChange += (_, _, _) => ApplyMesh();
        }

        private void ApplyMesh()
        {
            if (string.IsNullOrEmpty(_itemId.Value)) return;

            // Limpiar mesh anterior si hubiera.
            if (_spawnedMesh != null) Destroy(_spawnedMesh);

            GameObject prefabToUse = _defaultWorldPrefab;

            if (_database != null)
            {
                ItemSO def = _database.GetById(_itemId.Value);
                if (def != null && def.WorldPrefab != null)
                    prefabToUse = def.WorldPrefab;
            }

            if (prefabToUse == null) return;

            _spawnedMesh = Instantiate(prefabToUse, transform.position, transform.rotation, transform);
        }

        public ItemStack ToStack() => new ItemStack(_itemId.Value, _quantity.Value, _durability.Value);

        public string GetDisplayName(ItemDatabase db)
        {
            ItemSO def = db != null ? db.GetById(_itemId.Value) : null;
            return def != null ? def.DisplayName : _itemId.Value;
        }
    }
}