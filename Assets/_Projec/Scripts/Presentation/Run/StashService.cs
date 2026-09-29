using System;
using System.Threading.Tasks;
using Game.Core.Items;
using Game.Core.Run;
using UnityEngine;

namespace Game.Presentation.Run
{
    /// <summary>
    /// Persiste el stash del jugador (30 slots) entre sesiones. Stash es una cache en memoria de
    /// lectura instantánea. La lectura va por IStashStorage (local hasta que PlayFabSession activa
    /// el de PlayFab); la escritura la hace ProfileSaveQueue, junto con el loadout.
    /// </summary>
    public static class StashService
    {
        private const int MaxLoadRetries = 3;

        /// <summary>Backend activo. Cambiar esto es todo lo que hace falta para migrar de storage.</summary>
        public static IStashStorage Storage { get; set; } = new LocalStashStorage();

        private static StashData _stash;
        private static bool _initialized;
        private static Task<bool> _initTask;

        /// <summary>El stash actual (cache en memoria). Null si todavía no se inicializó.</summary>
        public static StashData Stash => _stash;

        /// <summary>True mientras quede un guardado sin confirmar (ver ProfileSaveQueue).</summary>
        public static bool PendingSync => ProfileSaveQueue.PendingSync;

        /// <summary>
        /// Carga desde el storage si nunca se inicializó en este proceso; si no hay nada guardado
        /// (jugador nuevo), arranca con un stash vacío. Llamar del lado cliente antes de necesitar
        /// Stash (al abrir la pantalla del menú). Devuelve false si el storage no se pudo leer tras
        /// reintentar: en ese caso NO se inicializa (un stash vacío guardado después pisaría el real).
        /// Se puede volver a llamar.
        /// </summary>
        public static Task<bool> EnsureInitializedAsync()
        {
            if (_initialized) return Task.FromResult(true);

            if (_initTask == null || _initTask.IsCompleted)
                _initTask = InitializeAsync();
            return _initTask;
        }

        private static async Task<bool> InitializeAsync()
        {
            StashData loaded = null;
            bool loadedOk = false;

            for (int attempt = 1; attempt <= MaxLoadRetries && !loadedOk; attempt++)
            {
                try
                {
                    loaded = await Storage.LoadAsync();
                    loadedOk = true;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[StashService] Falló la carga (intento {attempt}/{MaxLoadRetries}): {e.Message}");
                    if (attempt < MaxLoadRetries) await Task.Delay(500 * attempt);
                }
            }

            if (!loadedOk) return false;

            // Un Save() pudo haber llegado mientras se cargaba: ese estado es más nuevo, no pisarlo.
            if (_initialized) return true;

            _stash = loaded ?? new StashData();
            _initialized = true;
            return true;
        }

        /// <summary>Relee el stash del storage y reemplaza la cache solo si la lectura funcionó.</summary>
        public static async Task<bool> ReloadAsync()
        {
            try
            {
                var loaded = await Storage.LoadAsync();
                _stash = loaded ?? _stash ?? new StashData();
                _initialized = true;
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[StashService] Falló la relectura: {e.Message}");
                return false;
            }
        }

        /// <summary>Guarda el estado actual del stash. Cache instantáneo + persistencia en background.</summary>
        public static void Save(StashData stash)
        {
            _stash = stash;
            _initialized = true;
            ProfileSaveQueue.EnqueueStash(stash);
        }
    }
}