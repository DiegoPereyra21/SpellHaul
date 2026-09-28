using System;
using System.Threading.Tasks;
using Game.Core.Items;
using Game.Core.Run;
using UnityEngine;

namespace Game.Presentation.Run
{
    /// <summary>
    /// Persiste el stash del jugador (30 slots) entre sesiones. Stash es una cache en memoria de
    /// lectura instantánea; el guardado real se delega a IStashStorage (local por defecto,
    /// PlayFab más adelante — cambiar de backend es reasignar Storage, nada más se entera).
    /// </summary>
    public static class StashService
    {
        private const int MaxSaveRetries = 3;
        private const int MaxLoadRetries = 3;

        /// <summary>Backend activo. Cambiar esto es todo lo que hace falta para migrar de storage.</summary>
        public static IStashStorage Storage { get; set; } = new LocalStashStorage();

        private static StashData _stash;
        private static bool _initialized;
        private static bool _pendingSync;
        private static Task<bool> _initTask;

        /// <summary>El stash actual (cache en memoria). Null si todavía no se inicializó.</summary>
        public static StashData Stash => _stash;

        /// <summary>True si el último guardado falló tras agotar reintentos y todavía no se resincronizó.</summary>
        public static bool PendingSync => _pendingSync;

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

        /// <summary>Guarda el estado actual del stash. Cache instantáneo + persistencia en background.</summary>
        public static void Save(StashData stash)
        {
            _stash = stash;
            _initialized = true;
            _ = PersistAsync(stash);
        }

        /// <summary>Reintenta persistir el estado actual si el último guardado había fallado (ej. al recuperar conexión).</summary>
        public static Task RetrySyncAsync() => PersistAsync(_stash);

        private static bool _retryLoopRunning;

        private static async Task PersistAsync(StashData stash)
        {
            for (int attempt = 1; attempt <= MaxSaveRetries; attempt++)
            {
                try
                {
                    await Storage.SaveAsync(stash);
                    _pendingSync = false;
                    return;
                }
                catch (Exception e)
                {
                    if (attempt == MaxSaveRetries)
                    {
                        _pendingSync = true;
                        Debug.LogWarning($"[StashService] No se pudo persistir tras {MaxSaveRetries} intentos, reintentando en background: {e.Message}");
                        if (!_retryLoopRunning) _ = BackgroundRetryLoopAsync();
                        return;
                    }
                    await Task.Delay(500 * attempt);
                }
            }
        }

        /// <summary>Mientras quede un guardado pendiente, reintenta cada 5s hasta resincronizar.</summary>
        private static async Task BackgroundRetryLoopAsync()
        {
            _retryLoopRunning = true;
            try
            {
                while (_pendingSync)
                {
                    await Task.Delay(5000);
                    if (!_pendingSync) break;
                    try
                    {
                        await Storage.SaveAsync(_stash);
                        _pendingSync = false;
                    }
                    catch
                    {
                        // sigue pendiente, el while vuelve a intentar
                    }
                }
            }
            finally
            {
                _retryLoopRunning = false;
            }
        }
    }
}