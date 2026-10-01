using UnityEngine;

namespace Game.Core.Items
{
    /// <summary>
    /// Definición base de un item (compartida entre todas sus instancias). El estado único
    /// de cada item concreto (cantidad, durabilidad) vive en ItemStack, no acá.
    /// </summary>
    
    [CreateAssetMenu(menuName = "Game/Items/Basic Item", fileName = "Item_")]
    public class ItemSO : ScriptableObject
    {
        [Header("Identidad")]
        [SerializeField] private string _itemId;
        [SerializeField] private string _displayName;
        [SerializeField] private ItemCategory _category = ItemCategory.Misc;
        [SerializeField] private Rarity _rarity = Rarity.Common;
        [SerializeField] private Sprite _icon;
        [SerializeField, TextArea] private string _description;

        [Header("Apilamiento")]
        [SerializeField] private bool _isStackable = false;
        [SerializeField, Min(1)] private int _maxStack = 1;
        
        [Header("Economía")]
        [Tooltip("Valor en oro (el vendedor paga una fracción). 0 = automático según rareza y categoría (EconomyConfig).")]
        [SerializeField, Min(0)] private int _baseValue = 0;

        [Tooltip("Dónde se consigue (se ve en el tooltip). Vacío = automático (loot / crafteo / vendedor).")]
        [SerializeField] private string _obtainHint;

        [Header("Mundo")]
        [Tooltip("Prefab 3D que aparece en el suelo al dropearse. Si está vacío usa el prefab genérico.")]
        [SerializeField] public GameObject WorldPrefab;

        public string ItemId => _itemId;
        public string DisplayName => _displayName;
        public Sprite Icon => _icon;
        public string Description => _description;
        public bool IsStackable => _isStackable;
        public int MaxStack => _isStackable ? Mathf.Max(1, _maxStack) : 1;
        public ItemCategory Category => _category;
        public Rarity Rarity => _rarity;
        public int BaseValue => _baseValue;
        public string ObtainHint => _obtainHint;
        /// <summary>True si este item se puede equipar (los EquipmentItemSO lo sobrescriben).</summary>
        public virtual bool IsEquipment => false;
    }
}