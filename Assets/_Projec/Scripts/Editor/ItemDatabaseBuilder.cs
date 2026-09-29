using System.Collections.Generic;
using Game.Core.Items;
using UnityEditor;
using UnityEngine;

// A propósito NO va en el namespace Game.Core.Items: Core es lógica de juego sin
// dependencias de Unity/Editor, y esto es herramienta de Editor pura (AssetDatabase,
// SerializedObject). Vive aparte.
namespace Game.EditorTools.Items
{
    /// <summary>
    /// Mantiene ItemDatabase.asset sincronizado con todos los ItemSO del proyecto, sin
    /// depender de que alguien se acuerde de arrastrarlos a mano. Corre solo al crear,
    /// borrar o mover un ItemSO; también se puede forzar desde el menú.
    /// </summary>
    public class ItemDatabaseBuilder : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            // Un asset borrado ya no se puede cargar para saber si era un ItemSO: cualquier .asset
            // borrado dispara el rebuild (barato, y así no queda una entrada null en la base).
            if (TouchesItemSO(importedAssets) || TouchesItemSO(movedAssets) || AnyAsset(deletedAssets))
                Rebuild();
        }

        private static bool AnyAsset(string[] paths)
        {
            foreach (var path in paths)
                if (path.EndsWith(".asset")) return true;
            return false;
        }

        private static bool TouchesItemSO(string[] paths)
        {
            foreach (var path in paths)
            {
                if (!path.EndsWith(".asset")) continue;
                if (AssetDatabase.LoadAssetAtPath<ItemSO>(path) != null) return true;
            }
            return false;
        }

        [MenuItem("Game/Items/Rebuild Item Database")]
        public static void Rebuild()
        {
            string[] itemGuids = AssetDatabase.FindAssets("t:ItemSO");
            var found = new List<ItemSO>();
            var seenIds = new HashSet<string>();
            bool hasDuplicate = false;

            foreach (var guid in itemGuids)
            {
                var item = AssetDatabase.LoadAssetAtPath<ItemSO>(AssetDatabase.GUIDToAssetPath(guid));
                if (item == null) continue;

                if (string.IsNullOrEmpty(item.ItemId))
                {
                    Debug.LogWarning($"[ItemDatabaseBuilder] '{item.name}' no tiene ItemId, no se agrega.");
                    continue;
                }
                if (!seenIds.Add(item.ItemId))
                {
                    Debug.LogWarning($"[ItemDatabaseBuilder] ItemId duplicado: '{item.ItemId}' ({item.name}).");
                    hasDuplicate = true;
                }

                found.Add(item);
            }

            found.Sort((a, b) => string.Compare(a.ItemId, b.ItemId, System.StringComparison.Ordinal));

            string[] dbGuids = AssetDatabase.FindAssets("t:ItemDatabase");
            if (dbGuids.Length == 0)
            {
                Debug.LogWarning("[ItemDatabaseBuilder] No hay ningún ItemDatabase en el proyecto.");
                return;
            }
            if (dbGuids.Length > 1)
                Debug.LogWarning("[ItemDatabaseBuilder] Hay más de un ItemDatabase; actualizo el primero que encontré.");

            var database = AssetDatabase.LoadAssetAtPath<ItemDatabase>(AssetDatabase.GUIDToAssetPath(dbGuids[0]));
            var serialized = new SerializedObject(database);
            var itemsProp = serialized.FindProperty("_items");

            itemsProp.arraySize = found.Count;
            for (int i = 0; i < found.Count; i++)
                itemsProp.GetArrayElementAtIndex(i).objectReferenceValue = found[i];

            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(database);
            AssetDatabase.SaveAssets();

            Debug.Log(hasDuplicate
                ? $"[ItemDatabaseBuilder] Actualizado con {found.Count} items — hay IDs duplicados, revisá los warnings de arriba."
                : $"[ItemDatabaseBuilder] Actualizado: {found.Count} items, sin duplicados.");
        }
    }
}