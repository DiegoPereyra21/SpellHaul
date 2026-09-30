using Game.Core.Items;
using Game.Presentation.Run;
using UnityEngine;
using UnityEngine.UIElements;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Game.Presentation.UI
{
    /// <summary>
    /// Pantalla de gestión en el menú: inventario propio (equipo + dos pockets) y stash lado a
    /// lado. Trabaja sobre datos planos (snapshot del PlayerLoadoutService + StashData del
    /// StashService), sin red. Clic mueve items; shift+clic equipa; arrastrar mueve entre slots.
    /// Cada pocket siempre muestra 12 casilleros; los que superan la capacidad actual del pocket
    /// equipado quedan bloqueados. Si cambiar/sacar un pocket dejaría items sin espacio, el
    /// sobrante se manda al Stash; si el Stash tampoco tiene lugar, el cambio entero se cancela.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class StashScreenController : MonoBehaviour
    {
        [SerializeField] private ItemDatabase _database;
        [SerializeField] private StartingKitSO _startingKit;

        private const int MaxPocketSlots = 12;
        private const int DefaultPocketCapacity = 1;

        private UIDocument _document;
        private VisualElement _root;
        private VisualElement _equipmentSlots;
        private VisualElement _pocketLGrid;
        private VisualElement _pocketRGrid;
        private Label _pocketLLabel;
        private Label _pocketRLabel;
        private VisualElement _stashGrid;
        private Label _stashLabel;
        private VisualElement _tooltip;
        private Label _tooltipTitle;
        private Label _tooltipType;
        private Label _tooltipDescription;
        private VisualElement _tooltipStats;
        private float _pendingTooltipAnchorBottom;

        private enum SlotZone { Equipment, PocketL, PocketR, Stash }

        // Mientras se busca partida el equipo (loadout) no se puede tocar: el servidor de la run lo
        // va a leer así como está. El stash sí se puede ordenar.
        private VisualElement _loadoutColumn;
        private Label _loadoutLockNote;

        /// <summary>True mientras el loadout está bloqueado (buscando partida).</summary>
        public bool LoadoutLocked { get; private set; }

        public bool IsVisible => _root != null && _root.style.display == DisplayStyle.Flex;

        /// <summary>Se abrió / se cerró la pantalla (el menú acomoda su banner de búsqueda encima).</summary>
        public event System.Action Shown;
        public event System.Action Hidden;

        /// <summary>Bloquea o libera el loadout (equipo y pockets): queda atenuado y no acepta cambios.</summary>
        public void SetLoadoutLocked(bool locked)
        {
            LoadoutLocked = locked;
            // Buscando partida el banner de búsqueda del menú va arriba: el panel baja para dejarle lugar.
            _root?.EnableInClassList("searching", locked);
            if (_loadoutColumn != null) _loadoutColumn.EnableInClassList("loadout-locked", locked);
            if (_loadoutLockNote != null) _loadoutLockNote.style.display = locked ? DisplayStyle.Flex : DisplayStyle.None;
            if (locked && _isDragging && _dragging.Zone != SlotZone.Stash) CancelDrag();
        }

        private struct DragInfo
        {
            public SlotZone Zone;
            public int Index;
            public ItemStack Stack;
        }

        private DragInfo _dragging;
        private bool _isDragging;
        private VisualElement _ghost;
        private bool _dragMoved;

        private void OnEnable()
        {
            _document = GetComponent<UIDocument>();
            _root = _document.rootVisualElement.Q<VisualElement>("stash-root");
            _equipmentSlots = _root.Q<VisualElement>("equipment-slots");
            _pocketLGrid = _root.Q<VisualElement>("pocket-l-grid");
            _pocketRGrid = _root.Q<VisualElement>("pocket-r-grid");
            _pocketLLabel = _root.Q<Label>("pocket-l-label");
            _pocketRLabel = _root.Q<Label>("pocket-r-label");
            _stashGrid = _root.Q<VisualElement>("stash-grid");
            _stashLabel = _root.Q<Label>("stash-label");
            _loadoutColumn = _root.Q<VisualElement>("loadout-column");
            _loadoutLockNote = _root.Q<Label>("loadout-lock-note");
            SetLoadoutLocked(LoadoutLocked);
            _tooltip = _root.Q<VisualElement>("item-tooltip");
            _tooltipTitle = _root.Q<Label>("tooltip-title");
            _tooltipType = _root.Q<Label>("tooltip-type");
            _tooltipDescription = _root.Q<Label>("tooltip-description");
            _tooltipStats = _root.Q<VisualElement>("tooltip-stats");

            var closeBtn = _root.Q<Button>("stash-close");
            if (closeBtn != null) closeBtn.clicked += Hide;

            var sortType = _root.Q<Button>("sort-type");
            if (sortType != null) sortType.clicked += () => SortStash(byRarity: false);
            var sortRarity = _root.Q<Button>("sort-rarity");
            if (sortRarity != null) sortRarity.clicked += () => SortStash(byRarity: true);

            _root.RegisterCallback<PointerUpEvent>(_ => { if (_isDragging) CancelDrag(); });

            ProfileSaveQueue.OnProfileReloaded += HandleProfileReloaded;
        }

        private void OnDisable()
        {
            ProfileSaveQueue.OnProfileReloaded -= HandleProfileReloaded;
        }

        /// <summary>PlayFab rechazó un cambio y el perfil se volvió a leer: mostrar lo real.</summary>
        private void HandleProfileReloaded(string reason)
        {
            if (_root == null || _root.style.display != DisplayStyle.Flex) return;
            if (Inv == null || Stash == null) { Hide(); return; }
            if (_isDragging) CancelDrag();
            Redraw();
        }

        /// <summary>Asegura el loadout persistente leído (el kit inicial vive acá). False si no se
        /// pudo leer: quien llama no debe seguir (ni entrar a una run ni guardar nada).</summary>
        public Task<bool> EnsureLoadoutLoadedAsync() => PlayerLoadoutService.EnsureInitializedAsync(_startingKit);

        /// <summary>Abre la pantalla. False (y no abre) si el loadout o el stash no se pudieron leer:
        /// mostrar datos vacíos y guardarlos pisaría los reales.</summary>
        public async Task<bool> TryShowAsync()
        {
            if (!await EnsureLoadoutLoadedAsync()) return false;
            if (!await StashService.EnsureInitializedAsync()) return false;

            // Datos guardados con otra capacidad de pockets u otra cantidad de slots de equipo
            // (kit viejo, un EquipmentSlot nuevo al final del enum) se ajustan una vez al abrir.
            // Si el ajuste no entra en el Stash, se deja todo como estaba (nunca se descarta nada).
            // Con el loadout bloqueado no se ajusta: el ajuste manda sobrantes de los pockets al stash
            // y guardar solo el stash duplicaría esos items.
            if (!LoadoutLocked)
            {
                var backup = TakeBackup();
                if (!NormalizeInventory()) RestoreBackup(backup);
                else if (!MatchesBackup(backup)) PersistAll();
            }

            bool wasVisible = IsVisible;
            _root.style.display = DisplayStyle.Flex;
            Redraw();
            if (!wasVisible) Shown?.Invoke();
            return true;
        }

        public void Hide()
        {
            if (_root == null) return;
            if (_isDragging) CancelDrag();
            HideTooltip();
            bool wasVisible = IsVisible;
            _root.style.display = DisplayStyle.None;
            if (wasVisible) Hidden?.Invoke();
        }

        private InventorySnapshot Inv => PlayerLoadoutService.Current;
        private StashData Stash => StashService.Stash;

        /// <summary>Persiste el inventario propio y el stash (cache instantánea + guardado en
        /// background vía PlayerLoadoutService/StashService) y refresca la UI. Llamar después de
        /// cualquier mutación de Inv o Stash hecha desde esta pantalla.</summary>
        private void PersistAndRedraw()
        {
            PersistAll();
            Redraw();
        }

        private void PersistAll()
        {
            if (LoadoutLocked)
            {
                StashService.Save(Stash); // el loadout no cambió (TryMutate lo garantiza)
                return;
            }
            PlayerLoadoutService.Save(Inv);
            StashService.Save(Stash);
        }
        private void Redraw()
        {
            HideTooltip();
            DrawEquipment();
            DrawPockets();
            DrawStash();
        }

        // ---------- Equipamiento ----------
        private void DrawEquipment()
        {
            _equipmentSlots.Clear();
            var equip = Inv.Equipment;

            // Orden de pantalla: de la cabeza a los pies y después los pockets (bolsas), separados
            // con su propio encabezado. No es el orden del enum (ver EquipmentSlotExtensions).
            var order = EquipmentSlotExtensions.DisplayIndices(equip.Count);
            bool bagHeaderAdded = false;
            for (int n = 0; n < order.Count; n++)
            {
                int i = order[n];
                bool isBag = ((EquipmentSlot)i).IsPocket();
                if (isBag && !bagHeaderAdded)
                {
                    var header = new Label("Bags");
                    header.AddToClassList("equip-group-header");
                    _equipmentSlots.Add(header);
                    bagHeaderAdded = true;
                }
                bool lastOfGroup = n == order.Count - 1 || (!isBag && ((EquipmentSlot)order[n + 1]).IsPocket());

                int slotIndex = i;
                var row = new VisualElement();
                row.AddToClassList("equip-slot");
                if (isBag) row.AddToClassList("equip-slot-bag");
                if (lastOfGroup) row.AddToClassList("no-border");
                row.userData = new DragInfo { Zone = SlotZone.Equipment, Index = slotIndex, Stack = equip[i] };

                var label = new Label(((EquipmentSlot)i).DisplayName());
                label.AddToClassList("equip-slot-label");
                row.Add(label);

                ItemStack stack = equip[i];
                if (!stack.IsEmpty)
                {
                    ItemSO def = _database.GetById(stack.ItemId);

                    var itemWrap = new VisualElement();
                    itemWrap.AddToClassList("equip-row-item");

                    var dot = new VisualElement();
                    dot.AddToClassList("accent-dot");
                    dot.AddToClassList(GetAccentClass(def));
                    itemWrap.Add(dot);

                    var name = new Label(def != null ? def.DisplayName : stack.ItemId);
                    name.AddToClassList("equip-item-name");
                    itemWrap.Add(name);

                    row.Add(itemWrap);

                    row.RegisterCallback<PointerEnterEvent>(_ => ShowTooltip(row, def));
                    row.RegisterCallback<PointerLeaveEvent>(_ => HideTooltip());

                    row.RegisterCallback<ClickEvent>(_ =>
                    {
                        if (_dragMoved) { _dragMoved = false; return; }
                        UnequipToPocket(slotIndex);
                    });

                    row.RegisterCallback<PointerDownEvent>(evt =>
                    {
                        if (evt.button != 0) return;
                        BeginDrag(SlotZone.Equipment, slotIndex, stack, evt.position);
                    });
                }
                else
                {
                    var empty = new Label("— empty —");
                    empty.AddToClassList("equip-row-empty");
                    row.Add(empty);
                }

                row.RegisterCallback<PointerUpEvent>(_ => TryDrop(SlotZone.Equipment, slotIndex));
                _equipmentSlots.Add(row);
            }
        }

        // ---------- Pockets ----------
        private void DrawPockets()
        {
            DrawPocketGrid(_pocketLGrid, _pocketLLabel, "Pocket L", Inv.PocketL, SlotZone.PocketL);
            DrawPocketGrid(_pocketRGrid, _pocketRLabel, "Pocket R", Inv.PocketR, SlotZone.PocketR);
        }

        private void DrawPocketGrid(VisualElement grid, Label label, string title, System.Collections.Generic.List<ItemStack> list, SlotZone zone)
        {
            grid.Clear();
            if (label != null) label.text = $"{title} — {list.Count}/{MaxPocketSlots}";

            for (int i = 0; i < MaxPocketSlots; i++)
            {
                if (i < list.Count)
                {
                    int idx = i;
                    grid.Add(BuildItemSlot(list[i], zone, idx,
                        normalClick: () => MovePocketToStash(zone, idx),
                        shiftClick: () => EquipFromPocket(zone, idx)));
                }
                else
                {
                    var locked = new VisualElement();
                    locked.AddToClassList("item-slot");
                    locked.AddToClassList("locked");
                    grid.Add(locked);
                }
            }
        }

        private int PocketCapacity(EquipmentSlot pocketSlot)
        {
            int idx = (int)pocketSlot;
            if (idx < Inv.Equipment.Count)
            {
                ItemStack eq = Inv.Equipment[idx];
                if (!eq.IsEmpty && _database.GetById(eq.ItemId) is EquipmentItemSO e && e.Slot.IsPocket())
                    return Mathf.Clamp(e.PocketSlots, 0, MaxPocketSlots);
            }
            return DefaultPocketCapacity;
        }

        // ---------- Transacciones (nunca perder items) ----------

        /// <summary>
        /// Ejecuta una mutación de Inv/Stash como transacción: después de la acción se reajustan
        /// los pockets a su capacidad (el sobrante va al Stash). Si la acción falla o algo no entra,
        /// se restaura el estado anterior completo y no se guarda nada. Reemplaza la simulación
        /// previa (CanShrinkPocketSafely), que cubría solo algunos casos y dejaba otros caminos
        /// que borraban items.
        /// </summary>
        private bool TryMutate(System.Func<bool> action)
        {
            var backup = TakeBackup();
            bool ok = action() && NormalizeInventory();

            // Loadout bloqueado (buscando partida): solo valen movimientos dentro del stash.
            if (ok && LoadoutLocked && !LoadoutMatches(backup)) ok = false;

            if (ok)
            {
                PersistAndRedraw();
                return true;
            }

            RestoreBackup(backup);
            Redraw();
            return false;
        }

        /// <summary>Deja Inv consistente: un slot de equipo por EquipmentSlot y cada pocket con su
        /// capacidad actual. Lo que sobra de un pocket va al Stash. False si algo no entra.</summary>
        private bool NormalizeInventory()
        {
            int slotCount = System.Enum.GetValues(typeof(EquipmentSlot)).Length;
            while (Inv.Equipment.Count < slotCount) Inv.Equipment.Add(ItemStack.Empty);

            return RebuildPocketWithRescue(EquipmentSlot.PocketL)
                && RebuildPocketWithRescue(EquipmentSlot.PocketR);
        }

        /// <summary>Ajusta la lista de un pocket a su capacidad: agrega vacíos si creció y, si bajó,
        /// manda el sobrante al Stash. False si el Stash no tiene lugar para todo.</summary>
        private bool RebuildPocketWithRescue(EquipmentSlot pocketSlot)
        {
            var list = pocketSlot == EquipmentSlot.PocketL ? Inv.PocketL : Inv.PocketR;
            int cap = PocketCapacity(pocketSlot);
            bool ok = true;

            while (list.Count < cap) list.Add(ItemStack.Empty);

            while (list.Count > cap)
            {
                int last = list.Count - 1;
                ItemStack orphan = list[last];
                list.RemoveAt(last);

                if (orphan.IsEmpty) continue;
                if (Stash.Add(orphan, Resolve) > 0) ok = false;
            }

            return ok;
        }

        private sealed class Backup
        {
            public List<ItemStack> Equipment, PocketL, PocketR, StashSlots;
        }

        private Backup TakeBackup() => new Backup
        {
            Equipment = new List<ItemStack>(Inv.Equipment),
            PocketL = new List<ItemStack>(Inv.PocketL),
            PocketR = new List<ItemStack>(Inv.PocketR),
            StashSlots = new List<ItemStack>(Stash.Slots),
        };

        // Se restaura el contenido de las mismas listas: PlayerLoadoutService/StashService
        // cachean esos objetos, reemplazarlos dejaría la cache apuntando a otra cosa.
        private void RestoreBackup(Backup b)
        {
            ReplaceContents(Inv.Equipment, b.Equipment);
            ReplaceContents(Inv.PocketL, b.PocketL);
            ReplaceContents(Inv.PocketR, b.PocketR);
            ReplaceContents(Stash.Slots, b.StashSlots);
        }

        private bool LoadoutMatches(Backup b)
            => SameStacks(Inv.Equipment, b.Equipment) && SameStacks(Inv.PocketL, b.PocketL)
               && SameStacks(Inv.PocketR, b.PocketR);

        private bool MatchesBackup(Backup b)
            => SameStacks(Inv.Equipment, b.Equipment) && SameStacks(Inv.PocketL, b.PocketL)
               && SameStacks(Inv.PocketR, b.PocketR) && SameStacks(Stash.Slots, b.StashSlots);

        private static void ReplaceContents(List<ItemStack> target, List<ItemStack> source)
        {
            target.Clear();
            target.AddRange(source);
        }

        private static bool SameStacks(List<ItemStack> a, List<ItemStack> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!a[i].Equals(b[i])) return false;
            return true;
        }

        private ItemSO Resolve(string itemId) => _database.GetById(itemId);

        // ---------- Ordenar stash ----------

        /// <summary>
        /// Ordena el stash (el loadout no se toca, así que también vale buscando partida). Primero
        /// junta pilas incompletas del mismo item (misma durabilidad), después ordena:
        /// - Por tipo: equipo en el orden de la lista de equipo (sombrero → túnica → guantes →
        ///   botas → bolsas), después el resto por categoría; dentro de cada tipo, rareza mayor primero.
        /// - Por rareza: primero los equipables y después el resto (sin mezclarse); dentro de cada
        ///   grupo épico → raro → común, y dentro de cada rareza, por tipo.
        /// Empate: nombre y después cantidad (mayor primero). Los slots vacíos quedan al final.
        /// </summary>
        private void SortStash(bool byRarity)
        {
            if (_isDragging) CancelDrag();
            TryMutate(() =>
            {
                var items = new List<ItemStack>();
                foreach (var s in Stash.Slots)
                    if (!s.IsEmpty) AddMerged(items, s);

                items.Sort((a, b) =>
                {
                    ItemSO da = Resolve(a.ItemId), db = Resolve(b.ItemId);
                    int byType = TypeRank(da).CompareTo(TypeRank(db));
                    int byRare = RarityRank(db).CompareTo(RarityRank(da)); // mayor primero
                    // Por rareza, igual los equipables van antes que el resto (sin mezclarse).
                    if (byRarity)
                    {
                        int group = (da is EquipmentItemSO ? 0 : 1).CompareTo(db is EquipmentItemSO ? 0 : 1);
                        if (group != 0) return group;
                    }
                    int first = byRarity ? byRare : byType;
                    if (first != 0) return first;
                    int second = byRarity ? byType : byRare;
                    if (second != 0) return second;
                    int byName = string.CompareOrdinal(da != null ? da.DisplayName : a.ItemId, db != null ? db.DisplayName : b.ItemId);
                    return byName != 0 ? byName : b.Quantity.CompareTo(a.Quantity);
                });

                if (items.Count > Stash.Slots.Count) return false; // no debería pasar: juntar nunca agrega pilas
                for (int i = 0; i < Stash.Slots.Count; i++)
                    Stash.Slots[i] = i < items.Count ? items[i] : ItemStack.Empty;
                return true;
            });
        }

        /// <summary>Agrega el stack juntándolo con pilas incompletas del mismo item y durabilidad.</summary>
        private void AddMerged(List<ItemStack> items, ItemStack stack)
        {
            ItemSO def = Resolve(stack.ItemId);
            int remaining = stack.Quantity;
            if (def != null && def.IsStackable)
            {
                for (int i = 0; i < items.Count && remaining > 0; i++)
                {
                    var s = items[i];
                    if (s.ItemId != stack.ItemId || !Mathf.Approximately(s.Durability, stack.Durability)) continue;
                    int add = Mathf.Min(def.MaxStack - s.Quantity, remaining);
                    if (add <= 0) continue;
                    items[i] = new ItemStack(s.ItemId, s.Quantity + add, s.Durability);
                    remaining -= add;
                }
            }
            if (remaining > 0) items.Add(new ItemStack(stack.ItemId, remaining, stack.Durability));
        }

        /// <summary>Equipo primero, en el orden de la lista de equipo; después el resto por categoría.</summary>
        private static int TypeRank(ItemSO def)
        {
            if (def == null) return int.MaxValue;
            if (def is EquipmentItemSO equip)
            {
                // Los dos lados de pocket son el mismo tipo (bolsa).
                EquipmentSlot slot = equip.Slot.IsPocket() ? EquipmentSlot.PocketL : equip.Slot;
                return slot.DisplayRank();
            }
            return 100 + (int)def.Category;
        }

        private static int RarityRank(ItemSO def) => def != null ? (int)def.Rarity : -1;

        // ---------- Stash ----------
        private void DrawStash()
        {
            _stashGrid.Clear();
            var slots = Stash.Slots;
            if (_stashLabel != null) _stashLabel.text = $"Stash — {slots.Count} Slots";

            for (int i = 0; i < slots.Count; i++)
            {
                int idx = i;
                _stashGrid.Add(BuildItemSlot(slots[i], SlotZone.Stash, idx,
                    normalClick: () => MoveStashToInventory(idx),
                    shiftClick: () => EquipFromStash(idx)));
            }
        }

        // ---------- Construcción de slot ----------
        private VisualElement BuildItemSlot(ItemStack stack, SlotZone zone, int index,
            System.Action normalClick, System.Action shiftClick)
        {
            var slot = new VisualElement();
            slot.AddToClassList("item-slot");
            slot.userData = new DragInfo { Zone = zone, Index = index, Stack = stack };

            if (!stack.IsEmpty)
            {
                ItemSO def = _database.GetById(stack.ItemId);
                slot.AddToClassList(GetAccentClass(def));

                var name = new Label(def != null ? def.DisplayName : stack.ItemId);
                name.AddToClassList("item-name");
                slot.AddToClassList(ItemTooltipFormatter.RarityClass(def));
                slot.Add(name);

                if (stack.Quantity > 1)
                {
                    var qty = new Label($"x{stack.Quantity}");
                    qty.AddToClassList("item-qty");
                    slot.Add(qty);
                }
                
                slot.RegisterCallback<PointerEnterEvent>(_ => ShowTooltip(slot, def));
                slot.RegisterCallback<PointerLeaveEvent>(_ => HideTooltip());

                slot.RegisterCallback<ClickEvent>(evt =>
                {
                    if (_dragMoved) { _dragMoved = false; return; }
                    if (evt.shiftKey) shiftClick?.Invoke();
                    else normalClick?.Invoke();
                });

                slot.RegisterCallback<PointerDownEvent>(evt =>
                {
                    if (evt.button != 0) return;
                    BeginDrag(zone, index, stack, evt.position);
                });
            }

            slot.RegisterCallback<PointerUpEvent>(_ => TryDrop(zone, index));
            return slot;
        }

        private string GetAccentClass(ItemSO def)
        {
            if (def is EquipmentItemSO equip)
            {
                switch (equip.Slot)
                {
                    case EquipmentSlot.Boots: return "accent-green";
                    case EquipmentSlot.Hat: return "accent-cyan";
                    case EquipmentSlot.Robe: return "accent-violet";
                    case EquipmentSlot.Glove: return "accent-gold";
                    case EquipmentSlot.PocketL:
                    case EquipmentSlot.PocketR:
                        return "accent-amber";
                }
            }
            return "accent-loot";
        }

        // ---------- Tooltip ----------
        private void ShowTooltip(VisualElement anchor, ItemSO def)
        {
            if (_tooltip == null || def == null || _isDragging) return;

            _tooltipTitle.RemoveFromClassList("rarity-common");
            _tooltipTitle.RemoveFromClassList("rarity-rare");
            _tooltipTitle.RemoveFromClassList("rarity-epic");
            _tooltipTitle.AddToClassList(ItemTooltipFormatter.RarityClass(def));

            _tooltipTitle.text = def.DisplayName;
            var (type, stats) = ItemTooltipFormatter.Build(def);
            _tooltipType.text = type;

            bool hasDescription = !string.IsNullOrEmpty(def.Description);
            _tooltipDescription.text = def.Description;
            _tooltipDescription.style.display = hasDescription ? DisplayStyle.Flex : DisplayStyle.None;

            _tooltipStats.Clear();
            foreach (var stat in stats)
            {
                var line = new Label(stat.Text);
                line.AddToClassList("tooltip-stat-line");
                line.AddToClassList(stat.Sign > 0 ? "tooltip-stat-positive" : stat.Sign < 0 ? "tooltip-stat-negative" : "tooltip-stat-neutral");
                _tooltipStats.Add(line);
            }

            Rect bound = anchor.worldBound;
            _tooltip.style.left = bound.x;
            _tooltip.style.top = bound.y; // provisional, se corrige abajo cuando se conoce la altura real
            _pendingTooltipAnchorBottom = bound.y;
            _tooltip.style.display = DisplayStyle.Flex;
            _tooltip.RegisterCallback<GeometryChangedEvent>(OnTooltipGeometryChanged);
        }

        private void OnTooltipGeometryChanged(GeometryChangedEvent evt)
        {
            _tooltip.UnregisterCallback<GeometryChangedEvent>(OnTooltipGeometryChanged);
            _tooltip.style.top = _pendingTooltipAnchorBottom - evt.newRect.height - 10; // arriba del slot, con aire
        }

        private void HideTooltip()
        {
            if (_tooltip != null) _tooltip.style.display = DisplayStyle.None;
        }

        // ---------- Drag & drop ----------
        private void BeginDrag(SlotZone zone, int index, ItemStack stack, Vector2 pos)
        {
            if (LoadoutLocked && zone != SlotZone.Stash) return; // el equipo no se mueve buscando partida

            _dragging = new DragInfo { Zone = zone, Index = index, Stack = stack };
            _isDragging = true;
            _dragMoved = false;

            _ghost = new VisualElement();
            _ghost.AddToClassList("drag-ghost");
            _ghost.pickingMode = PickingMode.Ignore;
            var def = _database.GetById(stack.ItemId);
            var name = new Label(def != null ? def.DisplayName : stack.ItemId);
            name.AddToClassList("item-name");
            name.pickingMode = PickingMode.Ignore;
            _ghost.Add(name);
            _root.Add(_ghost);
            MoveGhost(pos);

            _root.RegisterCallback<PointerMoveEvent>(OnDragMove);
        }

        private void OnDragMove(PointerMoveEvent evt)
        {
            if (!_isDragging) return;
            _dragMoved = true;
            MoveGhost(evt.position);
        }

        private void MoveGhost(Vector2 pos)
        {
            if (_ghost == null) return;
            _ghost.style.left = pos.x - 30;
            _ghost.style.top = pos.y - 30;
        }

        private void TryDrop(SlotZone destZone, int destIndex)
        {
            if (!_isDragging) return;

            var from = _dragging;
            bool moved = _dragMoved;
            EndDrag();

            if (!moved) return;

            TryMutate(() => MoveItem(from.Zone, from.Index, destZone, destIndex));
        }

        private void CancelDrag() => EndDrag();

        private void EndDrag()
        {
            _isDragging = false;
            _root.UnregisterCallback<PointerMoveEvent>(OnDragMove);
            if (_ghost != null) { _ghost.RemoveFromHierarchy(); _ghost = null; }
        }

        /// <summary>Mueve o intercambia entre dos slots cualesquiera (equipo, pockets, stash), con
        /// merge de apilables. No persiste: se llama dentro de TryMutate.</summary>
        private bool MoveItem(SlotZone fromZone, int fromIndex, SlotZone toZone, int toIndex)
        {
            if (fromZone == toZone && fromIndex == toIndex) return false;
            if (!IsValidSlot(fromZone, fromIndex) || !IsValidSlot(toZone, toIndex)) return false;

            ItemStack item = GetStack(fromZone, fromIndex);
            if (item.IsEmpty) return false;

            if (toZone == SlotZone.Equipment)
            {
                if (Resolve(item.ItemId) is not EquipmentItemSO equip) return false;
                if (!ValidEquipTarget(equip, toIndex)) return false;
            }

            ItemStack existing = GetStack(toZone, toIndex);

            // Merge: mismo item apilable.
            if (!existing.IsEmpty && existing.ItemId == item.ItemId && Resolve(item.ItemId) is ItemSO def && def.IsStackable)
            {
                int space = def.MaxStack - existing.Quantity;
                if (space <= 0) return false;
                int move = Mathf.Min(space, item.Quantity);
                SetStack(toZone, toIndex, new ItemStack(existing.ItemId, existing.Quantity + move, existing.Durability));
                int remaining = item.Quantity - move;
                SetStack(fromZone, fromIndex, remaining > 0 ? new ItemStack(item.ItemId, remaining, item.Durability) : ItemStack.Empty);
                return true;
            }

            // Swap: lo que había en el destino vuelve al origen. Si el origen es un slot de
            // equipo, tiene que poder ir ahí (antes un material podía terminar en el slot Hat).
            if (!existing.IsEmpty && fromZone == SlotZone.Equipment)
            {
                if (Resolve(existing.ItemId) is not EquipmentItemSO exEquip || !ValidEquipTarget(exEquip, fromIndex))
                    return false;
            }

            SetStack(toZone, toIndex, item);
            SetStack(fromZone, fromIndex, existing);
            return true;
        }

        private bool ValidEquipTarget(EquipmentItemSO equip, int toIndex)
        {
            if (equip.Slot.IsPocket())
                return toIndex == (int)EquipmentSlot.PocketL || toIndex == (int)EquipmentSlot.PocketR;
            return (int)equip.Slot == toIndex;
        }

        private bool IsValidSlot(SlotZone zone, int index)
        {
            switch (zone)
            {
                case SlotZone.Equipment: return index >= 0 && index < Inv.Equipment.Count;
                case SlotZone.PocketL: return index >= 0 && index < Inv.PocketL.Count;
                case SlotZone.PocketR: return index >= 0 && index < Inv.PocketR.Count;
                case SlotZone.Stash: return index >= 0 && index < Stash.Slots.Count;
            }
            return false;
        }

        private ItemStack GetStack(SlotZone zone, int index)
        {
            switch (zone)
            {
                case SlotZone.Equipment: return (index >= 0 && index < Inv.Equipment.Count) ? Inv.Equipment[index] : ItemStack.Empty;
                case SlotZone.PocketL: return (index >= 0 && index < Inv.PocketL.Count) ? Inv.PocketL[index] : ItemStack.Empty;
                case SlotZone.PocketR: return (index >= 0 && index < Inv.PocketR.Count) ? Inv.PocketR[index] : ItemStack.Empty;
                case SlotZone.Stash: return (index >= 0 && index < Stash.Slots.Count) ? Stash.Slots[index] : ItemStack.Empty;
            }
            return ItemStack.Empty;
        }

        private void SetStack(SlotZone zone, int index, ItemStack stack)
        {
            switch (zone)
            {
                case SlotZone.Equipment: if (index >= 0 && index < Inv.Equipment.Count) Inv.Equipment[index] = stack; break;
                case SlotZone.PocketL: if (index >= 0 && index < Inv.PocketL.Count) Inv.PocketL[index] = stack; break;
                case SlotZone.PocketR: if (index >= 0 && index < Inv.PocketR.Count) Inv.PocketR[index] = stack; break;
                case SlotZone.Stash: if (index >= 0 && index < Stash.Slots.Count) Stash.Slots[index] = stack; break;
            }
        }

        // ---------- Equipar (shift+clic) ----------
        private void EquipFromPocket(SlotZone zone, int index)
        {
            var list = zone == SlotZone.PocketL ? Inv.PocketL : Inv.PocketR;
            TryMutate(() => index >= 0 && index < list.Count
                            && TryEquip(list[index], () => list[index] = ItemStack.Empty));
        }

        private void EquipFromStash(int stashIndex)
        {
            TryMutate(() => stashIndex >= 0 && stashIndex < Stash.Slots.Count
                            && TryEquip(Stash.Slots[stashIndex], () => Stash.TakeAt(stashIndex)));
        }

        /// <summary>Equipa el stack sacándolo de su origen. Lo que estaba equipado (y cualquier
        /// cantidad extra del stack) va a los pockets o, si no hay lugar, al Stash; si tampoco
        /// entra, devuelve false y TryMutate deshace todo.</summary>
        private bool TryEquip(ItemStack stack, System.Action removeFromSource)
        {
            if (stack.IsEmpty) return false;
            if (Resolve(stack.ItemId) is not EquipmentItemSO equip) return false;

            int slotIndex;
            if (equip.Slot.IsPocket())
            {
                int lIdx = (int)EquipmentSlot.PocketL;
                int rIdx = (int)EquipmentSlot.PocketR;
                if (Inv.Equipment[lIdx].IsEmpty) slotIndex = lIdx;
                else if (Inv.Equipment[rIdx].IsEmpty) slotIndex = rIdx;
                else slotIndex = lIdx; // las dos ocupadas: reemplaza L
            }
            else
            {
                slotIndex = (int)equip.Slot;
            }

            if (slotIndex < 0 || slotIndex >= Inv.Equipment.Count) return false;

            ItemStack current = Inv.Equipment[slotIndex];
            removeFromSource();
            Inv.Equipment[slotIndex] = new ItemStack(stack.ItemId, 1, stack.Durability);

            if (stack.Quantity > 1 && !StoreSomewhere(new ItemStack(stack.ItemId, stack.Quantity - 1, stack.Durability)))
                return false;
            if (!current.IsEmpty && !StoreSomewhere(current))
                return false;

            return true;
        }

        // ---------- Movimientos por clic ----------
        private void MovePocketToStash(SlotZone zone, int index)
        {
            var list = zone == SlotZone.PocketL ? Inv.PocketL : Inv.PocketR;
            TryMutate(() =>
            {
                if (index < 0 || index >= list.Count) return false;
                ItemStack stack = list[index];
                if (stack.IsEmpty) return false;

                int notAdded = Stash.Add(stack, Resolve);
                if (notAdded >= stack.Quantity) return false; // no entró nada
                list[index] = notAdded <= 0 ? ItemStack.Empty : new ItemStack(stack.ItemId, notAdded, stack.Durability);
                return true;
            });
        }

        private void MoveStashToInventory(int stashIndex)
        {
            TryMutate(() =>
            {
                if (stashIndex < 0 || stashIndex >= Stash.Slots.Count) return false;
                ItemStack stack = Stash.Slots[stashIndex];
                if (stack.IsEmpty) return false;

                // Pockets llenos: no se mueve nada (antes el item salía del stash igual y se perdía).
                if (!AddToPockets(stack)) return false;
                Stash.TakeAt(stashIndex);
                return true;
            });
        }

        private void UnequipToPocket(int equipSlotIndex)
        {
            TryMutate(() =>
            {
                if (equipSlotIndex < 0 || equipSlotIndex >= Inv.Equipment.Count) return false;
                ItemStack stack = Inv.Equipment[equipSlotIndex];
                if (stack.IsEmpty) return false;

                Inv.Equipment[equipSlotIndex] = ItemStack.Empty;
                return StoreSomewhere(stack);
            });
        }

        /// <summary>Guarda el stack en el primer slot libre de los pockets o, si no hay, en el Stash. False si no entró entero.</summary>
        private bool StoreSomewhere(ItemStack stack)
            => AddToPockets(stack) || Stash.Add(stack, Resolve) <= 0;

        /// <summary>
        /// Mete el stack entero en los pockets: si es apilable, primero completa las pilas del mismo
        /// item que ya haya (L y R) y el resto ocupa un slot vacío. Todo o nada: si no entra
        /// completo no toca nada (StoreSomewhere prueba después el stash con el stack entero).
        /// </summary>
        private bool AddToPockets(ItemStack stack)
        {
            ItemSO def = Resolve(stack.ItemId);
            bool stackable = def != null && def.IsStackable;
            int maxStack = stackable ? def.MaxStack : 1;

            int stackSpace = 0;
            if (stackable)
                foreach (var list in new[] { Inv.PocketL, Inv.PocketR })
                    foreach (var s in list)
                        if (!s.IsEmpty && s.ItemId == stack.ItemId) stackSpace += Mathf.Max(0, maxStack - s.Quantity);

            int emptyIndexL = Inv.PocketL.FindIndex(s => s.IsEmpty);
            int emptyIndexR = emptyIndexL >= 0 ? -1 : Inv.PocketR.FindIndex(s => s.IsEmpty);
            bool hasEmpty = emptyIndexL >= 0 || emptyIndexR >= 0;

            int leftover = stack.Quantity - Mathf.Min(stackSpace, stack.Quantity);
            if (leftover > 0 && (!hasEmpty || leftover > maxStack)) return false;

            int remaining = stack.Quantity;
            if (stackable)
            {
                foreach (var list in new[] { Inv.PocketL, Inv.PocketR })
                {
                    for (int i = 0; i < list.Count && remaining > 0; i++)
                    {
                        var s = list[i];
                        if (s.IsEmpty || s.ItemId != stack.ItemId) continue;
                        int add = Mathf.Min(maxStack - s.Quantity, remaining);
                        if (add <= 0) continue;
                        list[i] = new ItemStack(s.ItemId, s.Quantity + add, s.Durability);
                        remaining -= add;
                    }
                }
            }

            if (remaining > 0)
            {
                var target = emptyIndexL >= 0 ? Inv.PocketL : Inv.PocketR;
                int index = emptyIndexL >= 0 ? emptyIndexL : emptyIndexR;
                target[index] = new ItemStack(stack.ItemId, remaining, stack.Durability);
            }
            return true;
        }
    }
}