using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Game.Core.Items;
using UnityEngine;
#if ENABLE_PLAYFABSERVER_API
using PlayFab;
using PlayFab.ServerModels;
#endif

namespace Game.Presentation.Run
{
    /// <summary>
    /// Server-only. Persistencia del loadout escrita por el servidor de la run (Server API de
    /// PlayFab), no por el cliente: el servidor lee el loadout al entrar el jugador, marca la run
    /// en curso y guarda el resultado al extraer o morir. Así un cliente modificado ya no puede
    /// quedarse el equipo al morir ni "restaurar" lo que perdió.
    ///
    /// Solo se activa en un servidor dedicado con la clave secreta del título configurada
    /// (variable de entorno o archivo junto al ejecutable, ver Configure). Sin eso (host, editor,
    /// cliente) IsActive es false y todo sigue como antes: el cliente guarda su propio loadout.
    /// La clave secreta NUNCA va en el cliente ni en el repositorio.
    /// </summary>
    public static class ServerProfileStore
    {
        public const string SecretEnvVar = "SPELLHAUL_PLAYFAB_SECRET";
        public const string SecretFileName = "playfab_secret.txt";

        private const int MaxLoadRetries = 3;
        private const int MaxBackoffMs = 5000;

        /// <summary>True si este proceso escribe la persistencia de los jugadores.</summary>
        public static bool IsActive { get; private set; }

        /// <summary>
        /// Llamar una vez al arrancar el servidor dedicado. Busca la clave secreta en la variable
        /// de entorno SPELLHAUL_PLAYFAB_SECRET o en playfab_secret.txt junto al ejecutable.
        /// </summary>
        public static void Configure()
        {
#if ENABLE_PLAYFABSERVER_API
            string secret = Environment.GetEnvironmentVariable(SecretEnvVar);
            if (string.IsNullOrWhiteSpace(secret))
            {
                // Application.dataPath = <carpeta del ejecutable>/<Nombre>_Data
                string exeDir = Path.GetDirectoryName(Application.dataPath);
                string path = exeDir != null ? Path.Combine(exeDir, SecretFileName) : SecretFileName;
                if (File.Exists(path)) secret = File.ReadAllText(path);
            }

            if (string.IsNullOrWhiteSpace(secret))
            {
                IsActive = false;
                Debug.LogWarning("[ServerProfileStore] Sin clave secreta de PlayFab: el loadout lo sigue guardando el cliente.");
                return;
            }

            PlayFabSettings.staticSettings.DeveloperSecretKey = secret.Trim();
            IsActive = true;
            Debug.Log("[ServerProfileStore] Clave secreta cargada: el servidor es dueño del loadout de los jugadores.");
#else
            IsActive = false;
#endif
        }

        // ---------- Identidad ----------

        /// <summary>Resultado de validar el session ticket de un cliente.</summary>
        public struct VerifiedPlayer
        {
            public bool Ok;
            public string PlayFabId;
            public string EntityId;
            public string Error;
        }

        /// <summary>Valida el session ticket del cliente contra PlayFab: devuelve quién es de verdad.</summary>
        public static Task<VerifiedPlayer> VerifySessionTicketAsync(string sessionTicket)
        {
            var tcs = new TaskCompletionSource<VerifiedPlayer>();
#if ENABLE_PLAYFABSERVER_API
            if (string.IsNullOrEmpty(sessionTicket))
            {
                tcs.SetResult(new VerifiedPlayer { Error = "sin session ticket" });
                return tcs.Task;
            }

            PlayFabServerAPI.AuthenticateSessionTicket(
                new AuthenticateSessionTicketRequest { SessionTicket = sessionTicket },
                result =>
                {
                    var info = result.UserInfo;
                    bool expired = result.IsSessionTicketExpired == true;
                    string entityId = info?.TitleInfo?.TitlePlayerAccount?.Id;
                    if (info == null || expired || string.IsNullOrEmpty(info.PlayFabId) || string.IsNullOrEmpty(entityId))
                    {
                        tcs.SetResult(new VerifiedPlayer { Error = expired ? "session ticket vencido" : "respuesta incompleta" });
                        return;
                    }
                    tcs.SetResult(new VerifiedPlayer { Ok = true, PlayFabId = info.PlayFabId, EntityId = entityId });
                },
                error => tcs.SetResult(new VerifiedPlayer { Error = error.GenerateErrorReport() }));
#else
            tcs.SetResult(new VerifiedPlayer { Error = "Server API no disponible" });
#endif
            return tcs.Task;
        }

        // ---------- Lectura ----------

        /// <summary>
        /// Lee el loadout guardado del jugador. ok=false si PlayFab no respondió tras reintentar:
        /// en ese caso NO hay que escribir nada para este jugador (pisaría su loadout real).
        /// snapshot=null con ok=true: jugador sin nada guardado todavía.
        /// </summary>
        public static async Task<(bool ok, InventorySnapshot snapshot)> LoadLoadoutAsync(string playFabId)
        {
            for (int attempt = 1; attempt <= MaxLoadRetries; attempt++)
            {
                try
                {
                    return (true, await ReadLoadoutOnceAsync(playFabId));
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[ServerProfileStore] Falló la lectura del loadout de {playFabId} (intento {attempt}/{MaxLoadRetries}): {e.Message}");
                    if (attempt < MaxLoadRetries) await Task.Delay(500 * attempt);
                }
            }
            return (false, null);
        }

        private static Task<InventorySnapshot> ReadLoadoutOnceAsync(string playFabId)
        {
            var tcs = new TaskCompletionSource<InventorySnapshot>();
#if ENABLE_PLAYFABSERVER_API
            string key = PlayFabPlayerLoadoutStorage.DataKey;
            PlayFabServerAPI.GetUserData(
                new GetUserDataRequest { PlayFabId = playFabId, Keys = new List<string> { key } },
                result =>
                {
                    if (result.Data != null && result.Data.TryGetValue(key, out var record) && !string.IsNullOrEmpty(record.Value))
                    {
                        try { tcs.SetResult(JsonUtility.FromJson<InventorySnapshot>(record.Value)); }
                        catch (Exception e) { tcs.SetException(e); }
                    }
                    else
                    {
                        tcs.SetResult(null);
                    }
                },
                error => tcs.SetException(new Exception(error.GenerateErrorReport())));
#else
            tcs.SetException(new Exception("Server API no disponible"));
#endif
            return tcs.Task;
        }

        // ---------- Escritura ----------

        // Último JSON pendiente por jugador: lo más nuevo gana, igual que ProfileSaveQueue.
        private static readonly Dictionary<string, string> _pending = new();
        private static readonly HashSet<string> _running = new();

        /// <summary>True mientras quede alguna escritura sin confirmar (el proceso espera antes de cerrarse).</summary>
        public static bool HasPendingWrites => _pending.Count > 0 || _running.Count > 0;

        /// <summary>Guarda el loadout del jugador (JSON tomado ahora). Reintenta hasta confirmar.</summary>
        public static void SaveLoadout(string playFabId, InventorySnapshot snapshot)
        {
            if (!IsActive || string.IsNullOrEmpty(playFabId) || snapshot == null) return;

            _pending[playFabId] = JsonUtility.ToJson(snapshot);
            if (!_running.Contains(playFabId)) _ = RunAsync(playFabId);
        }

        private static async Task RunAsync(string playFabId)
        {
            _running.Add(playFabId);
            try
            {
                int failures = 0;
                while (_pending.TryGetValue(playFabId, out string json))
                {
                    _pending.Remove(playFabId);
                    try
                    {
                        await WriteOnceAsync(playFabId, json);
                        failures = 0;
                    }
                    catch (Exception e)
                    {
                        if (!_pending.ContainsKey(playFabId)) _pending[playFabId] = json; // lo nuevo gana
                        failures++;
                        int delay = Math.Min(MaxBackoffMs, 500 * (1 << Math.Min(failures - 1, 4)));
                        Debug.LogWarning($"[ServerProfileStore] Falló el guardado de {playFabId} (intento {failures}), reintento en {delay} ms: {e.Message}");
                        await Task.Delay(delay);
                    }
                }
            }
            finally
            {
                _running.Remove(playFabId);
            }
        }

        private static Task WriteOnceAsync(string playFabId, string json)
        {
            var tcs = new TaskCompletionSource<bool>();
#if ENABLE_PLAYFABSERVER_API
            PlayFabServerAPI.UpdateUserData(
                new UpdateUserDataRequest
                {
                    PlayFabId = playFabId,
                    Data = new Dictionary<string, string> { { PlayFabPlayerLoadoutStorage.DataKey, json } },
                    Permission = UserDataPermission.Private,
                },
                _ => tcs.SetResult(true),
                error => tcs.SetException(new Exception(error.GenerateErrorReport())));
#else
            tcs.SetException(new Exception("Server API no disponible"));
#endif
            return tcs.Task;
        }
    }
}
