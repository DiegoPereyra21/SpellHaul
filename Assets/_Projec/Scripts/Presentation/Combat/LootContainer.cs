using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using Game.Core.Items;
using UnityEngine;

namespace Game.Presentation.Combat
{
    /// <summary>
    /// Contenedor de loot en el mundo (cadáver, y a futuro mochila tirada). Guarda varios
    /// items (SyncList), server-authoritative. Nunca se despawnea al vaciarse — en cambio,
    /// cambia de color visualmente según si tiene contenido o no.
    /// </summary>
    public class LootContainer : NetworkBehaviour
    {
        [Header("Visual (color según si tiene loot)")]
        [SerializeField] private Renderer _renderer;
        [SerializeField] private Color _emptyColor = new Color(0.4f, 0.4f, 0.4f);
        [SerializeField] private Color _hasLootColor = new Color(0.85f, 0.65f, 0.15f);
        [Tooltip("URP Lit usa \"_BaseColor\"; Built-in Standard usa \"_Color\".")]
        [SerializeField] private string _colorProperty = "_BaseColor";

        private readonly SyncList<ItemStack> _contents = new SyncList<ItemStack>(new SyncTypeSettings(Game.Presentation.Combat.NetSyncRates.EveryTick));
        private MaterialPropertyBlock _mpb;
        private int _colorPropertyId;

        public IReadOnlyList<ItemStack> Contents => _contents;
        public event System.Action OnContentsChanged;

        public bool IsEmpty => _contents.Count == 0;

        private void Awake()
        {
            _colorPropertyId = Shader.PropertyToID(_colorProperty);
        }

        /// <summary>Server-only. Llena el contenedor con una colección de items.</summary>
        [Server]
        public void ServerFill(IEnumerable<ItemStack> items)
        {
            foreach (var s in items)
                if (!s.IsEmpty) _contents.Add(s);
        }

        /// <summary>Server-only. Saca un item por índice. Devuelve el stack sacado. Nunca despawnea.</summary>
        [Server]
        public bool ServerTryTake(int index, out ItemStack taken)
        {
            taken = ItemStack.Empty;
            if (index < 0 || index >= _contents.Count) return false;

            taken = _contents[index];
            _contents.RemoveAt(index);
            return true;
        }

        /// <summary>
        /// Server-only. Agrega un stack al contenedor (lista sin límite) respetando el apilamiento
        /// de su definición: solo completa pilas existentes si el item es apilable y hasta MaxStack;
        /// el resto va en entradas nuevas. Antes mergeaba cualquier item con el mismo id, así dos
        /// guantes quedaban como "x2" en un slot y al equiparlos se perdía uno.
        /// def null (id desconocido) = se trata como no apilable.
        /// </summary>
        [Server]
        public void ServerDeposit(ItemStack stack, ItemSO def)
        {
            if (stack.IsEmpty) return;

            int remaining = stack.Quantity;
            int maxStack = def != null ? def.MaxStack : 1; // MaxStack ya es 1 si no es apilable

            if (def != null && def.IsStackable)
            {
                for (int i = 0; i < _contents.Count && remaining > 0; i++)
                {
                    ItemStack s = _contents[i];
                    if (s.IsEmpty || s.ItemId != stack.ItemId) continue;
                    int space = maxStack - s.Quantity;
                    if (space <= 0) continue;
                    int add = Mathf.Min(space, remaining);
                    _contents[i] = new ItemStack(s.ItemId, s.Quantity + add, s.Durability);
                    remaining -= add;
                }
            }

            while (remaining > 0)
            {
                int add = Mathf.Min(maxStack, remaining);
                _contents.Add(new ItemStack(stack.ItemId, add, stack.Durability));
                remaining -= add;
            }
        }

        /// <summary>Server-only. Reemplaza o elimina un item por índice (para swap con el inventario). Nunca despawnea.</summary>
        [Server]
        public void ServerUpdateAt(int index, ItemStack stack)
        {
            if (index < 0 || index >= _contents.Count) return;
            if (stack.IsEmpty)
                _contents.RemoveAt(index);
            else
                _contents[index] = stack;
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            _mpb = new MaterialPropertyBlock();
            _contents.OnChange += (op, index, oldItem, newItem, asServer) =>
            {
                OnContentsChanged?.Invoke();
                UpdateVisual();
            };
            UpdateVisual(); // estado inicial correcto para clientes que se unen con el contenedor ya lleno
        }

        private void UpdateVisual()
        {
            if (_renderer == null) return;
            _renderer.GetPropertyBlock(_mpb);
            _mpb.SetColor(_colorPropertyId, IsEmpty ? _emptyColor : _hasLootColor);
            _renderer.SetPropertyBlock(_mpb);
        }

        public void RegisterChangeCallback(System.Action cb) => OnContentsChanged += cb;
        public void UnregisterChangeCallback(System.Action cb) => OnContentsChanged -= cb;
    }
}