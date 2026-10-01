using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Game.Core.Items;
using UnityEngine;

namespace Game.Presentation.Run
{
    /// <summary>
    /// Único escritor del perfil persistente (loadout + stash). Antes cada servicio guardaba por su
    /// cuenta y en paralelo: loadout y stash salían en dos escrituras separadas (si fallaba una sola,
    /// al recargar un item aparecía duplicado o desaparecía) y un reintento viejo podía llegar
    /// después de un guardado nuevo y pisarlo.
    ///
    /// Ahora: cada Save deja la foto (JSON, tomada en ese instante) como pendiente; un solo loop la
    /// escribe, de a una escritura por vez, y lo más nuevo siempre gana. Con PlayFab, loadout y
    /// stash pendientes viajan en la MISMA llamada a CommitProfile (CloudScript, atómica), que
    /// valida que no aparezcan items. Reintenta sin límite con backoff los errores de red; si
    /// CloudScript rechaza el cambio, se descarta y se relee el perfil real (OnProfileReloaded).
    /// Todo corre en el hilo principal (callbacks de PlayFab y continuaciones de Unity), así que
    /// no hay carreras entre Enqueue y el loop.
    /// </summary>
    public static class ProfileSaveQueue
    {
        private const int MaxBackoffMs = 5000;

        private static string _pendingLoadoutJson; // null = nada pendiente
        private static string _pendingStashJson;
        private static bool _running;

        // CommitProfile rechaza con "in_run" mientras siga la marca de run en curso. Justo después
        // de una run eso es transitorio (el servidor está guardando el resultado): se espera un rato.
        private const string InRunReason = "in_run";
        private const float MaxInRunWaitSeconds = 60f;
        private const int InRunRetryMs = 2000;
        private static float _inRunWaitStart = -1f;

        private static bool ShouldWaitForRunResult()
        {
            if (_inRunWaitStart < 0f) _inRunWaitStart = Time.realtimeSinceStartup;
            return Time.realtimeSinceStartup - _inRunWaitStart < MaxInRunWaitSeconds;
        }

        /// <summary>Se rechazó un guardado y el perfil se volvió a leer de PlayFab: las pantallas
        /// que muestran loadout/stash tienen que redibujarse. Argumento: motivo del rechazo.</summary>
        public static event Action<string> OnProfileReloaded;

        /// <summary>True mientras quede algo sin confirmar por el backend.</summary>
        public static bool PendingSync => _pendingLoadoutJson != null || _pendingStashJson != null;

        /// <summary>True si queda algo pendiente o hay una escritura en vuelo (EconomyService espera
        /// esto antes de craftear/vender: CloudScript tiene que ver el stash ya guardado).</summary>
        public static bool Busy => PendingSync || _running;

        public static void EnqueueLoadout(InventorySnapshot snapshot)
        {
            if (snapshot == null) return;
            _pendingLoadoutJson = JsonUtility.ToJson(snapshot);
            Kick();
        }

        public static void EnqueueStash(StashData stash)
        {
            if (stash == null) return;
            _pendingStashJson = JsonUtility.ToJson(stash);
            Kick();
        }

        private static void Kick()
        {
            if (!_running) _ = RunAsync();
        }

        private static async Task RunAsync()
        {
            _running = true;
            try
            {
                // Un frame de margen: un loadout y un stash guardados juntos (pantalla de Stash)
                // salen en una sola escritura.
                await Task.Yield();

                int failures = 0;
                while (PendingSync)
                {
                    string loadout = _pendingLoadoutJson;
                    string stash = _pendingStashJson;
                    _pendingLoadoutJson = null;
                    _pendingStashJson = null;

                    try
                    {
                        await WriteAsync(loadout, stash);
                        failures = 0;
                        _inRunWaitStart = -1f;
                    }
                    catch (PlayFabUserData.RejectedException rejected)
                        when (rejected.Reason == InRunReason && ShouldWaitForRunResult())
                    {
                        // Recién terminó una run y el servidor todavía no guardó el resultado (sigue
                        // la marca de run en curso): esperar un poco y reintentar, no descartar.
                        _pendingLoadoutJson ??= loadout;
                        _pendingStashJson ??= stash;
                        Debug.Log($"[ProfileSaveQueue] El resultado de la run todavía no está guardado; reintento en {InRunRetryMs} ms.");
                        await Task.Delay(InRunRetryMs);
                    }
                    catch (PlayFabUserData.RejectedException rejected)
                    {
                        _inRunWaitStart = -1f;
                        // Reintentar no cambia nada, y lo que siga pendiente se armó sobre este mismo
                        // estado inválido: se descarta todo y manda lo que diga PlayFab.
                        _pendingLoadoutJson = null;
                        _pendingStashJson = null;
                        Debug.LogWarning($"[ProfileSaveQueue] {rejected.Message}. Se relee el perfil.");
                        await ReloadFromBackendAsync(rejected.Reason);
                    }
                    catch (Exception e)
                    {
                        // Si mientras tanto llegó algo más nuevo para esa clave, gana lo nuevo.
                        _pendingLoadoutJson ??= loadout;
                        _pendingStashJson ??= stash;

                        failures++;
                        int delay = Math.Min(MaxBackoffMs, 500 * (1 << Math.Min(failures - 1, 4)));
                        Debug.LogWarning($"[ProfileSaveQueue] Falló el guardado (intento {failures}), reintento en {delay} ms: {e.Message}");
                        await Task.Delay(delay);
                    }
                }
            }
            finally
            {
                _running = false;
            }
        }

        private static async Task ReloadFromBackendAsync(string reason)
        {
            bool loadout = await PlayerLoadoutService.ReloadAsync();
            bool stash = await StashService.ReloadAsync();
            if (!loadout || !stash)
                Debug.LogWarning("[ProfileSaveQueue] No se pudo releer el perfil; se reintenta al volver a abrir el menú.");
            OnProfileReloaded?.Invoke(reason);
        }

        private static async Task WriteAsync(string loadoutJson, string stashJson)
        {
            bool loadoutOnPlayFab = PlayerLoadoutService.Storage is PlayFabPlayerLoadoutStorage;
            bool stashOnPlayFab = StashService.Storage is PlayFabStashStorage;

            // Camino normal (sesión de PlayFab activa): todo lo pendiente en una sola llamada.
            if ((loadoutJson == null || loadoutOnPlayFab) && (stashJson == null || stashOnPlayFab))
            {
                var args = new Dictionary<string, string>();
                if (loadoutJson != null) args["Loadout"] = loadoutJson;
                if (stashJson != null) args["Stash"] = stashJson;
                await PlayFabUserData.CallAsync(PlayFabUserData.CommitProfileFunction, args);
                return;
            }

            // Backend local (antes del login / pruebas por conexión directa): de a uno.
            if (loadoutJson != null)
                await PlayerLoadoutService.Storage.SaveAsync(JsonUtility.FromJson<InventorySnapshot>(loadoutJson));
            if (stashJson != null)
                await StashService.Storage.SaveAsync(JsonUtility.FromJson<StashData>(stashJson));
        }
    }
}
