using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Game.Core.Items;
using Game.Core.Run;
using PlayFab;
using PlayFab.ClientModels;
using UnityEngine;

namespace Game.Presentation.Run
{
    /// <summary>
    /// IPlayerLoadoutStorage respaldado por PlayFab Player Data (privado, por jugador — clave
    /// única bajo la cuenta logueada). Requiere sesión activa; ver PlayFabSession.
    /// </summary>
    public class PlayFabPlayerLoadoutStorage : IPlayerLoadoutStorage
    {
        public const string DataKey = "PlayerLoadout";

        public Task<InventorySnapshot> LoadAsync()
        {
            var tcs = new TaskCompletionSource<InventorySnapshot>();

            PlayFabClientAPI.GetUserData(
                new GetUserDataRequest { Keys = new List<string> { DataKey } },
                result =>
                {
                    if (result.Data != null && result.Data.TryGetValue(DataKey, out var record) && !string.IsNullOrEmpty(record.Value))
                    {
                        try { tcs.SetResult(JsonUtility.FromJson<InventorySnapshot>(record.Value)); }
                        catch (Exception e) { tcs.SetException(e); }
                    }
                    else
                    {
                        tcs.SetResult(null); // nada guardado todavía (jugador nuevo)
                    }
                },
                error => tcs.SetException(new Exception(error.GenerateErrorReport())));

            return tcs.Task;
        }

        public Task SaveAsync(InventorySnapshot snapshot)
            => PlayFabUserData.UpdateAsync(new Dictionary<string, string> { { DataKey, JsonUtility.ToJson(snapshot) } });
    }
}