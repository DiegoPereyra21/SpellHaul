using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PlayFab;
using PlayFab.ClientModels;

namespace Game.Presentation.Run
{
    /// <summary>Escritura de Player Data privada en PlayFab (varias claves en un solo request, que
    /// PlayFab aplica juntas). Requiere sesión activa; ver PlayFabSession.</summary>
    public static class PlayFabUserData
    {
        public static Task UpdateAsync(Dictionary<string, string> data)
        {
            var tcs = new TaskCompletionSource<bool>();

            PlayFabClientAPI.UpdateUserData(
                new UpdateUserDataRequest
                {
                    Data = data,
                    Permission = UserDataPermission.Private
                },
                _ => tcs.SetResult(true),
                error => tcs.SetException(new Exception(error.GenerateErrorReport())));

            return tcs.Task;
        }
    }
}
