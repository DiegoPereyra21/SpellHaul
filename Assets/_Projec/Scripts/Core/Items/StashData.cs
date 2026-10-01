using System.Collections.Generic;

namespace Game.Core.Items
{
    /// <summary>
    /// El stash como inventario con slots fijos. Datos planos (ItemStack por slot), persistente
    /// entre runs. Vive en el menú (sin red). Reemplaza el InMemoryStash de solo-acumular.
    /// </summary>
    [System.Serializable]
    public class StashData
    {
        public const int SlotCount = 42; // antes 30: los stashes guardados se completan al leer (EnsureSlots)

        public List<ItemStack> Slots = new List<ItemStack>();

        public StashData()
        {
            for (int i = 0; i < SlotCount; i++)
                Slots.Add(ItemStack.Empty);
        }

        /// <summary>Deposita un stack en el primer slot libre (o apila si hay lugar). Devuelve lo que no entró.</summary>
        public int Add(ItemStack stack, System.Func<string, ItemSO> resolve)
        {
            if (stack.IsEmpty) return 0;
            ItemSO def = resolve(stack.ItemId);
            int remaining = stack.Quantity;

            // Apilar en pilas existentes si es apilable.
            if (def != null && def.IsStackable)
            {
                for (int i = 0; i < Slots.Count && remaining > 0; i++)
                {
                    if (Slots[i].IsEmpty || Slots[i].ItemId != stack.ItemId) continue;
                    int space = def.MaxStack - Slots[i].Quantity;
                    if (space <= 0) continue;
                    int add = System.Math.Min(space, remaining);
                    var s = Slots[i]; s.Quantity += add; Slots[i] = s;
                    remaining -= add;
                }
            }

            // Ocupar slots libres.
            for (int i = 0; i < Slots.Count && remaining > 0; i++)
            {
                if (!Slots[i].IsEmpty) continue;
                int add = (def != null && def.IsStackable) ? System.Math.Min(def.MaxStack, remaining) : 1;
                Slots[i] = new ItemStack(stack.ItemId, add, stack.Durability);
                remaining -= add;
            }

            return remaining;
        }

        /// <summary>Completa con slots vacíos un stash guardado con menos slots (ej. cuando eran 30).</summary>
        public void EnsureSlots()
        {
            Slots ??= new List<ItemStack>();
            while (Slots.Count < SlotCount) Slots.Add(ItemStack.Empty);
        }

        public ItemStack TakeAt(int index)
        {
            if (index < 0 || index >= Slots.Count) return ItemStack.Empty;
            var s = Slots[index];
            Slots[index] = ItemStack.Empty;
            return s;
        }
    }
}