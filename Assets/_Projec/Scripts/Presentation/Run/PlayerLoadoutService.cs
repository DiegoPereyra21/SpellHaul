using System;
using System.Threading.Tasks;
using Game.Core.Items;
using Game.Core.Run;
using UnityEngine;

namespace Game.Presentation.Run
{
    /// <summary>
    /// Persiste el inventario propio del jugador (snapshot) entre runs. Current es una cache en
    /// memoria de lectura instantánea. La lectura va por IPlayerLoadoutStorage (local hasta que
    /// PlayFabSession activa el de PlayFab); la escritura la hace ProfileSaveQueue, junto con el
    /// stash. Provee el kit inicial la primera vez que no hay nada guardado.
    /// </summary>
    public static class PlayerLoadoutService
    {
        private const int MaxLoadRetries = 3;

        /// <summary>Backend activo. Cambiar esto es todo lo que hace falta para migrar de storage.</summary>
        public static IPlayerLoadoutStorage Storage { get; set; } = new LocalPlayerLoadoutStorage();

        private static InventorySnapshot _snapshot;
        private static bool _initialized;
        private static Task<bool> _initTask;

        /// <summary>El inventario propio persistente actual (cache en memoria). Null si nunca se inicializó.</summary>
        public static InventorySnapshot Current => _snapshot;

        public static bool HasSnapshot => _initialized && _snapshot != null;

        /// <summary>True mientras quede un guardado sin confirmar (ver ProfileSaveQueue).</summary>
        public static bool PendingSync => ProfileSaveQueue.PendingSync;

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
            ProfileSaveQueue.EnqueueLoadout(_snapshot); // primera vez (lectura OK y vacía): persistir el kit
            return true;
        }

        /// <summary>Guarda una foto nueva (al extraer / al gestionar en el menú). Cache instantáneo + persistencia en background.</summary>
        public static void Save(InventorySnapshot snapshot)
        {
            _snapshot = snapshot;
            _initialized = true;
            ProfileSaveQueue.EnqueueLoadout(snapshot);
        }

        /// <summary>Vacía el inventario propio (al morir: volvés desnudo, pero con los slots de equipo visibles).</summary>
        public static void Clear()
        {
            _snapshot = BuildEmptySnapshot();
            _initialized = true;
            ProfileSaveQueue.EnqueueLoadout(_snapshot);
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

                // Los items sueltos se reparten según la capacidad real de cada pocket del kit
                // (L primero, después R). Si el kit trae más de lo que entra, el sobrante queda en
                // L: la pantalla de Stash lo manda al stash y RunInventory lo reubica o lo dropea.
                int capL = KitPocketCapacity(kit, EquipmentSlot.PocketL);
                int capR = KitPocketCapacity(kit, EquipmentSlot.PocketR);
                foreach (var b in kit.StartingItems)
                {
                    if (b.Item == null) continue;
                    var stack = new ItemStack(b.Item.ItemId, b.Quantity, 1f);
                    if (snap.PocketL.Count < capL) snap.PocketL.Add(stack);
                    else if (snap.PocketR.Count < capR) snap.PocketR.Add(stack);
                    else snap.PocketL.Add(stack);
                }
            }

            return snap;
        }

        /// <summary>Capacidad del pocket del kit: la que declare el pocket equipado en ese lado, 1
        /// si no hay ninguno (misma regla que RunInventory.PocketCapacity).</summary>
        private static int KitPocketCapacity(StartingKitSO kit, EquipmentSlot pocketSlot)
        {
            foreach (var e in kit.Equipment)
                if (e.Slot == pocketSlot && e.Item != null && e.Item.Slot.IsPocket())
                    return Mathf.Max(e.Item.PocketSlots, 0);
            return 1;
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