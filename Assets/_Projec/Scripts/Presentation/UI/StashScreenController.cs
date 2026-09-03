using Game.Core.Items;
using Game.Presentation.Run;
using UnityEngine;
using UnityEngine.UIElements;
using System.Collections.Generic;

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
            _tooltip = _root.Q<VisualElement>("item-tooltip");
            _tooltipTitle = _root.Q<Label>("tooltip-title");
            _tooltipType = _root.Q<Label>("tooltip-type");
            _tooltipDescription = _root.Q<Label>("tooltip-description");
            _tooltipStats = _root.Q<VisualElement>("tooltip-stats");

            var closeBtn = _root.Q<Button>("stash-close");
            if (closeBtn != null) closeBtn.clicked += Hide;

            _root.RegisterCallback<PointerUpEvent>(_ => { if (_isDragging) CancelDrag(); });
        }

        public async void Show()
        {
            await PlayerLoadoutService.EnsureInitializedAsync(_startingKit);
            await StashService.EnsureInitializedAsync();
            _root.style.display = DisplayStyle.Flex;
            Redraw();
        }

        public void Hide() => _root.style.display = DisplayStyle.None;

        private InventorySnapshot Inv => PlayerLoadoutService.Current;
        private StashData Stash => StashService.Stash;

        /// <summary>Persiste el inventario propio y el stash (cache instantánea + guardado en
        /// background vía PlayerLoadoutService/StashService) y refresca la UI. Llamar después de
        /// cualquier mutación de Inv o Stash hecha desde esta pantalla.</summary>
        private void PersistAndRedraw()
        {
            PlayerLoadoutService.Save(Inv);
            StashService.Save(Stash);
            Redraw();
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

            for (int i = 0; i < equip.Count; i++)
            {

                int slotIndex = i;
                var row = new VisualElement();
                row.AddToClassList("equip-slot");
                if (slotIndex == equip.Count - 1) row.AddToClassList("no-border");
                row.userData = new DragInfo { Zone = SlotZone.Equipment, Index = slotIndex, Stack = equip[i] };

                var label = new Label(DisplayNameForSlot((EquipmentSlot)i));
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

        private string DisplayNameForSlot(EquipmentSlot slot)
        {
            return slot switch
            {
                EquipmentSlot.PocketL => "Pocket L",
                EquipmentSlot.PocketR => "Pocket R",
                _ => slot.ToString()
            };
        }

        // ---------- Pockets ----------
        private void DrawPockets()
        {
            RebuildPocketWithRescue(EquipmentSlot.PocketL);
            RebuildPocketWithRescue(EquipmentSlot.PocketR);

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

        /// <summary>
        /// ¿Cambiar el equipo de `targetEquipSlot` a `newItem` (o a nada, si null) dejaría items
        /// sin espacio? Si es así, simula si el Stash los absorbería (mergeando en pilas
        /// existentes primero, después en slots vacíos — mismo algoritmo real de StashData.Add).
        /// No muta nada — trabaja sobre una copia. Solo informa true/false.
        /// </summary>
        private bool CanShrinkPocketSafely(EquipmentSlot targetEquipSlot, EquipmentItemSO newItem)
        {
            if (!targetEquipSlot.IsPocket()) return true;

            var list = targetEquipSlot == EquipmentSlot.PocketL ? Inv.PocketL : Inv.PocketR;

            int newCap = (newItem != null && newItem.Slot.IsPocket())
                ? Mathf.Clamp(newItem.PocketSlots, 0, MaxPocketSlots)
                : DefaultPocketCapacity;
            if (newCap <= 0) newCap = DefaultPocketCapacity;

            var overflow = new System.Collections.Generic.List<ItemStack>();
            for (int i = newCap; i < list.Count; i++)
                if (!list[i].IsEmpty) overflow.Add(list[i]);

            if (overflow.Count == 0) return true;

            // Copia de trabajo del Stash: acá simulamos, no tocamos el real todavía.
            var simulatedStash = new System.Collections.Generic.List<ItemStack>(Stash.Slots);

            foreach (var item in overflow)
            {
                ItemSO def = _database.GetById(item.ItemId);
                int remaining = item.Quantity;

                // 1. Igual que StashData.Add: mergear en pilas existentes primero.
                if (def != null && def.IsStackable)
                {
                    for (int i = 0; i < simulatedStash.Count && remaining > 0; i++)
                    {
                        ItemStack s = simulatedStash[i];
                        if (s.IsEmpty || s.ItemId != item.ItemId) continue;
                        int space = def.MaxStack - s.Quantity;
                        if (space <= 0) continue;
                        int add = Mathf.Min(space, remaining);
                        s.Quantity += add;
                        simulatedStash[i] = s;
                        remaining -= add;
                    }
                }

                // 2. Lo que no mergeó, necesita slots vacíos.
                while (remaining > 0)
                {
                    int freeIndex = simulatedStash.FindIndex(s => s.IsEmpty);
                    if (freeIndex < 0) return false; // no entra, ni mergeando ni en vacíos

                    int add = (def != null && def.IsStackable) ? Mathf.Min(def.MaxStack, remaining) : 1;
                    simulatedStash[freeIndex] = new ItemStack(item.ItemId, add, item.Durability);
                    remaining -= add;
                }
            }

            return true;
        }

        /// <summary>
        /// Ajusta la lista de un pocket a su capacidad actual: agrega vacíos si creció, y si
        /// bajó, manda el sobrante al Stash (ya validado antes por CanShrinkPocketSafely — este
        /// método asume que va a entrar; si por algún motivo no entrara, se descarta en silencio).
        /// </summary>
        private void RebuildPocketWithRescue(EquipmentSlot pocketSlot)
        {
            var list = pocketSlot == EquipmentSlot.PocketL ? Inv.PocketL : Inv.PocketR;
            int cap = PocketCapacity(pocketSlot);

            while (list.Count < cap) list.Add(ItemStack.Empty);

            while (list.Count > cap)
            {
                int last = list.Count - 1;
                ItemStack orphan = list[last];
                list.RemoveAt(last);

                if (orphan.IsEmpty) continue;
                Stash.Add(orphan, id => _database.GetById(id));
            }
        }

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

            MoveItem(from.Zone, from.Index, destZone, destIndex);
            PersistAndRedraw();
        }

        private void CancelDrag() => EndDrag();

        private void EndDrag()
        {
            _isDragging = false;
            _root.UnregisterCallback<PointerMoveEvent>(OnDragMove);
            if (_ghost != null) { _ghost.RemoveFromHierarchy(); _ghost = null; }
        }

        private void MoveItem(SlotZone fromZone, int fromIndex, SlotZone toZone, int toIndex)
        {
            if (fromZone == toZone && fromIndex == toIndex) return;

            ItemStack item = GetStack(fromZone, fromIndex);
            if (item.IsEmpty) return;

            if (toZone == SlotZone.Equipment)
            {
                if (_database.GetById(item.ItemId) is not EquipmentItemSO equip) return;
                if (!ValidEquipTarget(equip, toIndex)) return;
            }

            ItemStack existing = GetStack(toZone, toIndex);

            // Pre-chequeo (sin mutar todavía): si el destino es un pocket, ¿el sobrante entra en el Stash?
            if (toZone == SlotZone.Equipment && ((EquipmentSlot)toIndex).IsPocket())
            {
                EquipmentItemSO incoming = _database.GetById(item.ItemId) as EquipmentItemSO;
                if (!CanShrinkPocketSafely((EquipmentSlot)toIndex, incoming))
                    return; // bloquear el cambio entero
            }

            // Pre-chequeo: si el origen es un pocket equipado, ¿lo que queda ahí después alcanza?
            if (fromZone == SlotZone.Equipment && ((EquipmentSlot)fromIndex).IsPocket())
            {
                EquipmentItemSO remaining = (!existing.IsEmpty && _database.GetById(existing.ItemId) is EquipmentItemSO exEq) ? exEq : null;
                if (!CanShrinkPocketSafely((EquipmentSlot)fromIndex, remaining))
                    return; // bloquear el cambio entero
            }

            SetStack(toZone, toIndex, new ItemStack(item.ItemId, item.Quantity, item.Durability));
            SetStack(fromZone, fromIndex, ItemStack.Empty);

            if (!existing.IsEmpty)
                SetStack(fromZone, fromIndex, existing);

            if (toZone == SlotZone.Equipment && ((EquipmentSlot)toIndex).IsPocket())
                RebuildPocketWithRescue((EquipmentSlot)toIndex);
            if (fromZone == SlotZone.Equipment && ((EquipmentSlot)fromIndex).IsPocket())
                RebuildPocketWithRescue((EquipmentSlot)fromIndex);
        }

        private bool ValidEquipTarget(EquipmentItemSO equip, int toIndex)
        {
            if (equip.Slot.IsPocket())
                return toIndex == (int)EquipmentSlot.PocketL || toIndex == (int)EquipmentSlot.PocketR;
            return (int)equip.Slot == toIndex;
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
            if (index < 0 || index >= list.Count) return;
            ItemStack stack = list[index];
            TryEquip(stack, () => list[index] = ItemStack.Empty);
            PersistAndRedraw();
        }

        private void EquipFromStash(int stashIndex)
        {
            ItemStack stack = Stash.Slots[stashIndex];
            TryEquip(stack, () => Stash.TakeAt(stashIndex));
            PersistAndRedraw();
        }

        private bool TryEquip(ItemStack stack, System.Action removeFromSource)
        {
            if (stack.IsEmpty) return false;
            if (_database.GetById(stack.ItemId) is not EquipmentItemSO equip) return false;

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

            // Pre-chequeo: si reemplazamos un pocket más grande por este, ¿el sobrante entra en el Stash?
            if (((EquipmentSlot)slotIndex).IsPocket() && !CanShrinkPocketSafely((EquipmentSlot)slotIndex, equip))
                return false; // bloquear el cambio entero

            ItemStack current = Inv.Equipment[slotIndex];
            removeFromSource();
            Inv.Equipment[slotIndex] = new ItemStack(stack.ItemId, 1, stack.Durability);

            if (!current.IsEmpty)
                AddToPockets(current);

            if (((EquipmentSlot)slotIndex).IsPocket())
                RebuildPocketWithRescue((EquipmentSlot)slotIndex);

            return true;
        }

        // ---------- Movimientos por clic ----------
        private void MovePocketToStash(SlotZone zone, int index)
        {
            var list = zone == SlotZone.PocketL ? Inv.PocketL : Inv.PocketR;
            if (index < 0 || index >= list.Count) return;
            ItemStack stack = list[index];
            if (stack.IsEmpty) return;

            int notAdded = Stash.Add(stack, id => _database.GetById(id));
            if (notAdded <= 0)
                list[index] = ItemStack.Empty;
            else
            {
                var s = stack; s.Quantity = notAdded; list[index] = s;
            }
            PersistAndRedraw();
        }

        private void MoveStashToInventory(int stashIndex)
        {
            ItemStack stack = Stash.Slots[stashIndex];
            if (stack.IsEmpty) return;

            AddToPockets(stack);
            Stash.TakeAt(stashIndex);
            PersistAndRedraw();
        }

        private void UnequipToPocket(int equipSlotIndex)
        {
            if (equipSlotIndex < 0 || equipSlotIndex >= Inv.Equipment.Count) return;
            ItemStack stack = Inv.Equipment[equipSlotIndex];
            if (stack.IsEmpty) return;

            EquipmentSlot slotEnum = (EquipmentSlot)equipSlotIndex;

            // Pre-chequeo: si es un pocket, al sacarlo la capacidad vuelve al default (1) — ¿entra el sobrante en el Stash?
            if (slotEnum.IsPocket() && !CanShrinkPocketSafely(slotEnum, null))
                return; // bloquear: no se saca nada

            AddToPockets(stack);
            Inv.Equipment[equipSlotIndex] = ItemStack.Empty;

            if (slotEnum.IsPocket())
                RebuildPocketWithRescue(slotEnum);

            PersistAndRedraw();
        }

        private bool AddToPockets(ItemStack stack)
        {
            for (int i = 0; i < Inv.PocketL.Count; i++)
                if (Inv.PocketL[i].IsEmpty) { Inv.PocketL[i] = stack; return true; }
            for (int i = 0; i < Inv.PocketR.Count; i++)
                if (Inv.PocketR[i].IsEmpty) { Inv.PocketR[i] = stack; return true; }
            return false;
        }
    }
}