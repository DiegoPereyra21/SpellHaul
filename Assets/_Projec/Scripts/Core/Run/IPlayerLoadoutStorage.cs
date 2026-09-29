using System.Threading.Tasks;
using Game.Core.Items;

namespace Game.Core.Run
{
    /// <summary>
    /// Almacén persistente del inventario propio del jugador (equipo + pockets), fuera de la
    /// run. Contrato: la implementación real (local antes del login, PlayFab después) se enchufa sin
    /// tocar a quien lo consume.
    /// </summary>
    public interface IPlayerLoadoutStorage
    {
        /// <summary>Carga el snapshot persistido. Null si nunca se guardó (jugador nuevo).</summary>
        Task<InventorySnapshot> LoadAsync();

        /// <summary>Persiste el snapshot completo.</summary>
        Task SaveAsync(InventorySnapshot snapshot);
    }
}