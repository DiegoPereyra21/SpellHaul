namespace Game.Core.Items
{
    public enum EquipmentSlot
    {
        Boots,
        Hat,
        Robe,
        Glove,
        PocketL,
        PocketR
    }

    /// <summary>
    /// PocketL y PocketR aceptan el mismo tipo de ítem (una bolsa cualquiera va en cualquiera
    /// de los dos lados) — este helper es la única razón por la que existe este archivo aparte.
    /// </summary>
    public static class EquipmentSlotExtensions
    {
        public static bool IsPocket(this EquipmentSlot slot)
            => slot == EquipmentSlot.PocketL || slot == EquipmentSlot.PocketR;
    }
}