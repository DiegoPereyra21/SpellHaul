using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Game.Core.Items;
using Game.Core.Run;
using UnityEngine;

namespace Game.Presentation.Run
{
    /// <summary>
    /// IStashStorage respaldado por PlayFab Player Data de solo lectura (por jugador). Requiere sesión
    /// activa; ver PlayFabSession.
    /// </summary>
    public class PlayFabStashStorage : IStashStorage
    {
        public const string DataKey = "Stash";

        public async Task<StashData> LoadAsync()
        {
            var values = await PlayFabUserData.ReadAsync(DataKey);
            return values.TryGetValue(DataKey, out string json) ? JsonUtility.FromJson<StashData>(json) : null;
        }

        /// <summary>Escritura validada por CloudScript (el cliente no puede escribir la clave directo).</summary>
        public Task SaveAsync(StashData stash)
            => PlayFabUserData.CallAsync(PlayFabUserData.CommitProfileFunction,
                new Dictionary<string, string> { { "Stash", JsonUtility.ToJson(stash) } });
    }
}