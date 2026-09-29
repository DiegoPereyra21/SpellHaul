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

        // El session ticket y el entity token de PlayFab vencen a las 24 h. Se renuevan con tiempo
        // de sobra (a las 20 h) en segundo plano, y antes de buscar partida o reconectar.
        private const float RefreshAfterSeconds = 20f * 3600f;
        private const int RefreshCheckIntervalMs = 30 * 60 * 1000;

        private static float _loggedInAt;
        private static Task<bool> _refreshTask;

        private void Start()
        {
            // Un server dedicado no persiste inventarios de nadie: cada cliente habla con PlayFab
            // por su cuenta. Loguear acá sería una cuenta fantasma sin uso.
            if (LaunchArgs.IsDedicatedServer) return;

            if (IsReady) return; // ya logueado en este proceso (ej. volviste al MainMenu)
            _ = LoginLoopAsync();
        }

        /// <summary>Reintenta el login indefinidamente cada LoginRetryDelaySeconds hasta que resuelve.</summary>
        private static async Task LoginLoopAsync()
        {
            while (!IsReady)
            {
                bool success = await TryLoginOnceAsync();
                if (success) break;
                await Task.Delay(LoginRetryDelaySeconds * 1000);
            }

            // Un solo loop por proceso (Start no vuelve a entrar con IsReady): sobrevive a los
            // cambios de escena porque no depende de este componente.
            while (true)
            {
                await Task.Delay(RefreshCheckIntervalMs);
                if (!Application.isPlaying) return;
                if (NeedsRefresh) await RefreshAsync();
            }
        }

        private static bool NeedsRefresh => Time.realtimeSinceStartup - _loggedInAt >= RefreshAfterSeconds;

        /// <summary>
        /// Garantiza una sesión vigente antes de algo que la necesita (buscar partida, reconectar:
        /// el servidor verifica el session ticket). Si está por vencer, vuelve a loguear. False si
        /// no hay sesión o no se pudo renovar.
        /// </summary>
        public static async Task<bool> EnsureFreshAsync()
        {
            if (!IsReady) return false;
            if (!NeedsRefresh) return true;
            return await RefreshAsync();
        }

        private static Task<bool> RefreshAsync()
        {
            // Una sola renovación en vuelo aunque la pidan el loop y el menú a la vez.
            if (_refreshTask == null || _refreshTask.IsCompleted)
            {
                Debug.Log("[PlayFabSession] Renovando la sesión de PlayFab.");
                _refreshTask = TryLoginOnceAsync();
            }
            return _refreshTask;
        }

        private static async Task<bool> TryLoginOnceAsync()
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

                _loggedInAt = Time.realtimeSinceStartup;
                if (IsReady) return true; // renovación: la sesión ya estaba lista, nada más que avisar

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