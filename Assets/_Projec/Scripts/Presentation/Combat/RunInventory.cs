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
        // Cada tick: mover/equipar/recoger se tiene que ver al instante (por defecto se juntaba cada 0,1 s).
        private readonly SyncList<ItemStack> _pocketL = new SyncList<ItemStack>(new SyncTypeSettings(Game.Presentation.Combat.NetSyncRates.EveryTick));
        private readonly SyncList<ItemStack> _pocketR = new SyncList<ItemStack>(new SyncTypeSettings(Game.Presentation.Combat.NetSyncRates.EveryTick));

        // Equipamiento por slot. Indexado por (int)EquipmentSlot. Vacío = nada equipado.
        private readonly SyncList<ItemStack> _equipment = new SyncList<ItemStack>(new SyncTypeSettings(Game.Presentation.Combat.NetSyncRates.EveryTick));

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

            _loadoutSubmitted = false;
            _ownerPlayFabId = null;
            _runId = null;
            _serverOwnsLoadout = false;
            _resultWritten = false;

            // Con el servidor dueño del loadout, lo lee él de PlayFab (el cliente ya no lo manda).
            if (Game.Presentation.Run.ServerProfileStore.IsActive)
                _ = ServerLoadLoadoutAsync();
        }

        // Server-only: el loadout de este personaje lo guarda el servidor (ver ServerProfileStore).
        private string _ownerPlayFabId;
        private string _runId;
        private bool _serverOwnsLoadout;
        private bool _resultWritten;

        /// <summary>
        /// Server-only. Lee el loadout del dueño desde PlayFab, lo aplica y guarda la marca de run en
        /// curso (el equipo ya está en juego: si el jugador no vuelve, se pierde al morir el cuerpo).
        /// Si no se puede leer, el jugador no juega esta run: se lo saca sin escribir nada, porque
        /// cualquier escritura pisaría su loadout real.
        /// </summary>
        private async System.Threading.Tasks.Task ServerLoadLoadoutAsync()
        {
            // El dueño se asigna en el mismo Spawn; por las dudas, darle un momento.
            for (int i = 0; i < 30 && base.IsSpawned && !base.Owner.IsValid; i++)
                await System.Threading.Tasks.Task.Yield();
            if (!base.IsSpawned) return;

            var owner = base.Owner;
            string key = null, playFabId = null;
            bool known = Game.Presentation.Bootstrap.PlayerIdentityAuthenticator.TryGetPlayerKey(owner, out key)
                         && Game.Presentation.Bootstrap.PlayerIdentityAuthenticator.TryGetPlayFabId(key, out playFabId);

            bool ok = false;
            InventorySnapshot loaded = null;
            if (known)
                (ok, loaded) = await Game.Presentation.Run.ServerProfileStore.LoadLoadoutAsync(playFabId);

            if (!base.IsSpawned) return;

            if (!ok)
            {
                Debug.LogError($"[RunInventory] No se pudo leer el loadout de {(known ? playFabId : "un jugador sin sesión verificada")}: se lo saca de la run sin tocar sus datos.");
                RejectAndRemoveBody(owner, Game.Presentation.Bootstrap.RunOutcome.ProfileUnavailable);
                return;
            }

            // Con el equipo todavía "adentro" de otra run (un cliente que se saltó el panel de
            // reconexión del menú), entrar acá duplicaría ese equipo en dos runs a la vez.
            if (loaded != null && loaded.ActiveRun.Active)
            {
                Debug.LogWarning($"[RunInventory] {playFabId} tiene otra run en curso ({loaded.ActiveRun.RunId}): no puede entrar a esta.");
                RejectAndRemoveBody(owner, Game.Presentation.Bootstrap.RunOutcome.AlreadyInRun);
                return;
            }

            // Murió o extrajo mientras se leía: su loadout nunca entró en juego, no hay nada que guardar.
            if (TryGetComponent(out Game.Presentation.Player.PlayerAvatarState avatar) && avatar.IsControlDisabled)
                return;

            loaded ??= Game.Presentation.Run.PlayerLoadoutService.CreateStartingSnapshot(_startingKit);
            _ownerPlayFabId = playFabId;
            _runId = System.Guid.NewGuid().ToString("N");
            _serverOwnsLoadout = true;
            _loadoutSubmitted = true;

            // Lo que haya juntado mientras se leía el loadout no se pierde: ApplySnapshot vacía todo.
            var pickedUp = new List<ItemStack>();
            foreach (var s in _equipment) if (!s.IsEmpty) pickedUp.Add(s);
            foreach (var s in _pocketL) if (!s.IsEmpty) pickedUp.Add(s);
            foreach (var s in _pocketR) if (!s.IsEmpty) pickedUp.Add(s);

            ApplySnapshot(SanitizeSnapshot(loaded));

            foreach (var s in pickedUp)
            {
                int left = TryAddItem(s.ItemId, s.Quantity);
                if (left > 0) SpawnWorldItem(new ItemStack(s.ItemId, left, s.Durability));
            }

            // Marca de run en curso con lo que trajo (lo juntado acá todavía no es suyo hasta extraer).
            var marked = SanitizeSnapshot(loaded);
            marked.ActiveRun = Game.Presentation.Bootstrap.PlayerIdentityAuthenticator.TryGetEndpoint(key, out string address, out ushort port)
                ? Game.Presentation.Run.PlayerLoadoutService.CreateActiveRun(address, port)
                : Game.Presentation.Run.PlayerLoadoutService.CreateActiveRun(string.Empty, 0);
            marked.ActiveRun.RunId = _runId;
            Game.Presentation.Run.ServerProfileStore.SaveLoadout(_ownerPlayFabId, marked);
            Debug.Log($"[RunInventory] Loadout de {playFabId} leído por el servidor: run {_runId} en curso guardada.");
        }

        /// <summary>Server-only. El jugador no puede jugar esta run: avisarle, sacarlo y quitar el
        /// cuerpo (sin cuerpo no cuenta como vivo ni bloquea el fin de la run).</summary>
        private void RejectAndRemoveBody(FishNet.Connection.NetworkConnection owner, Game.Presentation.Bootstrap.RunOutcome outcome)
        {
            if (owner != null && owner.IsActive)
                Game.Presentation.Bootstrap.PlayerIdentityAuthenticator.RejectWithOutcome(InstanceFinder.NetworkManager, owner, outcome);
            base.Despawn();
        }

        /// <summary>Server-only. Guarda el resultado final de la run (una sola vez por personaje),
        /// solo si el perfil sigue marcado con esta run.</summary>
        private void ServerWriteResult(InventorySnapshot result)
        {
            if (!_serverOwnsLoadout || _resultWritten) return;
            _resultWritten = true;
            Game.Presentation.Run.ServerProfileStore.SaveLoadout(_ownerPlayFabId, result, _runId);
        }

        public override void OnStartClient()
        {
            _pocketL.OnChange += (op, index, oldItem, newItem, asServer) => OnInventoryChanged?.Invoke();
            _pocketR.OnChange += (op, index, oldItem, newItem, asServer) => OnInventoryChanged?.Invoke();
            _equipment.OnChange += (op, index, oldItem, newItem, asServer) => OnInventoryChanged?.Invoke();
            if (base.IsOwner)
            {
                Game.Presentation.UI.RunSummary.BeginRun(_database);
                _ = ClientPushLoadoutAsync();
            }
        }

        /// <summary>Client-only (dueño). Asegura el loadout persistente cargado (PlayFab/local)
        /// y se lo empuja al servidor para que arme el inventario de esta run.</summary>
        private async System.Threading.Tasks.Task ClientPushLoadoutAsync()
        {
            // El servidor lee y guarda el loadout: acá solo se refleja en la cache la run en curso
            // (él ya la guardó en PlayFab), para que el menú ofrezca reconectar si se corta.
            if (Game.Presentation.Run.PlayerLoadoutService.ServerOwnsRun)
            {
                if (Game.Presentation.Bootstrap.RunServerEndpoint.IsSet)
                    Game.Presentation.Run.PlayerLoadoutService.MarkActiveRun(
                        Game.Presentation.Bootstrap.RunServerEndpoint.Address, Game.Presentation.Bootstrap.RunServerEndpoint.Port);
                return;
            }

            if (!await Game.Presentation.Run.PlayerLoadoutService.EnsureInitializedAsync(_startingKit))
            {
                // Sin loadout leído no se puede jugar la run: al extraer/morir se persistiría encima
                // del real. El menú ya lo precarga antes de buscar partida; esto es la red de seguridad.
                Debug.LogError("[RunInventory] No se pudo cargar el loadout persistente; se abandona la run.");
                InstanceFinder.ClientManager.StopConnection();
                return;
            }
            SubmitLoadoutServerRpc(Game.Presentation.Run.PlayerLoadoutService.Current);

            // Desde acá el equipo está en juego. Si el cliente se cae o se cierra, al volver al menú
            // tiene que reconectar o darlo por perdido. En host no aplica: el servidor es este proceso.
            // Se usa la dirección pedida al conectar, no la de Tugboat: su GetPort() con el cliente
            // conectado es el puerto local del socket, y reconectar ahí nunca llega.
            if (!base.IsServerStarted && Game.Presentation.Bootstrap.RunServerEndpoint.IsSet)
                Game.Presentation.Run.PlayerLoadoutService.MarkActiveRun(
                    Game.Presentation.Bootstrap.RunServerEndpoint.Address, Game.Presentation.Bootstrap.RunServerEndpoint.Port);
        }

        [ServerRpc]
        private void SubmitLoadoutServerRpc(Game.Core.Items.InventorySnapshot snapshot)
        {
            // Con el servidor dueño del loadout, lo que mande el cliente no cuenta.
            if (Game.Presentation.Run.ServerProfileStore.IsActive) return;

            // Una sola vez por run, al entrar: si se aceptara en cualquier momento, un cliente podía
            // "restaurar" su equipo a mitad de run o después de morir (duplicar lo que ya soltó).
            if (_loadoutSubmitted)
            {
                Debug.LogWarning($"[RunInventory] Cliente {base.Owner.ClientId} reenvió su loadout; se ignora.");
                return;
            }
            _loadoutSubmitted = true;

            ApplySnapshot(SanitizeSnapshot(snapshot));
        }

        private bool _loadoutSubmitted;

        // Tope de entradas por pocket en un snapshot: la capacidad máxima de cualquier pocket.
        private const int MaxSnapshotPocketEntries = 12;

        /// <summary>
        /// Server-only. Antes de aplicar un loadout (leído de PlayFab o, sin servidor dueño, enviado
        /// por el cliente) se descarta todo lo que no podría existir: ids desconocidos, items de
        /// equipo en un slot que no les corresponde, cantidades fuera de [1, MaxStack], durabilidad
        /// fuera de [0, 1] y listas más largas de lo posible.
        /// </summary>
        private InventorySnapshot SanitizeSnapshot(InventorySnapshot snap)
        {
            var clean = new InventorySnapshot();
            if (snap == null) return clean;

            int dropped = 0;

            if (snap.Equipment != null)
            {
                for (int i = 0; i < snap.Equipment.Count && i < _equipment.Count; i++)
                {
                    ItemStack s = snap.Equipment[i];
                    bool valid = !s.IsEmpty
                                 && _database.GetById(s.ItemId) is EquipmentItemSO equip
                                 && ValidEquipTarget(equip, i);
                    if (!s.IsEmpty && !valid) dropped++;
                    clean.Equipment.Add(valid ? new ItemStack(s.ItemId, 1, Mathf.Clamp01(s.Durability)) : ItemStack.Empty);
                }
                if (snap.Equipment.Count > _equipment.Count) dropped += snap.Equipment.Count - _equipment.Count;
            }

            dropped += SanitizePocket(snap.PocketL, clean.PocketL);
            dropped += SanitizePocket(snap.PocketR, clean.PocketR);

            if (dropped > 0)
                Debug.LogWarning($"[RunInventory] Loadout del cliente {base.Owner.ClientId} con {dropped} entradas inválidas: descartadas.");

            return clean;
        }

        private int SanitizePocket(List<ItemStack> source, List<ItemStack> target)
        {
            if (source == null) return 0;
            int dropped = 0;

            foreach (ItemStack s in source)
            {
                if (s.IsEmpty) continue;
                ItemSO def = _database.GetById(s.ItemId);
                if (def == null || target.Count >= MaxSnapshotPocketEntries)
                {
                    dropped++;
                    continue;
                }

                int qty = Mathf.Clamp(s.Quantity, 1, def.MaxStack);
                if (qty != s.Quantity) dropped++;
                target.Add(new ItemStack(s.ItemId, qty, Mathf.Clamp01(s.Durability)));
            }
            return dropped;
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

            // Primero completar las pilas que ya tenga en CUALQUIER pocket; recién después ocupar
            // slots vacíos (L primero). Antes se llenaba L entero (pilas y huecos) antes de mirar R,
            // y un item apilable abría una pila nueva en L aunque en R hubiera una con lugar.
            int remaining = quantity;
            if (def.IsStackable)
            {
                remaining = StackIntoExisting(_pocketL, def, itemId, remaining);
                remaining = StackIntoExisting(_pocketR, def, itemId, remaining);
            }
            remaining = FillEmptySlots(_pocketL, def, itemId, remaining);
            remaining = FillEmptySlots(_pocketR, def, itemId, remaining);

            return remaining; // lo que no entró (los dos pockets llenos)
        }

        private static int StackIntoExisting(SyncList<ItemStack> list, ItemSO def, string itemId, int remaining)
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
            return remaining;
        }

        private static int FillEmptySlots(SyncList<ItemStack> list, ItemSO def, string itemId, int remaining)
        {
            for (int i = 0; i < list.Count && remaining > 0; i++)
            {
                if (!list[i].IsEmpty) continue;

                int add = def.IsStackable ? Mathf.Min(def.MaxStack, remaining) : 1;
                list[i] = new ItemStack(itemId, add, 1f);
                remaining -= add;
            }

            return remaining;
        }

        /// <summary>
        /// Server-only. Ordena el contenido de los dos pockets juntos (mismas reglas que el Stash,
        /// ver ItemSorting): junta pilas incompletas y llena Pocket L y después Pocket R. Nunca
        /// crea ni pierde items; si por algo no entrara, no toca nada.
        /// </summary>
        [Server]
        public bool TrySortPockets(ItemSortMode mode)
        {
            var all = new List<ItemStack>();
            foreach (var s in _pocketL) if (!s.IsEmpty) all.Add(s);
            foreach (var s in _pocketR) if (!s.IsEmpty) all.Add(s);

            var sorted = ItemSorting.MergeAndSort(all, _database.GetById, mode);
            if (sorted.Count > _pocketL.Count + _pocketR.Count) return false;

            int n = 0;
            for (int i = 0; i < _pocketL.Count; i++) _pocketL[i] = n < sorted.Count ? sorted[n++] : ItemStack.Empty;
            for (int i = 0; i < _pocketR.Count; i++) _pocketR[i] = n < sorted.Count ? sorted[n++] : ItemStack.Empty;
            return true;
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

            int slotIndex = ChooseEquipSlot(equip);

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

        /// <summary>
        /// Slot donde va un item al auto-equiparlo (shift+clic). Pockets: el primer lado vacío; si
        /// los dos están ocupados, reemplaza el de menor capacidad (empate: L). Antes, con los dos
        /// ocupados, el shift+clic no hacía nada.
        /// </summary>
        private int ChooseEquipSlot(EquipmentItemSO equip)
        {
            if (!equip.Slot.IsPocket()) return (int)equip.Slot;

            int lIdx = (int)EquipmentSlot.PocketL;
            int rIdx = (int)EquipmentSlot.PocketR;
            if (_equipment[lIdx].IsEmpty) return lIdx;
            if (_equipment[rIdx].IsEmpty) return rIdx;
            return PocketCapacity(EquipmentSlot.PocketL) <= PocketCapacity(EquipmentSlot.PocketR) ? lIdx : rIdx;
        }

        /// <summary>
        /// Server-only. Equipa directo un item de un LootContainer (shift+clic en la columna del
        /// contenedor), aunque los pockets estén llenos. Lo que estaba equipado va al primer lugar
        /// libre de los pockets o, si no hay, queda en el contenedor en el lugar del item tomado
        /// (un intercambio). El que llama valida alcance y estado del jugador.
        /// </summary>
        [Server]
        public bool TryEquipFromContainer(LootContainer container, int index)
        {
            if (container == null || index < 0 || index >= container.Contents.Count) return false;

            ItemStack stack = container.Contents[index];
            if (stack.IsEmpty) return false;
            if (_database.GetById(stack.ItemId) is not EquipmentItemSO equip) return false;

            int slotIndex = ChooseEquipSlot(equip);
            ItemStack displaced = _equipment[slotIndex];

            // Sacar el item del contenedor: si era un stack de más de 1 (no debería), queda el resto.
            container.ServerUpdateAt(index, stack.Quantity > 1
                ? new ItemStack(stack.ItemId, stack.Quantity - 1, stack.Durability)
                : ItemStack.Empty);

            _equipment[slotIndex] = new ItemStack(stack.ItemId, 1, stack.Durability);

            if (!displaced.IsEmpty)
            {
                // Un pocket reemplazado vuelve al contenedor: meterlo en los pockets justo cuando
                // cambia su capacidad podía dejarlo en un slot que desaparece.
                var (freeZone, freeIndex) = ((EquipmentSlot)slotIndex).IsPocket() ? (-1, -1) : FindFreePocketSlot();
                if (freeZone >= 0) SetSlot(freeZone, freeIndex, displaced);
                else container.ServerDeposit(displaced, _database.GetById(displaced.ItemId));
            }

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
                    container.ServerDeposit(existing, _database.GetById(existing.ItemId)); // si el contenedor se hubiera despawneado antes, esto se perdía

                if (toZone == 0) RebuildAllPocketCapacities();
                return true;
            }
            else
            {
                ItemStack dragged = GetSlot(fromZone, fromIndex);
                if (dragged.IsEmpty) return false;

                SetSlot(fromZone, fromIndex, ItemStack.Empty);
                container.ServerDeposit(dragged, _database.GetById(dragged.ItemId));

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
            ServerWriteResult(snapshot);
            if (base.Owner.IsActive) // desconectado: sin a quién mandarlo (ver reconexión)
                SaveLoadoutTargetRpc(base.Owner, snapshot);
        }

        [TargetRpc]
        private void SaveLoadoutTargetRpc(FishNet.Connection.NetworkConnection conn, Game.Core.Items.InventorySnapshot snapshot)
        {
            Game.Presentation.UI.RunSummary.SetExtracted(snapshot);
            Game.Presentation.Run.PlayerLoadoutService.ApplyRunResult(snapshot);
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

            ServerWriteResult(Game.Presentation.Run.PlayerLoadoutService.CreateEmptySnapshot());
            if (base.Owner.IsActive) // desconectado: el cliente lo resuelve al volver al menú
                ClearLoadoutTargetRpc(base.Owner);
            ClearAll();
        }

        [TargetRpc]
        private void ClearLoadoutTargetRpc(FishNet.Connection.NetworkConnection conn)
        {
            Game.Presentation.Run.PlayerLoadoutService.ApplyRunLost();
        }

        [Server]
        private void ClearAll()
        {
            for (int i = 0; i < _pocketL.Count; i++) _pocketL[i] = ItemStack.Empty;
            for (int i = 0; i < _pocketR.Count; i++) _pocketR[i] = ItemStack.Empty;
            for (int i = 0; i < _equipment.Count; i++) _equipment[i] = ItemStack.Empty;
            RebuildAllPocketCapacities();
        }
    }
}