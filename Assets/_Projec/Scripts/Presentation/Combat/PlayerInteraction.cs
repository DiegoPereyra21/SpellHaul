using FishNet.Object;
using Game.Core.Items;
using Game.Presentation.Player;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Presentation.Combat
{
    /// <summary>
    /// Detecta con qué WorldItem está apuntando el jugador (raycast desde la cámara), muestra
    /// el prompt y, con la tecla, pide recogerlo. La recogida real es server-authoritative.
    /// </summary>
    public class PlayerInteraction : NetworkBehaviour
    {
        [SerializeField] private Transform _aimOrigin;      // la cámara
        [SerializeField] private float _range = 3.5f;
        [SerializeField] private LayerMask _interactMask;   // capa de los WorldItem
        [SerializeField] private RunInventory _inventory;
        [SerializeField] private ItemDatabase _database;
        [SerializeField] private Game.Presentation.UI.InventoryUIController _inventoryUI;
        [Tooltip("Margen extra (m) sobre el rango al validar en el servidor: cubre lo que el jugador se movió durante la latencia.")]
        [SerializeField] private float _serverRangeTolerance = 1.5f;
        private PlayerAvatarState _avatar;
        private LootContainer _currentContainer;
        private InputAction _interactAction;
        private PlayerControls _controls;
        private WorldItem _current;   // a qué estamos apuntando ahora

        /// <summary>El HUD lee esto para el prompt. Null = no apuntando a nada recogible.</summary>
        public string CurrentPrompt { get; private set; }

        private bool _inputBlocked;
        public void SetInputBlocked(bool blocked) => _inputBlocked = blocked;

        private void Awake()
        {
            _controls = new PlayerControls();
            _interactAction = _controls.Player.Interact;
            _avatar = GetComponent<PlayerAvatarState>();
        }

        /// <summary>
        /// Server-only. True si este jugador sigue en la run y el punto está a su alcance, medido
        /// desde la posición del servidor (nunca la que diga el cliente). Lo usan la recogida y los
        /// contenedores: sin esto se podía lootear cualquier cosa del mapa, o hacerlo ya extraído.
        /// </summary>
        public bool ServerCanReach(Vector3 point)
        {
            if (_avatar != null && _avatar.IsControlDisabled) return false;

            Vector3 origin = _aimOrigin != null ? _aimOrigin.position : transform.position;
            return Vector3.Distance(origin, point) <= _range + _serverRangeTolerance;
        }

        private void OnDestroy()
        {
            _controls?.Dispose();
        }

        private void OnEnable() => _interactAction.Enable();
        private void OnDisable() => _interactAction.Disable();

        private void Update()
        {
            if (!base.IsOwner) return;
            if (_inputBlocked) { CurrentPrompt = null; return; }

            DetectTarget();

            if (_interactAction.WasPressedThisFrame())
            {
                if (_current != null)
                {
                    Game.Presentation.Audio.GameAudio.Ui(l => l.ItemPickup);
                    PickupServerRpc(_current);
                }
                else if (_currentContainer != null && _inventoryUI != null)
                    _inventoryUI.OpenWithContainer(_currentContainer);
            }
        }

        private void DetectTarget()
        {
            _current = null;
            _currentContainer = null;
            CurrentPrompt = null;

            if (_aimOrigin == null) return;

            if (Physics.Raycast(_aimOrigin.position, _aimOrigin.forward, out RaycastHit hit, _range, _interactMask))
            {
                // ¿Es un item suelto?
                WorldItem item = hit.collider.GetComponentInParent<WorldItem>();
                if (item != null)
                {
                    _current = item;
                    CurrentPrompt = $"E  Pick up {item.GetDisplayName(_database)} x{item.Quantity}";
                    return;
                }

                // ¿Es un contenedor (cadáver)?
                LootContainer container = hit.collider.GetComponentInParent<LootContainer>();
                if (container != null && !container.IsEmpty)
                {
                    _currentContainer = container;
                    CurrentPrompt = "E  Loot";
                    return;
                }
            }
        }

        [ServerRpc]
        private void PickupServerRpc(WorldItem item)
        {
            if (item == null || !item.IsSpawned) return;
            if (!ServerCanReach(item.transform.position)) return;

            ItemStack stack = item.ToStack();
            ItemSO def = _database.GetById(stack.ItemId);

            // ¿Es un pocket? Intentar auto-equip si mejora alguna de las dos posiciones.
            if (def is EquipmentItemSO equip && equip.Slot.IsPocket())
            {
                EquipmentSlot lSlot = EquipmentSlot.PocketL;
                EquipmentSlot rSlot = EquipmentSlot.PocketR;
                ItemStack currentL = _inventory.Equipment[(int)lSlot];
                ItemStack currentR = _inventory.Equipment[(int)rSlot];

                int capL = (!currentL.IsEmpty && _database.GetById(currentL.ItemId) is EquipmentItemSO eL) ? eL.PocketSlots : 0;
                int capR = (!currentR.IsEmpty && _database.GetById(currentR.ItemId) is EquipmentItemSO eR) ? eR.PocketSlots : 0;

                // Reemplazar la posición más floja de las dos (empate → preferimos L).
                bool replaceL = capL <= capR;
                EquipmentSlot targetSlot = replaceL ? lSlot : rSlot;
                int worseCap = replaceL ? capL : capR;
                ItemStack displaced = replaceL ? currentL : currentR;

                if (equip.PocketSlots > worseCap)
                {
                    _inventory.EquipPocketFromWorld(stack, targetSlot);

                    if (!displaced.IsEmpty)
                    {
                        int leftover = _inventory.TryAddStack(displaced);
                        if (leftover > 0)
                            SpawnWorldItemNear(new ItemStack(displaced.ItemId, leftover, displaced.Durability));
                    }

                    item.Despawn();
                    return;
                }
                // Si no mejora ninguna de las dos posiciones, cae al flujo normal.
            }

            // Flujo normal: intentar agregar a los pockets.
            int notAdded = _inventory.TryAddItem(stack.ItemId, stack.Quantity);

            if (notAdded <= 0)
                item.Despawn();
            else if (notAdded < stack.Quantity)
                item.ServerSetItem(new ItemStack(stack.ItemId, notAdded, stack.Durability));
        }

        private void SpawnWorldItemNear(ItemStack stack)
        {
            // Reutiliza el mismo prefab y lógica que RunInventory, pero desde PlayerInteraction.
            // Delegamos al RunInventory para no duplicar la lógica de spawn.
            _inventory.SpawnWorldItemPublic(stack, transform.position);
        }
    }
}