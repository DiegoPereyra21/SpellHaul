using System.Collections.Generic;

namespace Game.Core.Items
{
    /// <summary>
    /// "Foto" del inventario propio del jugador: equipamiento por slot + items de cada pocket.
    /// Es lo que persiste entre runs y se restaura al entrar. Listas planas de ItemStack,
    /// listas para serializar (se guarda en PlayFab Player Data).
    /// </summary>
    [System.Serializable]
    public class InventorySnapshot
    {
        public List<ItemStack> Equipment = new List<ItemStack>();
        public List<ItemStack> PocketL = new List<ItemStack>();
        public List<ItemStack> PocketR = new List<ItemStack>();

        public bool IsEmpty => Equipment.Count == 0 && PocketL.Count == 0 && PocketR.Count == 0;
    }
}