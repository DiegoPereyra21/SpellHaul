using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using Game.Core.Items;
using Game.Core.Run;
using UnityEngine;
using FishNet;

namespace Game.Presentation.Combat
{
    /// <summary>
    /// Inventario de run server-authoritative. Dos pockets independientes (L/R) + equipamiento
    /// (por slot), todos sincronizados por SyncList. Implementa IRunInventory (extracción salva,
    /// muerte pierde).
    /// Zonas: 0 = Equipment, 1 = Pocket L, 2 = Pocket R, 3 = Container (LootContainer externo).
    /// </summary>
    public class RunInventory : NetworkBehaviour, IRunInventory
    {
        [SerializeField] private ItemDatabase _database;
        [SerializeField] private GameObject _lootContainerPrefab; // asignar en el Inspector
        [SerializeField] private Game.Core.Items.StartingKitSO _startingKit;
        [SerializeField] private GameObject _worldItemPrefab; // prefab con WorldItem + NetworkObject

        // Pockets: dos listas independientes. Vacíos representan slots libres.
        private readonly SyncList<ItemStack> _pocketL = new SyncList<ItemStack>();
        private readonly SyncList<ItemStack> _pocketR = new SyncList<ItemStack>();

        // Equipamiento por slot. Indexado por (int)EquipmentSlot. Vacío = nada equipado.
        private readonly SyncList<ItemStack> _equipment = new SyncList<ItemStack>();

        public IReadOnlyList<ItemStack> PocketL => _pocketL;
        public IReadOnlyList<ItemStack> PocketR => _pocketR;
        public IReadOnlyList<ItemStack> Equipment => _equipment;

        public event System.Action OnInventoryChanged;

        public override void OnStartServer()
        {
            // Inicializar los slots de equipamiento (uno por cada EquipmentSlot, vacío).
            int slotCount = System.Enum.GetValues(typeof(EquipmentSlot)).Length;
            for (int i = 0; i < slotCount; i++)
                _equipment.Add(ItemStack.Empty);

            RebuildAllPocketCapacities();
        }

        public override void OnStartClient()
        {
            _pocketL.OnChange += (op, index, oldItem, newItem, asServer) => OnInventoryChanged?.Invoke();
            _pocketR.OnChange += (op, index, oldItem, newItem, asServer) => OnInventoryChanged?.Invoke();
            _equipment.OnChange += (op, index, oldItem, newItem, asServer) => OnInventoryChanged?.Invoke();
            if (base.IsOwner)
                _ = ClientPushLoadoutAsync();
        }

        /// <summary>Client-only (dueño). Asegura el loadout persistente cargado (PlayFab/local)
        /// y se lo empuja al servidor para que arme el inventario de esta run.</summary>
        private async System.Threading.Tasks.Task ClientPushLoadoutAsync()
        {
            if (!await Game.Presentation.Run.PlayerLoadoutService.EnsureInitializedAsync(_startingKit))
            {
                // Sin loadout leído no se puede jugar la run: al extraer/morir se persistiría encima
                // del real. El menú ya lo precarga antes de buscar partida; esto es la red de seguridad.
                Debug.LogError("[RunInventory] No se pudo cargar el loadout persistente; se abandona la run.");
                InstanceFinder.ClientManager.StopConnection();
                return;
            }
            SubmitLoadoutServerRpc(Game.Presentation.Run.PlayerLoadoutService.Current);
        }

        [ServerRpc]
        private void SubmitLoadoutServerRpc(Game.Core.Items.InventorySnapshot snapshot)
        {
            ApplySnapshot(snapshot);
        }

        // ---------- Capacidad de pockets ----------

        /// <summary>Capacidad de un pocket: 1 por defecto sin nada equipado, exacta a lo que declare el ítem si hay algo puesto.</summary>
        private int PocketCapacity(EquipmentSlot pocketSlot)
        {
            ItemStack eq = _equipment[(int)pocketSlot];
            if (!eq.IsEmpty && _database.GetById(eq.ItemId) is EquipmentItemSO e && e.Slot.IsPocket())
                return Mathf.Max(e.PocketSlots, 0);
            return 1;
        }

        private void RebuildPocketCapacity(SyncList<ItemStack> list, int cap)
        {
            while (list.Count < cap) list.Add(ItemStack.Empty);

            // Si la capacidad bajó, rescatar los items de los slots que desaparecen.
            while (list.Count > cap)
            {
                int last = list.Count - 1;
                ItemStack orphan = list[last];
                list.RemoveAt(last);

                if (orphan.IsEmpty) continue;

                bool rescued = false;
                for (int i = 0; i < list.Count; i++)
                {
                    if (!list[i].IsEmpty) continue;
                    list[i] = orphan;
                    rescued = true;
                    break;
                }

                if (!rescued)
                    SpawnWorldItem(orphan);
            }
        }

        private void RebuildAllPocketCapacities()
        {
            RebuildPocketCapacity(_pocketL, PocketCapacity(EquipmentSlot.PocketL));
            RebuildPocketCapacity(_pocketR, PocketCapacity(EquipmentSlot.PocketR));
        }

        [Server]
        private void SpawnWorldItem(ItemStack stack)
        {
            SpawnWorldItemPublic(stack, transform.position);
        }

        /// <summary>Server-only. Dropea un item del inventario al mundo.</summary>
        [Server]
        public bool TryDropToWorld(int zone, int index, Vector3 nearPosition)
        {
            ItemStack stack = GetSlot(zone, index);
            if (stack.IsEmpty) return false;

            SetSlot(zone, index, ItemStack.Empty);

            // Si era un pocket equipado, RebuildAllPocketCapacities rescata/dropea el contenido.
            if (zone == 0 && ((EquipmentSlot)index).IsPocket())
                RebuildAllPocketCapacities();

            SpawnWorldItemPublic(stack, nearPosition);
            return true;
        }

        /// <summary>Server-only. Equipa un pocket que viene del mundo en la posición indicada.
        /// Quién llama decide el target (PlayerInteraction ya calcula cuál conviene reemplazar).</summary>
        [Server]
        public void EquipPocketFromWorld(ItemStack stack, EquipmentSlot targetSlot)
        {
            if (!targetSlot.IsPocket()) return;
            _equipment[(int)targetSlot] = new ItemStack(stack.ItemId, 1, stack.Durability);
            RebuildAllPocketCapacities();
        }

        /// <summary>Server-only. Punto de entrada público para spawnear un WorldItem cerca de una posición.</summary>
        [Server]
        public void SpawnWorldItemPublic(ItemStack stack, Vector3 nearPosition)
        {
            if (_worldItemPrefab == null) return;

            Vector3 pos = nearPosition + Vector3.up * 0.5f;
            pos += new Vector3(Random.Range(-0.3f, 0.3f), 0f, Random.Range(-0.3f, 0.3f));

            GameObject obj = Instantiate(_worldItemPrefab, pos, Random.rotation);
            if (obj.TryGetComponent(out WorldItem worldItem))
            {
                InstanceFinder.ServerManager.Spawn(obj);
                worldItem.ServerSetItem(stack);
            }
        }

        // ---------- Agregar item (con stacking) ----------

        /// <summary>Server-only. Intenta agregar cantidad de un item, primero en Pocket L y luego en Pocket R. Devuelve lo que NO entró.</summary>
        [Server]
        public int TryAddItem(string itemId, int quantity)
        {
            ItemSO def = _database.GetById(itemId);
            if (def == null || quantity <= 0) return quantity;

            int remaining = quantity;
            remaining = TryAddItemToList(_pocketL, def, itemId, remaining);
            if (remaining > 0)
                remaining = TryAddItemToList(_pocketR, def, itemId, remaining);

            return remaining; // lo que no entró (los dos pockets llenos)
        }

        private int TryAddItemToList(SyncList<ItemStack> list, ItemSO def, string itemId, int remaining)
        {
            // 1. Si es apilable, rellenar pilas existentes del mismo item.
            if (def.IsStackable)
            {
                for (int i = 0; i < list.Count && remaining > 0; i++)
                {
                    ItemStack s = list[i];
                    if (s.IsEmpty || s.ItemId != itemId) continue;

                    int space = def.MaxStack - s.Quantity;
                    if (space <= 0) continue;

                    int add = Mathf.Min(space, remaining);
                    s.Quantity += add;
                    list[i] = s;
                    remaining -= add;
                }
            }

            // 2. Ocupar slots vacíos con nuevas pilas.
            for (int i = 0; i < list.Count && remaining > 0; i++)
            {
                if (!list[i].IsEmpty) continue;

                int add = def.IsStackable ? Mathf.Min(def.MaxStack, remaining) : 1;
                list[i] = new ItemStack(itemId, add, 1f);
                remaining -= add;
            }

            return remaining;
        }

        /// <summary>Server-only. Crea un snapshot del inventario actual (para persistir al extraer).</summary>
        [Server]
        public Game.Core.Items.InventorySnapshot TakeSnapshot()
        {
            var snap = new Game.Core.Items.InventorySnapshot();
            foreach (var s in _equipment) snap.Equipment.Add(s);   // incluye vacíos: preserva los slots
            foreach (var s in _pocketL) if (!s.IsEmpty) snap.PocketL.Add(s);
            foreach (var s in _pocketR) if (!s.IsEmpty) snap.PocketR.Add(s);
            return snap;
        }

        /// <summary>Server-only. Restaura el inventario desde un snapshot (inventario propio persistente).</summary>
        [Server]
        public void ApplySnapshot(Game.Core.Items.InventorySnapshot snap)
        {
            ClearAll();

            if (snap == null) return;

            // Restaurar equipamiento por slot (el snapshot guarda un stack por cada slot, en orden).
            for (int i = 0; i < snap.Equipment.Count && i < _equipment.Count; i++)
                _equipment[i] = snap.Equipment[i];

            // Recalcular capacidad de ambos pockets según lo equipado del snapshot.
            RebuildAllPocketCapacities();

            var overflow = new List<ItemStack>();
            RestoreIntoList(_pocketL, snap.PocketL, overflow);
            RestoreIntoList(_pocketR, snap.PocketR, overflow);

            // Lo que no entró en su pocket prueba en el otro; si tampoco hay lugar, cae al suelo al
            // lado del jugador (antes se descartaba en silencio y la pérdida se persistía al extraer).
            foreach (var stack in overflow)
                if (!TryPlaceInFirstEmpty(_pocketL, stack) && !TryPlaceInFirstEmpty(_pocketR, stack))
                    SpawnWorldItem(stack);
        }

        private static void RestoreIntoList(SyncList<ItemStack> list, List<ItemStack> source, List<ItemStack> overflow)
        {
            foreach (var stack in source)
            {
                if (stack.IsEmpty) continue;
                if (!TryPlaceInFirstEmpty(list, stack))
                    overflow.Add(stack);
            }
        }

        private static bool TryPlaceInFirstEmpty(SyncList<ItemStack> list, ItemStack stack)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (!list[i].IsEmpty) continue;
                list[i] = stack;
                return true;
            }
            return false;
        }

        // ---------- Equipar / desequipar ----------

        /// <summary>Server-only. Equipa un item desde un slot de un pocket (zone 1 o 2).</summary>
        [Server]
        public bool TryEquipFromInventory(int zone, int index)
        {
            if (zone != 1 && zone != 2) return false;
            SyncList<ItemStack> list = zone == 1 ? _pocketL : _pocketR;
            if (index < 0 || index >= list.Count) return false;

            ItemStack stack = list[index];
            if (stack.IsEmpty) return false;
            if (_database.GetById(stack.ItemId) is not EquipmentItemSO equip) return false;

            int slotIndex;
            if (equip.Slot.IsPocket())
            {
                int lIdx = (int)EquipmentSlot.PocketL;
                int rIdx = (int)EquipmentSlot.PocketR;
                if (_equipment[lIdx].IsEmpty) slotIndex = lIdx;
                else if (_equipment[rIdx].IsEmpty) slotIndex = rIdx;
                else return false; // las dos posiciones de pocket ya ocupadas
            }
            else
            {
                slotIndex = (int)equip.Slot;
            }

            if (!_equipment[slotIndex].IsEmpty)
            {
                ItemStack current = _equipment[slotIndex];
                list[index] = current; // swap
            }
            else
            {
                list[index] = ItemStack.Empty;
            }

            _equipment[slotIndex] = new ItemStack(stack.ItemId, 1, stack.Durability);

            if (((EquipmentSlot)slotIndex).IsPocket())
                RebuildAllPocketCapacities();

            return true;
        }

        /// <summary>Server-only. Desequipa el slot de equipo dado, mandando el item al primer pocket con espacio (L primero).</summary>
        [Server]
        public bool TryUnequip(int equipmentSlotIndex)
        {
            if (equipmentSlotIndex < 0 || equipmentSlotIndex >= _equipment.Count) return false;
            ItemStack equipped = _equipment[equipmentSlotIndex];
            if (equipped.IsEmpty) return false;

            int freeZone = -1, freeIndex = -1;
            for (int i = 0; i < _pocketL.Count; i++)
                if (_pocketL[i].IsEmpty) { freeZone = 1; freeIndex = i; break; }
            if (freeZone < 0)
                for (int i = 0; i < _pocketR.Count; i++)
                    if (_pocketR[i].IsEmpty) { freeZone = 2; freeIndex = i; break; }

            if (freeZone < 0) return false; // los dos pockets llenos, no se puede desequipar

            SetSlot(freeZone, freeIndex, equipped);
            _equipment[equipmentSlotIndex] = ItemStack.Empty;

            if (((EquipmentSlot)equipmentSlotIndex).IsPocket())
                RebuildAllPocketCapacities();

            return true;
        }

        /// <summary>Server-only. Agrega un stack ya formado al inventario (para saqueo de contenedores).</summary>
        [Server]
        public int TryAddStack(ItemStack stack)
        {
            return TryAddItem(stack.ItemId, stack.Quantity);
        }

        /// <summary>Valida si un ítem de equipo puede ir al índice de slot dado (pocket-aware: L/R son intercambiables).</summary>
        private bool ValidEquipTarget(EquipmentItemSO equip, int toIndex)
        {
            if (equip.Slot.IsPocket())
                return toIndex == (int)EquipmentSlot.PocketL || toIndex == (int)EquipmentSlot.PocketR;
            return (int)equip.Slot == toIndex;
        }

        /// <summary>Busca el primer slot libre entre los dos pockets. Devuelve (-1,-1) si no hay.</summary>
        private (int zone, int index) FindFreePocketSlot()
        {
            for (int i = 0; i < _pocketL.Count; i++)
                if (_pocketL[i].IsEmpty) return (1, i);
            for (int i = 0; i < _pocketR.Count; i++)
                if (_pocketR[i].IsEmpty) return (2, i);
            return (-1, -1);
        }

        /// <summary>Server-only. Mueve un item entre dos slots cualesquiera del inventario propio.
        /// Soporta reorden de pockets, equipar a slot exacto, desequipar, y merge de apilables.</summary>
        [Server]
        public bool TryMoveSlot(int fromZone, int fromIndex, int toZone, int toIndex)
        {
            // Origen y destino tienen que existir y ser distintos: soltar un stack sobre su propio
            // slot entraba al merge y lo borraba; un destino inválido vaciaba el origen sin
            // escribir en ningún lado.
            if (!IsValidSlot(fromZone, fromIndex) || !IsValidSlot(toZone, toIndex)) return false;
            if (fromZone == toZone && fromIndex == toIndex) return false;

            ItemStack from = GetSlot(fromZone, fromIndex);
            if (from.IsEmpty) return false;

            // Validar destino de equipo: el item debe corresponder al slot (pocket-aware).
            if (toZone == 0) // Equipment
            {
                if (_database.GetById(from.ItemId) is not EquipmentItemSO equip) return false;
                if (!ValidEquipTarget(equip, toIndex)) return false;
            }

            ItemStack to = GetSlot(toZone, toIndex);

            // Merge: mismo item apilable, destino no vacío.
            if (!to.IsEmpty && to.ItemId == from.ItemId)
            {
                ItemSO def = _database.GetById(from.ItemId);
                if (def != null && def.IsStackable)
                {
                    int space = def.MaxStack - to.Quantity;
                    if (space <= 0) return false;
                    int move = Mathf.Min(space, from.Quantity);
                    SetSlot(toZone, toIndex, new ItemStack(to.ItemId, to.Quantity + move, to.Durability));
                    int remaining = from.Quantity - move;
                    SetSlot(fromZone, fromIndex, remaining > 0
                        ? new ItemStack(from.ItemId, remaining, from.Durability)
                        : ItemStack.Empty);

                    if (toZone == 0 || fromZone == 0) RebuildAllPocketCapacities();
                    return true;
                }
            }

            // Swap normal.
            // Si el origen es equipo y el destino es un pocket, validar que el item
            // del destino (si hay) pueda ir al slot de equipo origen.
            if (fromZone == 0 && (toZone == 1 || toZone == 2) && !to.IsEmpty)
            {
                bool destinationFitsBack = _database.GetById(to.ItemId) is EquipmentItemSO toEquip
                                             && ValidEquipTarget(toEquip, fromIndex);

                if (!destinationFitsBack)
                {
                    // No puede ir al slot de equipo: mover el equipo al primer slot libre entre los pockets.
                    var (freeZone, freeIndex) = FindFreePocketSlot();

                    if (freeZone >= 0)
                    {
                        SetSlot(freeZone, freeIndex, from);
                        SetSlot(0, fromIndex, ItemStack.Empty);
                    }
                    else
                    {
                        // No hay slot libre: mover igualmente al slot destino (pisa el item, que se dropea).
                        SpawnWorldItemPublic(to, transform.position);
                        SetSlot(toZone, toIndex, from);
                        SetSlot(0, fromIndex, ItemStack.Empty);
                    }

                    RebuildAllPocketCapacities();
                    return true;
                }
            }

            SetSlot(toZone, toIndex, from);
            SetSlot(fromZone, fromIndex, to.IsEmpty ? ItemStack.Empty : to);

            if (toZone == 0 || fromZone == 0) RebuildAllPocketCapacities();
            return true;
        }

        /// <summary>Server-only. Mueve entre el inventario propio y un LootContainer externo (zone 3).</summary>
        [Server]
        public bool TryMoveWithContainer(int fromZone, int fromIndex, int toZone, int toIndex, LootContainer container)
        {
            // fromZone/toZone: 0=Equipment, 1=Pocket L, 2=Pocket R, 3=Container
            bool fromContainer = fromZone == 3;
            bool toContainer   = toZone   == 3;

            if (fromContainer && toContainer) return false; // container→container no aplica
            if (!fromContainer && !toContainer) return false; // ambos internos: usar TryMoveSlot

            // El lado propio tiene que ser un slot real: si no, el item salía de un lado y no
            // entraba en el otro (se perdía).
            if (fromContainer ? !IsValidSlot(toZone, toIndex) : !IsValidSlot(fromZone, fromIndex)) return false;

            if (fromContainer)
            {
                if (fromIndex < 0 || fromIndex >= container.Contents.Count) return false;
                ItemStack dragged = container.Contents[fromIndex];
                if (dragged.IsEmpty) return false;

                ItemStack existing = GetSlot(toZone, toIndex);

                if (toZone == 0)
                {
                    if (_database.GetById(dragged.ItemId) is not EquipmentItemSO equip) return false;
                    if (!ValidEquipTarget(equip, toIndex)) return false;
                }

                if (!existing.IsEmpty && existing.ItemId == dragged.ItemId)
                {
                    ItemSO def = _database.GetById(dragged.ItemId);
                    if (def != null && def.IsStackable)
                    {
                        int space = def.MaxStack - existing.Quantity;
                        if (space <= 0) return false;
                        int move = Mathf.Min(space, dragged.Quantity);
                        SetSlot(toZone, toIndex, new ItemStack(existing.ItemId, existing.Quantity + move, existing.Durability));
                        int remaining = dragged.Quantity - move;
                        container.ServerUpdateAt(fromIndex, remaining > 0
                            ? new ItemStack(dragged.ItemId, remaining, dragged.Durability)
                            : ItemStack.Empty);
                        return true;
                    }
                }

                // Swap: sacar del contenedor, poner en slot, depositar lo que había
                container.ServerUpdateAt(fromIndex, ItemStack.Empty);
                SetSlot(toZone, toIndex, dragged);
                if (!existing.IsEmpty)
                    container.ServerDeposit(existing); // si el contenedor se hubiera despawneado antes, esto se perdía

                if (toZone == 0) RebuildAllPocketCapacities();
                return true;
            }
            else
            {
                ItemStack dragged = GetSlot(fromZone, fromIndex);
                if (dragged.IsEmpty) return false;

                SetSlot(fromZone, fromIndex, ItemStack.Empty);
                container.ServerDeposit(dragged);

                if (fromZone == 0) RebuildAllPocketCapacities();
                return true;
            }
        }

        /// <summary>True si (zone, index) es un slot existente del inventario propio (0 = equipo, 1/2 = pockets).</summary>
        private bool IsValidSlot(int zone, int index)
        {
            return zone switch
            {
                0 => index >= 0 && index < _equipment.Count,
                1 => index >= 0 && index < _pocketL.Count,
                2 => index >= 0 && index < _pocketR.Count,
                _ => false
            };
        }

        private ItemStack GetSlot(int zone, int index)
        {
            return zone switch
            {
                0 => (index >= 0 && index < _equipment.Count) ? _equipment[index] : ItemStack.Empty,
                1 => (index >= 0 && index < _pocketL.Count) ? _pocketL[index] : ItemStack.Empty,
                2 => (index >= 0 && index < _pocketR.Count) ? _pocketR[index] : ItemStack.Empty,
                _ => ItemStack.Empty
            };
        }

        private void SetSlot(int zone, int index, ItemStack stack)
        {
            switch (zone)
            {
                case 0: if (index >= 0 && index < _equipment.Count) _equipment[index] = stack; break;
                case 1: if (index >= 0 && index < _pocketL.Count) _pocketL[index] = stack; break;
                case 2: if (index >= 0 && index < _pocketR.Count) _pocketR[index] = stack; break;
            }
        }

        // ---------- IRunInventory ----------

        [Server]
        public void CommitToStash()
        {
            var snapshot = TakeSnapshot();
            SaveLoadoutTargetRpc(base.Owner, snapshot);
        }

        [TargetRpc]
        private void SaveLoadoutTargetRpc(FishNet.Connection.NetworkConnection conn, Game.Core.Items.InventorySnapshot snapshot)
        {
            Game.Presentation.Run.PlayerLoadoutService.Save(snapshot);
        }

        [Server]
        public void DropAll()
        {
            var loot = new List<ItemStack>();
            foreach (var s in _pocketL) if (!s.IsEmpty) loot.Add(s);
            foreach (var s in _pocketR) if (!s.IsEmpty) loot.Add(s);
            foreach (var s in _equipment) if (!s.IsEmpty) loot.Add(s);

            if (loot.Count > 0 && _lootContainerPrefab != null)
            {
                Vector3 pos = transform.position;
                if (Physics.Raycast(transform.position, Vector3.down, out RaycastHit groundHit, 200f))
                    pos = groundHit.point + Vector3.up * 0.1f;

                GameObject obj = Instantiate(_lootContainerPrefab, pos, Quaternion.identity);

                if (obj.TryGetComponent(out LootContainer container))
                {
                    InstanceFinder.ServerManager.Spawn(obj);
                    container.ServerFill(loot);
                }
            }

            ClearLoadoutTargetRpc(base.Owner);
            ClearAll();
        }


        [TargetRpc]
        private void ClearLoadoutTargetRpc(FishNet.Connection.NetworkConnection conn)
        {
            Game.Presentation.Run.PlayerLoadoutService.Clear();
        }

        [Server]
        private void ClearAll()
        {
            for (int i = 0; i < _pocketL.Count; i++) _pocketL[i] = ItemStack.Empty;
            for (int i = 0; i < _pocketR.Count; i++) _pocketR[i] = ItemStack.Empty;
            for (int i = 0; i < _equipment.Count; i++) _equipment[i] = ItemStack.Empty;
            RebuildAllPocketCapacities();
        }

        // ---------- Debug helper (para InventoryDebugger) ----------

        public void DebugLogContents()
        {
            Debug.Log("--- Inventario ---");
            for (int i = 0; i < _equipment.Count; i++)
                if (!_equipment[i].IsEmpty)
                    Debug.Log($"  [Equip {(EquipmentSlot)i}] {_equipment[i].ItemId} (dur {_equipment[i].Durability:0.00})");
            for (int i = 0; i < _pocketL.Count; i++)
                if (!_pocketL[i].IsEmpty)
                    Debug.Log($"  [Pocket L {i}] {_pocketL[i].ItemId} x{_pocketL[i].Quantity}");
            for (int i = 0; i < _pocketR.Count; i++)
                if (!_pocketR[i].IsEmpty)
                    Debug.Log($"  [Pocket R {i}] {_pocketR[i].ItemId} x{_pocketR[i].Quantity}");
        }
    }
}