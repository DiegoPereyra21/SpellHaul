namespace Game.Core.Run
{
    /// <summary>
    /// Contrato del inventario de run (lo implementa RunInventory). Extracción y muerte lo
    /// invocan sin acoplarse al inventario concreto.
    /// </summary>
    public interface IRunInventory
    {
        /// <summary>Extracción exitosa: el inventario de la run pasa a ser el inventario propio
        /// persistente (loadout) del jugador. No toca el stash, pese al nombre.</summary>
        void CommitToStash();

        /// <summary>Suelta / pierde el loot de la run (muerte).</summary>
        void DropAll();
    }
}