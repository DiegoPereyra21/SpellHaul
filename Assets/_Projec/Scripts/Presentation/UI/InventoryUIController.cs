using System.Collections.Generic;
using FishNet.Object;
using Game.Core.Items;
using Game.Presentation.Abilities;
using Game.Presentation.Combat;
using Game.Presentation.Player;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Game.Presentation.UI
{
    public class InventoryUIController : NetworkBehaviour
    {
        [SerializeField] private ItemDatabase _database;
        [SerializeField] private RunInventory _inventory;
        [SerializeField] private PlayerMovementController _movement;
        [SerializeField] private AbilityController _abilities;
        [SerializeField] private PlayerInteraction _interaction;
        [SerializeField] private UIDocument _document;

        private const int MaxPocketSlots = 12;

        private VisualElement _root;
        private VisualElement _invPanel;
        private VisualElement _equipmentSlots;
        private VisualElement _pocketLGrid;
        private VisualElement _pocketRGrid;
        private Label _pocketLLabel;
        private Label _pocketRLabel;
        private VisualElement _usableSlots;
        private VisualElement _containerColumn;
        private VisualElement _containerGrid;
        private Label _containerLabel;
        private VisualElement _tooltip;
        private Label _tooltipTitle;
        private Label _tooltipType;
        private Label _tooltipDescription;
        private VisualElement _tooltipStats;
        private float _pendingTooltipAnchorBottom;
        private InputAction _toggleAction;
        private PlayerControls _controls;
        private bool _isOpen;

        private LootContainer _openContainer;

        // ---------- Drag & drop ----------
        private enum SlotZone { Equipment, PocketL, PocketR, Container }
        private struct DragInfo { public SlotZone Zone; public int Index; public ItemStack Stack; }
        private DragInfo _dragging;
        private bool _isDragging;
        private VisualElement _ghost;
        private bool _dragMoved;
        private bool _dragOutside;

        private static readonly Color GhostColorNormal  = new Color(0.16f, 0.16f, 0.24f, 0.95f);
        private static readonly Color GhostColorDrop    = new Color(0.55f, 0.08f, 0.08f, 0.95f);

        private PlayerAvatarState _avatar;

        private void Awake()
        {
            _avatar = GetComponent<PlayerAvatarState>();
            if (_interaction == null) _interaction = GetComponent<PlayerInteraction>();
            _controls = new PlayerControls();
            _toggleAction = _controls.Player.ToggleInventory;
        }

        private void OnDestroy()
        {
            _controls?.Dispose();
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            ApplyOwnershipClient();
        }

        // Se reevalúa al cambiar de dueño: al reconectar, el cliente recibe su personaje primero
        // sin dueño (el servidor le devuelve el control un instante después) y OnStartClient ya
        // corrió como si fuera de otro jugador.
        public override void OnOwnershipClient(FishNet.Connection.NetworkConnection prevOwner)
        {
            base.OnOwnershipClient(prevOwner);
            ApplyOwnershipClient();
        }

        private bool _ownerSetupDone;

        private void ApplyOwnershipClient()
        {
            if (!base.IsOwner) { enabled = false; return; }

            enabled = true;
            if (_ownerSetupDone) return;
            _ownerSetupDone = true;

            var go = GameObject.Find("InventoryUIDocument");
            if (go != null) _document = go.GetComponent<UIDocument>();

            if (_document == null)
            {
                Debug.LogError("[InventoryUI] No se encontró 'InventoryUIDocument' en la escena.");
                return;
            }

            _root            = _document.rootVisualElement.Q<VisualElement>("inv-root");
            _invPanel        = _root.Q<VisualElement>("inv-panel");
            _equipmentSlots  = _root.Q<VisualElement>("equipment-slots");
            _pocketLGrid     = _root.Q<VisualElement>("pocket-l-grid");
            _pocketRGrid     = _root.Q<VisualElement>("pocket-r-grid");
            _pocketLLabel    = _root.Q<Label>("pocket-l-label") ?? _root.Q<VisualElement>("pocket-l-grid").parent.Q<Label>();
            _pocketRLabel    = _root.Q<Label>("pocket-r-label") ?? _root.Q<VisualElement>("pocket-r-grid").parent.Q<Label>();
            _usableSlots     = _root.Q<VisualElement>("usable-slots");
            _containerColumn = _root.Q<VisualElement>("container-column");
            _containerGrid   = _root.Q<VisualElement>("container-grid");
            _tooltip = _root.Q<VisualElement>("item-tooltip");
            _tooltipTitle = _root.Q<Label>("tooltip-title");
            _tooltipType = _root.Q<Label>("tooltip-type");
            _tooltipDescription = _root.Q<Label>("tooltip-description");
            _tooltipStats = _root.Q<VisualElement>("tooltip-stats");
            _containerLabel = _root.Q<Label>("container-label");
            BuildUsableSlots();

            Game.Presentation.Audio.GameAudio.AttachButtonSounds(_root);

            var sortType = _root.Q<Button>("pockets-sort-type");
            if (sortType != null) sortType.clicked += () => SortPockets(ItemSortMode.Type);
            var sortRarity = _root.Q<Button>("pockets-sort-rarity");
            if (sortRarity != null) sortRarity.clicked += () => SortPockets(ItemSortMode.Rarity);

            _inventory.OnInventoryChanged += Redraw;
            _toggleAction.Enable();

            _root.RegisterCallback<PointerUpEvent>(OnRootPointerUp);
        }

        public override void OnStopClient()
        {
            base.OnStopClient();
            if (_inventory != null) _inventory.OnInventoryChanged -= Redraw;
            _toggleAction.Disable();
        }

        private void Update()
        {
            if (!base.IsOwner) return;
            if (PauseMenuController.IsPaused) return; // con el menú de pausa abierto, Tab no hace nada
            if (_toggleAction.WasPressedThisFrame()) SetOpen(!_isOpen);

            // R con el inventario abierto: ordenar los pockets con el último criterio usado.
            if (_isOpen && Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame)
                SortPockets(StashScreenController.LastSortMode);
        }

        /// <summary>Ordena los pockets (lo hace el servidor) y recuerda el criterio para la tecla R.</summary>
        private void SortPockets(ItemSortMode mode)
        {
            if (_isDragging) EndDrag();
            StashScreenController.LastSortMode = mode;
            Sfx(l => l.Sort);
            SortPocketsServerRpc((int)mode);
        }

        // Sonidos de las acciones del inventario: suenan al pedirlas (el servidor las valida después;
        // esperar la confirmación se sentiría con retraso).
        private static void Sfx(System.Func<Game.Presentation.Audio.AudioLibrarySO, AudioClip> select)
            => Game.Presentation.Audio.GameAudio.Ui(select);

        private static void SfxEquip()
        {
            var lib = Game.Presentation.Audio.GameAudio.Library;
            if (lib != null) Game.Presentation.Audio.GameAudio.Play2D(Game.Presentation.Audio.GameAudio.Pick(lib.ItemEquip), lib.UiVolume, 0.05f);
        }

        private void SetOpen(bool open)
        {
            if (open != _isOpen) Sfx(l => open ? l.InventoryOpen : l.InventoryClose);
            _isOpen = open;
            _root.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;

            UnityEngine.Cursor.lockState = open ? CursorLockMode.None : CursorLockMode.Locked;
            UnityEngine.Cursor.visible = open;
            if (_movement != null)    _movement.SetInputBlocked(open);
            if (_abilities != null)   _abilities.SetInputBlocked(open);
            if (_interaction != null) _interaction.SetInputBlocked(open);

            if (open) Redraw();
            else CloseContainer();
        }

        public bool IsOpen => _isOpen;

        /// <summary>Cierra el inventario (Esc lo usa antes de abrir el menú de pausa).</summary>
        public void Close()
        {
            if (_isOpen) SetOpen(false);
        }

        public void OpenWithContainer(LootContainer container)
        {
            _openContainer = container;
            if (container != null) container.RegisterChangeCallback(Redraw);
            SetOpen(true);
            PlayContainerOpenSounds(container);
        }

        /// <summary>Abrir un contenedor suena; si trae algo raro o épico, además un brillo.</summary>
        private void PlayContainerOpenSounds(LootContainer container)
        {
            var lib = Game.Presentation.Audio.GameAudio.Library;
            if (lib == null || container == null) return;
            Sfx(l => l.ContainerOpen);

            Rarity best = Rarity.Common;
            foreach (var stack in container.Contents)
            {
                if (stack.IsEmpty) continue;
                ItemSO def = _database.GetById(stack.ItemId);
                if (def != null && def.Rarity > best) best = def.Rarity;
            }
            if (best == Rarity.Epic) Sfx(l => l.EpicLoot);
            else if (best == Rarity.Rare) Sfx(l => l.RareLoot);
        }

        private void CloseContainer()
        {
            if (_openContainer != null) _openContainer.UnregisterChangeCallback(Redraw);
            _openContainer = null;
        }

        public void DisableInventory()
        {
            if (_isOpen) SetOpen(false);
            _toggleAction.Disable();
            enabled = false;
        }

        // ---------- Dibujo ----------

        private void Redraw()
        {
            if (!_isOpen) return;
            HideTooltip();
            DrawEquipment();
            DrawPockets();
            DrawContainer();
        }

        private void DrawEquipment()
        {
            _equipmentSlots.Clear();
            var equip = _inventory.Equipment;
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
                var slot = new VisualElement();
                slot.AddToClassList("equip-slot");
                if (isBag) slot.AddToClassList("equip-slot-bag");
                if (lastOfGroup) slot.AddToClassList("no-border");

                var label = new Label(((EquipmentSlot)i).DisplayName());
                label.AddToClassList("equip-slot-label");
                slot.Add(label);

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
                    name.AddToClassList(ItemTooltipFormatter.RarityClass(def));
                    itemWrap.Add(name);

                    slot.Add(itemWrap);

                    slot.RegisterCallback<PointerEnterEvent>(_ => ShowTooltip(slot, def));
                    slot.RegisterCallback<PointerLeaveEvent>(_ => HideTooltip());

                    slot.RegisterCallback<ClickEvent>(evt =>
                    {
                        if (_dragMoved) { _dragMoved = false; return; }
                        if (evt.ctrlKey) { Sfx(l => l.ItemDrop); DropItemServerRpc((int)SlotZone.Equipment, slotIndex); return; }
                        SfxEquip();
                        UnequipServerRpc(slotIndex);
                    });

                    slot.RegisterCallback<PointerDownEvent>(evt =>
                    {
                        if (evt.button != 0) return;
                        BeginDrag(SlotZone.Equipment, slotIndex, stack, evt.position);
                    });
                }

                slot.RegisterCallback<PointerUpEvent>(_ => TryDrop(SlotZone.Equipment, slotIndex));
                _equipmentSlots.Add(slot);
            }
        }

        private void DrawPockets()
        {
            DrawPocketGrid(_pocketLGrid, _pocketLLabel, "Pocket L", _inventory.PocketL, SlotZone.PocketL);
            DrawPocketGrid(_pocketRGrid, _pocketRLabel, "Pocket R", _inventory.PocketR, SlotZone.PocketR);
        }

        private void DrawPocketGrid(VisualElement grid, Label label, string title, IReadOnlyList<ItemStack> list, SlotZone zone)
        {
            if (grid == null) return;
            grid.Clear();
            if (label != null) label.text = $"{title} — {list.Count}/{MaxPocketSlots}";

            for (int i = 0; i < MaxPocketSlots; i++)
            {
                if (i < list.Count)
                {
                    int slotIndex = i;
                    ItemStack stack = list[i];
                    System.Action onClick = null;
                    if (!stack.IsEmpty && _database.GetById(stack.ItemId) is EquipmentItemSO)
                        onClick = () => { SfxEquip(); QuickEquipServerRpc((int)zone, slotIndex); };

                    grid.Add(BuildItemSlot(stack, zone, slotIndex, onClick));
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

        private void DrawContainer()
        {
            if (_openContainer == null)
            {
                if (_containerColumn != null) _containerColumn.style.display = DisplayStyle.None;
                return;
            }

            if (_containerColumn != null) _containerColumn.style.display = DisplayStyle.Flex;
            _containerGrid.Clear();

            var contents = _openContainer.Contents;
            int nonEmptyCount = 0;
            foreach (var s in contents) if (!s.IsEmpty) nonEmptyCount++;
            if (_containerLabel != null) _containerLabel.text = $"Loot Container — {nonEmptyCount} Items";

            for (int i = 0; i < contents.Count; i++)
            {
                int idx = i;
                _containerGrid.Add(BuildItemSlot(contents[i], SlotZone.Container, idx,
                    () => { Sfx(l => l.ItemPickup); TakeFromContainerServerRpc(_openContainer, idx); }));
            }
        }

        private VisualElement BuildItemSlot(ItemStack stack, SlotZone zone, int index, System.Action onClick)
        {
            var slot = new VisualElement();
            slot.AddToClassList("item-slot");

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
                    if (evt.ctrlKey)  { Sfx(l => l.ItemDrop); DropItemServerRpc((int)zone, index); return; }
                    if (evt.shiftKey)
                    {
                        if (zone == SlotZone.Container)
                        {
                            if (_openContainer != null) { SfxEquip(); QuickEquipFromContainerServerRpc(_openContainer, index); }
                        }
                        else
                        {
                            SfxEquip();
                            QuickEquipServerRpc((int)zone, index);
                        }
                        return;
                    }
                    onClick?.Invoke();
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

        /// <summary>Placeholders visuales (sin lógica todavía) para los 3 slots de usables.</summary>
        private void BuildUsableSlots()
        {
            if (_usableSlots == null) return;
            _usableSlots.Clear();
            for (int i = 0; i < 3; i++)
            {
                var slot = new VisualElement();
                slot.AddToClassList("usable-slot");

                var keyHint = new Label((i + 1).ToString());
                keyHint.AddToClassList("usable-key-hint");
                slot.Add(keyHint);

                _usableSlots.Add(slot);
            }
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
            _dragging  = new DragInfo { Zone = zone, Index = index, Stack = stack };
            _isDragging = true;
            _dragMoved  = false;
            _dragOutside = false;

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

            bool outside = _invPanel != null && !_invPanel.worldBound.Contains(evt.position);
            if (outside != _dragOutside)
            {
                _dragOutside = outside;
                _ghost.style.backgroundColor = outside ? GhostColorDrop : GhostColorNormal;
            }
        }

        private void MoveGhost(Vector2 pos)
        {
            if (_ghost == null) return;
            _ghost.style.left = pos.x - 30;
            _ghost.style.top  = pos.y - 30;
        }

        private void TryDrop(SlotZone destZone, int destIndex)
        {
            if (!_isDragging) return;

            var from  = _dragging;
            bool moved = _dragMoved;
            EndDrag();

            if (!moved) return;
            if (from.Zone == destZone && from.Index == destIndex) return; // soltado sobre sí mismo

            if (destZone == SlotZone.Container) return;

            if (from.Zone == SlotZone.Container)
            {
                if (_openContainer == null) return;
                Sfx(l => l.ItemMove);
                MoveWithContainerServerRpc((int)from.Zone, from.Index, (int)destZone, destIndex, _openContainer);
            }
            else
            {
                Sfx(l => l.ItemMove);
                MoveSlotServerRpc((int)from.Zone, from.Index, (int)destZone, destIndex);
            }
        }

        private void OnRootPointerUp(PointerUpEvent evt)
        {
            if (!_isDragging) return;

            var from  = _dragging;
            bool moved = _dragMoved;
            EndDrag();

            if (!moved) return; // clic suelto fuera de slot, no arrastre

            if (from.Zone == SlotZone.Container)
            {
                if (_openContainer != null)
                {
                    Sfx(l => l.ItemDrop);
                    DropContainerItemToWorldServerRpc(_openContainer, from.Index);
                }
                return;
            }

            Sfx(l => l.ItemDrop);
            DropToWorldServerRpc((int)from.Zone, from.Index);
        }

        private void CancelDrag() => EndDrag();

        private void EndDrag()
        {
            _isDragging  = false;
            _dragOutside = false;
            _root.UnregisterCallback<PointerMoveEvent>(OnDragMove);
            if (_ghost != null) { _ghost.RemoveFromHierarchy(); _ghost = null; }
        }

        // ---------- Acciones (server-authoritative) ----------
        // Desactivar este componente al morir/extraer no bloquea los ServerRpc: cada uno valida
        // que el jugador siga en la run y, si toca un contenedor, que lo tenga al alcance.

        private bool ServerCanAct() => _avatar == null || !_avatar.IsControlDisabled;

        private bool ServerCanUseContainer(LootContainer container)
            => container != null && container.IsSpawned
               && _interaction != null && _interaction.ServerCanReach(container.transform.position);

        [ServerRpc]
        private void UnequipServerRpc(int equipmentSlotIndex)
        {
            if (!ServerCanAct()) return;
            _inventory.TryUnequip(equipmentSlotIndex);
        }

        [ServerRpc]
        private void TakeFromContainerServerRpc(LootContainer container, int index)
        {
            if (!ServerCanUseContainer(container)) return;
            if (!container.ServerTryTake(index, out ItemStack taken)) return;

            int notAdded = _inventory.TryAddStack(taken);
            if (notAdded > 0)
                container.ServerFill(new[] { new ItemStack(taken.ItemId, notAdded, taken.Durability) });


        }

        [ServerRpc]
        private void MoveSlotServerRpc(int fromZone, int fromIndex, int toZone, int toIndex)
        {
            if (!ServerCanAct()) return;
            _inventory.TryMoveSlot(fromZone, fromIndex, toZone, toIndex);
        }

        [ServerRpc]
        private void MoveWithContainerServerRpc(int fromZone, int fromIndex, int toZone, int toIndex, LootContainer container)
        {
            if (!ServerCanUseContainer(container)) return;
            _inventory.TryMoveWithContainer(fromZone, fromIndex, toZone, toIndex, container);
        }

        [ServerRpc]
        private void DropToWorldServerRpc(int zone, int index)
        {
            if (!ServerCanAct()) return;
            _inventory.TryDropToWorld(zone, index, transform.position);
        }

        [ServerRpc]
        private void DropContainerItemToWorldServerRpc(LootContainer container, int index)
        {
            if (!ServerCanUseContainer(container)) return;
            if (!container.ServerTryTake(index, out ItemStack taken)) return;
            _inventory.SpawnWorldItemPublic(taken, transform.position);
        }

        [ServerRpc]
        private void DropItemServerRpc(int zone, int index)
        {
            if (!ServerCanAct()) return;
            _inventory.TryDropToWorld(zone, index, transform.position);
        }

        [ServerRpc]
        private void QuickEquipFromContainerServerRpc(LootContainer container, int index)
        {
            if (!ServerCanUseContainer(container)) return;
            _inventory.TryEquipFromContainer(container, index);
        }

        [ServerRpc]
        private void SortPocketsServerRpc(int mode)
        {
            if (!ServerCanAct()) return;
            if (mode != (int)ItemSortMode.Type && mode != (int)ItemSortMode.Rarity) return;
            _inventory.TrySortPockets((ItemSortMode)mode);
        }

        [ServerRpc]
        private void QuickEquipServerRpc(int zone, int index)
        {
            if (!ServerCanAct()) return;
            _inventory.TryEquipFromInventory(zone, index);
        }
    }
}