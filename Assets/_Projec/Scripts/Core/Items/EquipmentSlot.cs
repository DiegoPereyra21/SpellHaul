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
    /// de los dos lados). También define el orden en que se MUESTRAN los slots (de la cabeza a
    /// los pies y después los pockets), que no es el del enum: el enum no se reordena porque su
    /// valor numérico es el índice guardado en los loadouts.
    /// </summary>
    public static class EquipmentSlotExtensions
    {
        public static bool IsPocket(this EquipmentSlot slot)
            => slot == EquipmentSlot.PocketL || slot == EquipmentSlot.PocketR;

        /// <summary>Orden de arriba a abajo en las pantallas de equipo (y del orden "por tipo").</summary>
        public static readonly EquipmentSlot[] DisplayOrder =
        {
            EquipmentSlot.Hat,
            EquipmentSlot.Robe,
            EquipmentSlot.Glove,
            EquipmentSlot.Boots,
            EquipmentSlot.PocketL,
            EquipmentSlot.PocketR,
        };

        /// <summary>Posición del slot en DisplayOrder. Un slot nuevo que no esté en la lista va al final.</summary>
        public static int DisplayRank(this EquipmentSlot slot)
        {
            int i = System.Array.IndexOf(DisplayOrder, slot);
            return i >= 0 ? i : DisplayOrder.Length + (int)slot;
        }

        /// <summary>Índices de slot (del loadout) en orden de pantalla; incluye los que no estén en DisplayOrder.</summary>
        public static System.Collections.Generic.List<int> DisplayIndices(int slotCount)
        {
            var list = new System.Collections.Generic.List<int>(slotCount);
            foreach (var s in DisplayOrder)
                if ((int)s < slotCount) list.Add((int)s);
            for (int i = 0; i < slotCount; i++)
                if (!list.Contains(i)) list.Add(i);
            return list;
        }

        /// <summary>Nombre para mostrar del slot en la lista de equipo.</summary>
        public static string DisplayName(this EquipmentSlot slot) => slot switch
        {
            EquipmentSlot.Glove => "Gloves",
            EquipmentSlot.PocketL => "Pocket L",
            EquipmentSlot.PocketR => "Pocket R",
            _ => slot.ToString(),
        };
    }
}