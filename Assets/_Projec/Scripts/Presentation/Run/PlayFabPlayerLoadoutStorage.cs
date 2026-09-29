using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Game.Core.Items;
using Game.Core.Run;
using UnityEngine;

namespace Game.Presentation.Run
{
    /// <summary>
    /// IPlayerLoadoutStorage respaldado por PlayFab Player Data de solo lectura (por jugador — clave
    /// única bajo la cuenta logueada). Requiere sesión activa; ver PlayFabSession.
    /// </summary>
    public class PlayFabPlayerLoadoutStorage : IPlayerLoadoutStorage
    {
        public const string DataKey = "PlayerLoadout";

        public async Task<InventorySnapshot> LoadAsync()
        {
            var values = await PlayFabUserData.ReadAsync(DataKey);
            return values.TryGetValue(DataKey, out string json) ? JsonUtility.FromJson<InventorySnapshot>(json) : null;
        }

        /// <summary>Escritura validada por CloudScript (el cliente no puede escribir la clave directo).</summary>
        public Task SaveAsync(InventorySnapshot snapshot)
            => PlayFabUserData.CallAsync(PlayFabUserData.CommitProfileFunction,
                new Dictionary<string, string> { { "Loadout", JsonUtility.ToJson(snapshot) } });
    }
}