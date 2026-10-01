using System;
using System.Collections.Generic;
using Game.Core.Items;
using UnityEngine;

namespace Game.Core.Economy
{
    /// <summary>
    /// Toda la economía del hub en un solo asset: valor de los items (para venderlos), recetas de
    /// crafteo y ofertas del vendedor. Lo usa el cliente para MOSTRAR (lista de recetas, precios);
    /// quien decide es CloudScript, que lee una copia exportada a Title Data ("Economy", menú
    /// Game/Economy/Copy Economy JSON). Cambiar este asset sin volver a exportarlo deja al menú
    /// mostrando algo que el servidor no acepta.
    ///
    /// Va en Resources/EconomyConfig (lo carga el menú sin referencias en escena).
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Economy/Economy Config", fileName = "EconomyConfig")]
    public class EconomyConfigSO : ScriptableObject
    {
        public const string ResourcePath = "EconomyConfig";

        [Serializable]
        public struct Ingredient
        {
            public ItemSO Item;
            [Min(1)] public int Quantity;
        }

        [Serializable]
        public class Recipe
        {
            [Tooltip("Identificador único (lo manda el cliente; no cambiarlo una vez publicado).")]
            public string Id;
            public ItemSO Output;
            [Min(1)] public int OutputQuantity = 1;
            public List<Ingredient> Inputs = new List<Ingredient>();
            [Min(0)] public int Gold;
        }

        [Serializable]
        public class Offer
        {
            [Tooltip("Identificador único (lo manda el cliente; no cambiarlo una vez publicado).")]
            public string Id;
            public ItemSO Item;
            [Min(1)] public int Quantity = 1;
            [Min(1)] public int Price = 10;
        }

        [Header("Venta")]
        [Tooltip("Fracción del valor que paga el vendedor al comprarte un item.")]
        [SerializeField, Range(0.05f, 1f)] private float _sellRate = 0.4f;

        [Header("Valor automático por rareza (si el item no define uno propio)")]
        [SerializeField] private int _valueCommon = 20;
        [SerializeField] private int _valueRare = 60;
        [SerializeField] private int _valueEpic = 180;
        [Tooltip("Multiplicador del valor para materiales (Material) y recursos (Resource).")]
        [SerializeField] private float _materialMultiplier = 0.25f;
        [SerializeField] private float _resourceMultiplier = 0.5f;
        [Tooltip("Multiplicador del valor para consumibles.")]
        [SerializeField] private float _consumableMultiplier = 0.6f;

        [Header("Crafteo")]
        [SerializeField] private List<Recipe> _recipes = new List<Recipe>();

        [Header("Vendedor")]
        [SerializeField] private List<Offer> _offers = new List<Offer>();

        public float SellRate => _sellRate;
        public IReadOnlyList<Recipe> Recipes => _recipes;
        public IReadOnlyList<Offer> Offers => _offers;

        /// <summary>Valor base del item: el propio (ItemSO.BaseValue) o el automático por rareza y categoría.</summary>
        public int ValueOf(ItemSO item)
        {
            if (item == null) return 0;
            if (item.BaseValue > 0) return item.BaseValue;

            float v = item.Rarity switch
            {
                Rarity.Rare => _valueRare,
                Rarity.Epic => _valueEpic,
                _ => _valueCommon
            };
            v *= item.Category switch
            {
                ItemCategory.Material => _materialMultiplier,
                ItemCategory.Resource => _resourceMultiplier,
                ItemCategory.Consumable => _consumableMultiplier,
                _ => 1f
            };
            return Mathf.Max(1, Mathf.RoundToInt(v));
        }

        /// <summary>Lo que paga el vendedor por UNA unidad (mismo cálculo que CloudScript).</summary>
        public int SellPriceOf(ItemSO item) => item == null ? 0 : Mathf.Max(1, Mathf.FloorToInt(ValueOf(item) * _sellRate));

        public Recipe FindRecipe(string id) => _recipes.Find(r => r.Id == id);
        public Offer FindOffer(string id) => _offers.Find(o => o.Id == id);

        private static EconomyConfigSO _cached;

        /// <summary>El asset de Resources (null si todavía no se generó).</summary>
        public static EconomyConfigSO Load()
        {
            if (_cached == null) _cached = Resources.Load<EconomyConfigSO>(ResourcePath);
            return _cached;
        }
    }
}
