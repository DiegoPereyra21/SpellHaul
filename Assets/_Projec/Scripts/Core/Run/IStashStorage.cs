using System.Threading.Tasks;
using Game.Core.Items;

namespace Game.Core.Run
{
    /// <summary>
    /// Almacén persistente del stash del jugador (fuera de la run). Contrato: la implementación
    /// real (local antes del login, PlayFab después) se enchufa sin tocar a quien lo consume.
    /// </summary>
    public interface IStashStorage
    {
        /// <summary>Carga el stash persistido. Null si nunca se guardó (primera vez).</summary>
        Task<StashData> LoadAsync();

        /// <summary>Persiste el estado completo del stash.</summary>
        Task SaveAsync(StashData stash);
    }
}