using System;
using FishNet.Serializing;

namespace Game.Core.Items
{
    /// <summary>
    /// Estado de una instancia de item, apto para viajar por red (struct liviano, sin
    /// referencias a ScriptableObject). Por la red viaja el id del item (string) + estado
    /// (cantidad, durabilidad); el cliente reconstruye la definición desde el ItemDatabase.
    /// Un stack vacío se representa con ItemId nulo/vacío.
    /// </summary>
    [Serializable]
    public struct ItemStack
    {
        public string ItemId;
        public int Quantity;
        public float Durability;

        public bool IsEmpty => string.IsNullOrEmpty(ItemId) || Quantity <= 0;

        public static ItemStack Empty => new ItemStack { ItemId = null, Quantity = 0, Durability = 0f };

        public ItemStack(string itemId, int quantity, float durability = 1f)
        {
            ItemId = itemId;
            Quantity = quantity;
            Durability = durability;
        }
    }
}