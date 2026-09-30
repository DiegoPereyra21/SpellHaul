using Game.Core.Items;
using UnityEngine;

namespace Game.Presentation.UI
{
    /// <summary>
    /// Client-only. Lo que la pantalla de resultados muestra de la run del jugador local: tiempo,
    /// enemigos derrotados, lo que sacó (al extraer) o lo que perdió y qué lo mató (al morir).
    /// Lo van llenando el inventario (al extraer), el handler de muerte y el HUD (kills).
    /// </summary>
    public static class RunSummary
    {
        public static float RunStartTime { get; private set; }
        public static int Kills { get; private set; }
        public static InventorySnapshot ExtractedItems { get; private set; }
        public static InventorySnapshot LostItems { get; private set; }
        public static string DeathCause { get; private set; }
        public static ItemDatabase Database { get; private set; }

        /// <summary>Al tomar el control del personaje en una run.</summary>
        public static void BeginRun(ItemDatabase database)
        {
            RunStartTime = Time.time;
            Kills = 0;
            ExtractedItems = null;
            LostItems = null;
            DeathCause = null;
            if (database != null) Database = database;
        }

        public static void AddKill() => Kills++;
        public static void SetExtracted(InventorySnapshot items) => ExtractedItems = items;

        public static void SetDeath(string cause, InventorySnapshot lost)
        {
            DeathCause = cause;
            LostItems = lost;
        }

        public static float ElapsedSeconds => Mathf.Max(0f, Time.time - RunStartTime);
    }
}
