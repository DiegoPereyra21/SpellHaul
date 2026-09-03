using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace Game.Core.Items
{
    /// <summary>
    /// Item equipable: ocupa un slot de equipo y aporta modificadores de stat mientras está puesto.
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Items/Equipment", fileName = "Equip_")]
    public class EquipmentItemSO : ItemSO
    {
        [Header("Equipamiento")]
        [SerializeField] private EquipmentSlot _slot;
        [SerializeField] private List<StatModifier> _modifiers = new List<StatModifier>();

        [Header("Pocket (solo si el slot es PocketL o PocketR)")]
        [Tooltip("Cuántos slots aporta este pocket. Ambos lados (L/R) aceptan el mismo tipo de ítem.")]
        [FormerlySerializedAs("_backpackSlots")]
        [SerializeField, Min(0)] private int _pocketSlots = 0;

        public EquipmentSlot Slot => _slot;
        public IReadOnlyList<StatModifier> Modifiers => _modifiers;

        /// <summary>Cuántos slots aporta (solo relevante si Slot es PocketL/PocketR).</summary>
        public int PocketSlots => _pocketSlots;

        public override bool IsEquipment => true;

        private static ItemCategory ExpectedCategoryFor(EquipmentSlot slot)
        {
            // Boots/Hat/Robe/Pockets comparten la categoría genérica Equipment; Glove es
            // la única excepción con categoría propia.
            return slot == EquipmentSlot.Glove ? ItemCategory.Glove : ItemCategory.Equipment;
        }

        private void OnValidate()
        {
            ItemCategory expected = ExpectedCategoryFor(_slot);
            if (Category != expected)
                Debug.LogWarning($"[{name}] Slot={_slot} normalmente va con Category={expected}, pero tiene Category={Category}. Revisar si es a propósito.", this);
        }
    }
}