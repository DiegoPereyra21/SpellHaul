using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PlayFab;
using PlayFab.ClientModels;
using UnityEngine;

namespace Game.Presentation.Bootstrap
{
    /// <summary>
    /// Login a PlayFab al arrancar el menú. Activa los backends reales de persistencia
    /// (PlayerLoadoutService/StashService) recién cuando el login resuelve — hasta entonces
    /// ambos servicios siguen en su backend Local por defecto. Vive una vez en la escena
    /// MainMenu; IsReady/OnReady son estáticos, no importa el orden de ejecución de scripts.
    /// TEMPORAL: usa LoginWithCustomID hasta tener el AppID de Steam propio. Cuando esté, este
    /// es el único archivo que hay que tocar para pasar a LoginWithSteam.
    /// </summary>
    public class PlayFabSession : MonoBehaviour
    {
        public static bool IsReady { get; private set; }

        /// <summary>Entity Key del jugador logueado (lo que usan las APIs de matchmaking, distinto
        /// del PlayFabId). Vacío hasta que IsReady sea true.</summary>
        public static string EntityId { get; private set; }

        /// <summary>Session ticket de la sesión actual: se lo manda al servidor de la run para que
        /// verifique la identidad con PlayFab. Nunca se loguea.</summary>
        public static string SessionTicket { get; private set; }
        public static string EntityType { get; private set; }


        public static event Action OnReady;

        private const int LoginRetryDelaySeconds = 5;

        private void Start()
        {
            // Un server dedicado no persiste inventarios de nadie: cada cliente habla con PlayFab
            // por su cuenta. Loguear acá sería una cuenta fantasma sin uso.
            if (LaunchArgs.IsDedicatedServer) return;

            if (IsReady) return; // ya logueado en este proceso (ej. volviste al MainMenu)
            _ = LoginLoopAsync();
        }

        /// <summary>Reintenta el login indefinidamente cada LoginRetryDelaySeconds hasta que resuelve.</summary>
        private async Task LoginLoopAsync()
        {
            while (!IsReady)
            {
                bool success = await TryLoginOnceAsync();
                if (success) break;
                await Task.Delay(LoginRetryDelaySeconds * 1000);
            }
        }

        private async Task<bool> TryLoginOnceAsync()
        {
            var tcs = new TaskCompletionSource<LoginResult>();


            // -playerid permite simular jugadores distintos en la misma máquina (ver LaunchArgs).
            string customId = string.IsNullOrEmpty(LaunchArgs.PlayerId)
                ? SystemInfo.deviceUniqueIdentifier
                : LaunchArgs.PlayerId;

            var request = new LoginWithCustomIDRequest
            {
                CustomId = customId,
                CreateAccount = true
            };

            
            PlayFabClientAPI.LoginWithCustomID(request,
                result => tcs.SetResult(result),
                error => tcs.SetException(new Exception(error.GenerateErrorReport())));

            try
            {
                var result = await tcs.Task;
                Debug.Log($"[PlayFabSession] Login OK. PlayFabId: {result.PlayFabId}");
                EntityId = result.EntityToken?.Entity?.Id;
                SessionTicket = result.SessionTicket;
                EntityType = result.EntityToken?.Entity?.Type;
                // El perfil (loadout + stash) es de solo lectura para el cliente: si es un jugador
                // nuevo, CloudScript le da el kit inicial. Sin esto no se puede jugar ni usar el stash.
                await Game.Presentation.Run.PlayFabUserData.CallAsync(Game.Presentation.Run.PlayFabUserData.EnsureProfileFunction);

                Game.Presentation.Run.PlayerLoadoutService.Storage = new Game.Presentation.Run.PlayFabPlayerLoadoutStorage();
                Game.Presentation.Run.StashService.Storage = new Game.Presentation.Run.PlayFabStashStorage();

                IsReady = true;
                OnReady?.Invoke();
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PlayFabSession] Login falló, reintento en {LoginRetryDelaySeconds}s: {e.Message}");
                return false;
            }
        }
    }
}