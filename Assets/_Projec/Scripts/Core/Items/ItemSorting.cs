using System;
using System.Collections.Generic;

namespace Game.Core.Items
{
    /// <summary>Criterio de ordenamiento de inventario. Valores explícitos: se guarda en PlayerPrefs.</summary>
    public enum ItemSortMode
    {
        Type = 0,
        Rarity = 1,
    }

    /// <summary>
    /// Ordenamiento de inventarios (Stash en el menú, pockets en la run): mismas reglas en los dos.
    /// - Por tipo: equipo en el orden de la lista de equipo (sombrero → túnica → guantes → botas →
    ///   bolsas), después el resto por categoría; dentro de cada tipo, rareza mayor primero.
    /// - Por rareza: primero los equipables y después el resto (sin mezclarse); dentro de cada grupo
    ///   épico → raro → común, y dentro de cada rareza, por tipo.
    /// Empate: nombre y después cantidad (mayor primero).
    /// </summary>
    public static class ItemSorting
    {
        /// <summary>
        /// Junta pilas incompletas del mismo item (misma durabilidad) y devuelve la lista ordenada,
        /// sin vacíos. Nunca crea ni pierde unidades.
        /// </summary>
        public static List<ItemStack> MergeAndSort(IEnumerable<ItemStack> stacks, Func<string, ItemSO> resolve, ItemSortMode mode)
        {
            var items = new List<ItemStack>();
            foreach (var s in stacks)
                if (!s.IsEmpty) AddMerged(items, s, resolve(s.ItemId));

            items.Sort((a, b) => Compare(a, resolve(a.ItemId), b, resolve(b.ItemId), mode));
            return items;
        }

        private static void AddMerged(List<ItemStack> items, ItemStack stack, ItemSO def)
        {
            int remaining = stack.Quantity;
            if (def != null && def.IsStackable)
            {
                for (int i = 0; i < items.Count && remaining > 0; i++)
                {
                    var s = items[i];
                    if (s.ItemId != stack.ItemId || Math.Abs(s.Durability - stack.Durability) > 0.0001f) continue;
                    int add = Math.Min(def.MaxStack - s.Quantity, remaining);
                    if (add <= 0) continue;
                    items[i] = new ItemStack(s.ItemId, s.Quantity + add, s.Durability);
                    remaining -= add;
                }
            }
            if (remaining > 0) items.Add(new ItemStack(stack.ItemId, remaining, stack.Durability));
        }

        public static int Compare(ItemStack a, ItemSO da, ItemStack b, ItemSO db, ItemSortMode mode)
        {
            int byType = TypeRank(da).CompareTo(TypeRank(db));
            int byRare = RarityRank(db).CompareTo(RarityRank(da)); // mayor primero

            if (mode == ItemSortMode.Rarity)
            {
                int group = (da is EquipmentItemSO ? 0 : 1).CompareTo(db is EquipmentItemSO ? 0 : 1);
                if (group != 0) return group;
                if (byRare != 0) return byRare;
                if (byType != 0) return byType;
            }
            else
            {
                if (byType != 0) return byType;
                if (byRare != 0) return byRare;
            }

            int byName = string.CompareOrdinal(da != null ? da.DisplayName : a.ItemId, db != null ? db.DisplayName : b.ItemId);
            return byName != 0 ? byName : b.Quantity.CompareTo(a.Quantity);
        }

        /// <summary>Equipo primero, en el orden de la lista de equipo; después el resto por categoría.</summary>
        public static int TypeRank(ItemSO def)
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
    }
}
