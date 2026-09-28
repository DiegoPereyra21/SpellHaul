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
    /// stash pendientes viajan en el MISMO UpdateUserData (atómico). Reintenta sin límite con
    /// backoff mientras quede algo pendiente. Todo corre en el hilo principal (callbacks de
    /// PlayFab y continuaciones de Unity), así que no hay carreras entre Enqueue y el loop.
    /// </summary>
    public static class ProfileSaveQueue
    {
        private const int MaxBackoffMs = 5000;

        private static string _pendingLoadoutJson; // null = nada pendiente
        private static string _pendingStashJson;
        private static bool _running;

        /// <summary>True mientras quede algo sin confirmar por el backend.</summary>
        public static bool PendingSync => _pendingLoadoutJson != null || _pendingStashJson != null;

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

        private static async Task WriteAsync(string loadoutJson, string stashJson)
        {
            bool loadoutOnPlayFab = PlayerLoadoutService.Storage is PlayFabPlayerLoadoutStorage;
            bool stashOnPlayFab = StashService.Storage is PlayFabStashStorage;

            // Camino normal (sesión de PlayFab activa): todo lo pendiente en una sola escritura.
            if ((loadoutJson == null || loadoutOnPlayFab) && (stashJson == null || stashOnPlayFab))
            {
                var data = new Dictionary<string, string>();
                if (loadoutJson != null) data[PlayFabPlayerLoadoutStorage.DataKey] = loadoutJson;
                if (stashJson != null) data[PlayFabStashStorage.DataKey] = stashJson;
                await PlayFabUserData.UpdateAsync(data);
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
