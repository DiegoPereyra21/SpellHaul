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

            _snapshot = CreateStartingSnapshot(kit);
            _initialized = true;
            ProfileSaveQueue.EnqueueLoadout(_snapshot); // primera vez (lectura OK y vacía): persistir el kit
            return true;
        }

        /// <summary>True si el loadout está "adentro" de una run que no terminó para este jugador
        /// (se desconectó o cerró el juego). Hasta resolverla (reconectar o abandonar) no se puede
        /// jugar otra ni tocar el inventario.</summary>
        public static bool IsInActiveRun => _snapshot != null && _snapshot.ActiveRun.Active;

        public static ActiveRunInfo ActiveRun => _snapshot != null ? _snapshot.ActiveRun : default;

        /// <summary>
        /// Client-only, por conexión. True si el servidor de la run guarda el loadout (ver
        /// ServerProfileStore): el cliente solo actualiza su cache y no escribe nada propio de la run.
        /// Lo avisa el servidor al autenticar; se reinicia en cada conexión.
        /// </summary>
        public static bool ServerOwnsRun { get; set; }

        /// <summary>Client-only. El servidor ya tiene nuestro loadout: desde acá está en juego.
        /// Si el servidor es dueño del loadout, él ya guardó la marca: acá solo se refleja en la cache.</summary>
        public static void MarkActiveRun(string address, ushort port)
        {
            if (_snapshot == null) return;
            _snapshot.ActiveRun = CreateActiveRun(address, port);
            if (!ServerOwnsRun) ProfileSaveQueue.EnqueueLoadout(_snapshot);
        }

        public static ActiveRunInfo CreateActiveRun(string address, ushort port) => new ActiveRunInfo
        {
            Active = true,
            Address = address,
            Port = port,
            StartedUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };

        /// <summary>Client-only. Resultado de la run que llega del servidor (extrajo con esto).</summary>
        public static void ApplyRunResult(InventorySnapshot snapshot)
        {
            if (snapshot == null) return;
            if (ServerOwnsRun) { _snapshot = snapshot; _initialized = true; } // ya lo guardó el servidor
            else Save(snapshot);
        }

        /// <summary>Client-only. Descarta la cache: la próxima lectura va a PlayFab (el servidor
        /// es quien sabe cómo quedó el loadout).</summary>
        public static void Invalidate()
        {
            _snapshot = null;
            _initialized = false;
        }

        /// <summary>Client-only. La run se perdió (murió, o ya no puede volver).</summary>
        public static void ApplyRunLost()
        {
            if (ServerOwnsRun) { _snapshot = CreateEmptySnapshot(); _initialized = true; }
            else Clear();
        }

        /// <summary>Abandona la run en curso: el equipo que se llevó se pierde (igual que morir).</summary>
        public static void AbandonActiveRun() => Clear();

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
            _snapshot = CreateEmptySnapshot();
            _initialized = true;
            ProfileSaveQueue.EnqueueLoadout(_snapshot);
        }

        /// <summary>Loadout de un jugador nuevo: el kit inicial acomodado en sus slots.</summary>
        public static InventorySnapshot CreateStartingSnapshot(StartingKitSO kit)
        {
            var snap = CreateEmptySnapshot();

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

        /// <summary>Snapshot con un slot vacío por cada EquipmentSlot (sin items). Base común de Clear/CreateStartingSnapshot.</summary>
        public static InventorySnapshot CreateEmptySnapshot()
        {
            var snap = new InventorySnapshot();
            int slotCount = System.Enum.GetValues(typeof(EquipmentSlot)).Length;
            for (int i = 0; i < slotCount; i++)
                snap.Equipment.Add(ItemStack.Empty);
            return snap;
        }
    }
}