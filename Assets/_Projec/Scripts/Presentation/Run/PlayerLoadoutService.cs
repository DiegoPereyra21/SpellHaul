using System;
using System.Threading.Tasks;
using Game.Core.Items;
using Game.Core.Run;
using UnityEngine;

namespace Game.Presentation.Run
{
    /// <summary>
    /// Persiste el inventario propio del jugador (snapshot) entre runs. Current es una cache en
    /// memoria de lectura instantánea; el guardado real se delega a IPlayerLoadoutStorage (local
    /// por defecto, PlayFab más adelante — cambiar de backend es reasignar Storage, nada más se
    /// entera). Provee el kit inicial la primera vez que no hay nada guardado.
    /// </summary>
    public static class PlayerLoadoutService
    {
        private const int MaxSaveRetries = 3;
        private const int MaxLoadRetries = 3;

        /// <summary>Backend activo. Cambiar esto es todo lo que hace falta para migrar de storage.</summary>
        public static IPlayerLoadoutStorage Storage { get; set; } = new LocalPlayerLoadoutStorage();

        private static InventorySnapshot _snapshot;
        private static bool _initialized;
        private static bool _pendingSync;
        private static Task<bool> _initTask;

        /// <summary>El inventario propio persistente actual (cache en memoria). Null si nunca se inicializó.</summary>
        public static InventorySnapshot Current => _snapshot;

        public static bool HasSnapshot => _initialized && _snapshot != null;

        /// <summary>True si el último guardado falló tras agotar reintentos y todavía no se resincronizó.</summary>
        public static bool PendingSync => _pendingSync;

        /// <summary>
        /// Carga desde el storage si nunca se inicializó en este proceso; si no hay nada guardado
        /// (jugador nuevo), arma el kit inicial y lo persiste. Llamar del lado cliente antes de
        /// necesitar Current (menú / al conectar a una run). Devuelve false si el storage no se
        /// pudo leer tras reintentar: en ese caso NO se inicializa ni se persiste nada (persistir el
        /// kit pisaría el loadout real del jugador por un corte puntual). Se puede volver a llamar.
        /// </summary>
        public static Task<bool> EnsureInitializedAsync(StartingKitSO kit)
        {
            if (_initialized) return Task.FromResult(true);

            // Una sola carga en vuelo: dos llamadas concurrentes (ej. stash + buscar partida)
            // otorgarían el kit dos veces.
            if (_initTask == null || _initTask.IsCompleted)
                _initTask = InitializeAsync(kit);
            return _initTask;
        }

        private static async Task<bool> InitializeAsync(StartingKitSO kit)
        {
            InventorySnapshot loaded = null;
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
                    Debug.LogWarning($"[PlayerLoadoutService] Falló la carga (intento {attempt}/{MaxLoadRetries}): {e.Message}");
                    if (attempt < MaxLoadRetries) await Task.Delay(500 * attempt);
                }
            }

            if (!loadedOk) return false;

            // Un Save() pudo haber llegado mientras se cargaba: ese estado es más nuevo, no pisarlo.
            if (_initialized) return true;

            if (loaded != null)
            {
                _snapshot = loaded;
                _initialized = true;
                return true;
            }

            _snapshot = BuildSnapshotWithKit(kit);
            _initialized = true;
            await PersistAsync(_snapshot); // primera vez (lectura OK y vacía): persistir el kit
            return true;
        }

        /// <summary>Guarda una foto nueva (al extraer / al gestionar en el menú). Cache instantáneo + persistencia en background.</summary>
        public static void Save(InventorySnapshot snapshot)
        {
            _snapshot = snapshot;
            _initialized = true;
            _ = PersistAsync(snapshot);
        }

        /// <summary>Vacía el inventario propio (al morir: volvés desnudo, pero con los slots de equipo visibles).</summary>
        public static void Clear()
        {
            _snapshot = BuildEmptySnapshot();
            _initialized = true;
            _ = PersistAsync(_snapshot);
        }

        /// <summary>Reintenta persistir el estado actual si el último guardado había fallado (ej. al recuperar conexión).</summary>
        public static Task RetrySyncAsync() => PersistAsync(_snapshot);

        private static bool _retryLoopRunning;

        private static async Task PersistAsync(InventorySnapshot snapshot)
        {
            for (int attempt = 1; attempt <= MaxSaveRetries; attempt++)
            {
                try
                {
                    await Storage.SaveAsync(snapshot);
                    _pendingSync = false;
                    return;
                }
                catch (Exception e)
                {
                    if (attempt == MaxSaveRetries)
                    {
                        _pendingSync = true;
                        Debug.LogWarning($"[PlayerLoadoutService] No se pudo persistir tras {MaxSaveRetries} intentos, reintentando en background: {e.Message}");
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
                        await Storage.SaveAsync(_snapshot);
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

        private static InventorySnapshot BuildSnapshotWithKit(StartingKitSO kit)
        {
            var snap = BuildEmptySnapshot();

            if (kit != null)
            {
                // Colocar cada pieza del kit en su slot correcto (según su EquipmentSlot).
                foreach (var e in kit.Equipment)
                {
                    if (e.Item == null) continue;
                    int slotIndex = (int)e.Slot;
                    if (slotIndex >= 0 && slotIndex < snap.Equipment.Count)
                        snap.Equipment[slotIndex] = new ItemStack(e.Item.ItemId, 1, 1f);
                }

                // Los items sueltos del kit arrancan en Pocket L. Si el kit trae más items de
                // los que la capacidad real termine permitiendo, RunInventory los rescata/dropea
                // igual que hoy hace con la mochila (misma lógica de RebuildBackpackCapacity).
                foreach (var b in kit.StartingItems)
                    if (b.Item != null)
                        snap.PocketL.Add(new ItemStack(b.Item.ItemId, b.Quantity, 1f));
            }

            return snap;
        }

        /// <summary>Snapshot con un slot vacío por cada EquipmentSlot (sin items). Base común de Clear/BuildSnapshotWithKit.</summary>
        private static InventorySnapshot BuildEmptySnapshot()
        {
            var snap = new InventorySnapshot();
            int slotCount = System.Enum.GetValues(typeof(EquipmentSlot)).Length;
            for (int i = 0; i < slotCount; i++)
                snap.Equipment.Add(ItemStack.Empty);
            return snap;
        }
    }
}