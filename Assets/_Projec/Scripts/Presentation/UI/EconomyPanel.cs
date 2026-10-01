using System;
using System.Collections.Generic;
using Game.Core.Economy;
using Game.Core.Items;
using Game.Presentation.Audio;
using Game.Presentation.Run;
using UnityEngine.UIElements;

namespace Game.Presentation.UI
{
    /// <summary>
    /// Columna derecha de la pantalla del Stash con tres pestañas: Stash (la grilla de siempre),
    /// Crafting (recetas de EconomyConfig, con lo que tenés en el stash) y Trader (ofertas del
    /// vendedor y venta de items del stash). El oro se muestra siempre arriba.
    ///
    /// Nada se resuelve acá: cada acción va a EconomyService → CloudScript, que valida contra
    /// PlayFab y después se relee el stash y el oro. Lo usa StashScreenController (no es un
    /// componente de escena).
    /// </summary>
    public class EconomyPanel
    {
        public enum Tab { Stash, Crafting, Trader }

        private readonly VisualElement _stashView;
        private readonly ScrollView _craftingView;
        private readonly VisualElement _traderView;
        private readonly VisualElement _traderOffers;
        private readonly VisualElement _traderSellGrid;
        private readonly Label _goldLabel;
        private readonly Label _status;
        private readonly Dictionary<Tab, Button> _tabButtons = new();

        private readonly ItemDatabase _database;
        private readonly Func<StashData> _stash;
        private readonly Action<VisualElement, ItemSO> _showTooltip;
        private readonly Action _hideTooltip;
        private readonly Action _onStashChanged;

        private EconomyConfigSO _config;
        private Tab _tab = Tab.Stash;
        private int _armedSellIndex = -1; // primer clic para vender: el segundo confirma

        public Tab Current => _tab;

        public EconomyPanel(VisualElement root, ItemDatabase database, Func<StashData> stash,
            Action<VisualElement, ItemSO> showTooltip, Action hideTooltip, Action onStashChanged)
        {
            _database = database;
            _stash = stash;
            _showTooltip = showTooltip;
            _hideTooltip = hideTooltip;
            _onStashChanged = onStashChanged;

            _stashView = root.Q<VisualElement>("stash-view");
            _craftingView = root.Q<ScrollView>("crafting-view");
            _traderView = root.Q<VisualElement>("trader-view");
            _traderOffers = root.Q<VisualElement>("trader-offers");
            _traderSellGrid = root.Q<VisualElement>("trader-sell-grid");
            _goldLabel = root.Q<Label>("gold-label");
            _status = root.Q<Label>("economy-status");

            Bind(root, "tab-stash", Tab.Stash);
            Bind(root, "tab-crafting", Tab.Crafting);
            Bind(root, "tab-trader", Tab.Trader);

            EconomyService.OnChanged += HandleEconomyChanged;
        }

        public void Dispose() => EconomyService.OnChanged -= HandleEconomyChanged;

        private void Bind(VisualElement root, string name, Tab tab)
        {
            var b = root.Q<Button>(name);
            if (b == null) return;
            _tabButtons[tab] = b;
            b.clicked += () => Select(tab);
        }

        /// <summary>Al abrir la pantalla: refresca el oro y vuelve a la pestaña del stash.</summary>
        public void OnShown()
        {
            _config = EconomyConfigSO.Load();
            SetStatus(null);
            Select(Tab.Stash);
            _ = EconomyService.ReloadGoldAsync();
        }

        public void Select(Tab tab)
        {
            _tab = tab;
            _armedSellIndex = -1;
            foreach (var kv in _tabButtons) kv.Value.EnableInClassList("tab-active", kv.Key == tab);
            if (_stashView != null) _stashView.style.display = tab == Tab.Stash ? DisplayStyle.Flex : DisplayStyle.None;
            if (_craftingView != null) _craftingView.style.display = tab == Tab.Crafting ? DisplayStyle.Flex : DisplayStyle.None;
            if (_traderView != null) _traderView.style.display = tab == Tab.Trader ? DisplayStyle.Flex : DisplayStyle.None;
            Redraw();
        }

        private void HandleEconomyChanged()
        {
            _onStashChanged?.Invoke(); // redibuja toda la pantalla (incluye esta columna)
        }

        /// <summary>Redibuja oro y la pestaña activa (Crafting/Trader dependen del stash).</summary>
        public void Redraw()
        {
            if (_goldLabel != null)
                _goldLabel.text = EconomyService.GoldLoaded ? $"{EconomyService.Gold} Gold" : "— Gold";

            if (_tab == Tab.Crafting) DrawCrafting();
            else if (_tab == Tab.Trader) DrawTrader();
        }

        private void SetStatus(string message, bool error = true)
        {
            if (_status == null) return;
            _status.text = message ?? "";
            _status.EnableInClassList("economy-status--ok", !error);
        }

        private bool Ready(out string reason)
        {
            reason = null;
            if (_config == null) reason = "Crafting and trading are not set up yet.";
            else if (!EconomyService.Available) reason = "Not connected to the game service.";
            return reason == null;
        }

        // ---------- Crafting ----------

        private void DrawCrafting()
        {
            if (_craftingView == null) return;
            _craftingView.Clear();
            if (!Ready(out string reason)) { _craftingView.Add(Note(reason)); return; }

            var stash = _stash();
            foreach (var recipe in _config.Recipes)
            {
                if (recipe.Output == null) continue;
                _craftingView.Add(BuildRecipeRow(recipe, stash));
            }
        }

        private VisualElement BuildRecipeRow(EconomyConfigSO.Recipe recipe, StashData stash)
        {
            var row = new VisualElement();
            row.AddToClassList("recipe-row");

            var info = new VisualElement();
            info.AddToClassList("recipe-info");

            string qty = recipe.OutputQuantity > 1 ? $" x{recipe.OutputQuantity}" : "";
            var name = new Label(recipe.Output.DisplayName + qty);
            name.AddToClassList("recipe-name");
            name.AddToClassList(ItemTooltipFormatter.RarityClass(recipe.Output));
            info.Add(name);
            name.RegisterCallback<PointerEnterEvent>(_ => _showTooltip(name, recipe.Output));
            name.RegisterCallback<PointerLeaveEvent>(_ => _hideTooltip());

            bool canCraft = true;
            var inputs = new VisualElement();
            inputs.AddToClassList("recipe-inputs");
            foreach (var input in recipe.Inputs)
            {
                if (input.Item == null) continue;
                int have = Count(stash, input.Item.ItemId);
                bool enough = have >= input.Quantity;
                canCraft &= enough;
                var l = new Label($"{input.Item.DisplayName} {have}/{input.Quantity}");
                l.AddToClassList("recipe-input");
                l.AddToClassList(enough ? "recipe-input--ok" : "recipe-input--missing");
                inputs.Add(l);
            }
            if (recipe.Gold > 0)
            {
                bool enoughGold = EconomyService.Gold >= recipe.Gold;
                canCraft &= enoughGold;
                var g = new Label($"{recipe.Gold} Gold");
                g.AddToClassList("recipe-input");
                g.AddToClassList(enoughGold ? "recipe-input--ok" : "recipe-input--missing");
                inputs.Add(g);
            }
            info.Add(inputs);
            row.Add(info);

            var button = new Button { text = "Craft" };
            button.AddToClassList("sort-btn");
            button.AddToClassList("economy-btn");
            button.SetEnabled(canCraft && !EconomyService.Busy);
            button.clicked += async () =>
            {
                SetStatus("Crafting...", false);
                button.SetEnabled(false);
                string error = await EconomyService.CraftAsync(recipe.Id);
                if (error == null) GameAudio.Ui(l => l.MatchFound != null ? l.MatchFound : l.UiNotice);
                else GameAudio.Ui(l => l.UiError, 0.6f);
                SetStatus(error ?? $"Crafted {recipe.Output.DisplayName}.", error != null);
            };
            row.Add(button);
            return row;
        }

        // ---------- Trader ----------

        private void DrawTrader()
        {
            if (_traderOffers == null || _traderSellGrid == null) return;
            _traderOffers.Clear();
            _traderSellGrid.Clear();
            if (!Ready(out string reason)) { _traderOffers.Add(Note(reason)); return; }

            foreach (var offer in _config.Offers)
            {
                if (offer.Item == null) continue;
                _traderOffers.Add(BuildOfferRow(offer));
            }

            var stash = _stash();
            for (int i = 0; i < stash.Slots.Count; i++)
                _traderSellGrid.Add(BuildSellSlot(stash.Slots[i], i));
        }

        private VisualElement BuildOfferRow(EconomyConfigSO.Offer offer)
        {
            var row = new VisualElement();
            row.AddToClassList("recipe-row");

            string qty = offer.Quantity > 1 ? $" x{offer.Quantity}" : "";
            var name = new Label(offer.Item.DisplayName + qty);
            name.AddToClassList("recipe-name");
            name.AddToClassList(ItemTooltipFormatter.RarityClass(offer.Item));
            name.RegisterCallback<PointerEnterEvent>(_ => _showTooltip(name, offer.Item));
            name.RegisterCallback<PointerLeaveEvent>(_ => _hideTooltip());
            row.Add(name);

            var button = new Button { text = $"Buy · {offer.Price} Gold" };
            button.AddToClassList("sort-btn");
            button.AddToClassList("economy-btn");
            button.SetEnabled(EconomyService.Gold >= offer.Price && !EconomyService.Busy);
            button.clicked += async () =>
            {
                SetStatus("Buying...", false);
                button.SetEnabled(false);
                string error = await EconomyService.BuyAsync(offer.Id);
                if (error == null) GameAudio.Ui(l => l.MatchFound != null ? l.MatchFound : l.UiNotice);
                else GameAudio.Ui(l => l.UiError, 0.6f);
                SetStatus(error ?? $"Bought {offer.Item.DisplayName}.", error != null);
            };
            row.Add(button);
            return row;
        }

        /// <summary>Casilla del stash en modo venta: muestra lo que paga el vendedor. Primer clic
        /// la marca ("Sell?"), el segundo vende el stack entero.</summary>
        private VisualElement BuildSellSlot(ItemStack stack, int index)
        {
            var slot = new VisualElement();
            slot.AddToClassList("item-slot");
            slot.AddToClassList("sell-slot");
            if (stack.IsEmpty) return slot;

            ItemSO def = _database.GetById(stack.ItemId);
            slot.AddToClassList(ItemTooltipFormatter.RarityClass(def));
            var name = new Label(def != null ? def.DisplayName : stack.ItemId);
            name.AddToClassList("item-name");
            slot.Add(name);

            int total = _config.SellPriceOf(def) * stack.Quantity;
            bool armed = index == _armedSellIndex;
            var price = new Label(armed ? "Sell?" : $"{total}g");
            price.AddToClassList("sell-price");
            slot.Add(price);
            if (armed) slot.AddToClassList("sell-slot--armed");

            slot.RegisterCallback<PointerEnterEvent>(_ => _showTooltip(slot, def));
            slot.RegisterCallback<PointerLeaveEvent>(_ => _hideTooltip());
            slot.RegisterCallback<ClickEvent>(async _ =>
            {
                if (EconomyService.Busy || def == null) return;
                if (_armedSellIndex != index)
                {
                    _armedSellIndex = index;
                    SetStatus($"Click again to sell {def.DisplayName}{(stack.Quantity > 1 ? $" x{stack.Quantity}" : "")} for {total} gold.", false);
                    DrawTrader();
                    return;
                }
                _armedSellIndex = -1;
                _hideTooltip();
                SetStatus("Selling...", false);
                string error = await EconomyService.SellAsync(index, stack.ItemId);
                if (error == null) GameAudio.Ui(l => l.MatchFound != null ? l.MatchFound : l.UiNotice);
                else GameAudio.Ui(l => l.UiError, 0.6f);
                SetStatus(error ?? $"Sold for {total} gold.", error != null);
            });
            return slot;
        }

        // ---------- Utilidades ----------

        private static int Count(StashData stash, string itemId)
        {
            int n = 0;
            if (stash == null) return 0;
            foreach (var s in stash.Slots)
                if (!s.IsEmpty && s.ItemId == itemId) n += s.Quantity;
            return n;
        }

        private static Label Note(string text)
        {
            var l = new Label(text);
            l.AddToClassList("economy-note");
            return l;
        }
    }
}
