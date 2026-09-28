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
    /// IStashStorage respaldado por PlayFab Player Data (privado, por jugador). Requiere sesión
    /// activa; ver PlayFabSession.
    /// </summary>
    public class PlayFabStashStorage : IStashStorage
    {
        public const string DataKey = "Stash";

        public Task<StashData> LoadAsync()
        {
            var tcs = new TaskCompletionSource<StashData>();

            PlayFabClientAPI.GetUserData(
                new GetUserDataRequest { Keys = new List<string> { DataKey } },
                result =>
                {
                    if (result.Data != null && result.Data.TryGetValue(DataKey, out var record) && !string.IsNullOrEmpty(record.Value))
                    {
                        try { tcs.SetResult(JsonUtility.FromJson<StashData>(record.Value)); }
                        catch (Exception e) { tcs.SetException(e); }
                    }
                    else
                    {
                        tcs.SetResult(null); // nada guardado todavía
                    }
                },
                error => tcs.SetException(new Exception(error.GenerateErrorReport())));

            return tcs.Task;
        }

        public Task SaveAsync(StashData stash)
            => PlayFabUserData.UpdateAsync(new Dictionary<string, string> { { DataKey, JsonUtility.ToJson(stash) } });
    }
}