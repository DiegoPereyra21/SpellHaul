using System.Collections.Generic;
using Game.Core.Items;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools.Items
{
    /// <summary>
    /// Genera (o regenera) las tablas de loot de cada fuente —enemigo cuerpo a cuerpo, enemigo a distancia, guardián
    /// planta y cofre— a partir de TODOS los ItemSO del proyecto y un perfil de probabilidades por
    /// rareza/categoría definido acá abajo. Así un item nuevo entra solo a las tablas y el balance
    /// se toca en un único lugar (Profiles). Volver a correrlo pisa las entradas de estas tablas.
    /// </summary>
    public static class LootTableGenerator
    {
        private const string Folder = "Assets/_Projec/Scripts/Core/Items/LootTable";

        /// <summary>Probabilidad por entrada (rolls independientes, ver LootTableSO.Roll).</summary>
        private struct Profile
        {
            public string AssetName;
            public float Common, Rare, Epic;        // equipamiento (incluye pockets), por item
            public float Material;                  // categoría Material (apilables)
            public int MaterialMin, MaterialMax;
            public float Resource;                  // categoría Resource
            public float PotionCommon, PotionRare, PotionEpic; // consumibles (pociones), por item
            public bool GuaranteeAtLeastOne;
        }

        // Valores de partida para balancear jugando. Con el catálogo actual (13 items por rareza,
        // pockets incluidos) el esperado por fuente es aprox.:
        //   Melee:     0.33 comunes · 0.10 raros · 0.03 épicos · madera 45% · cristal 10%
        //   Ranged:    0.39 comunes · 0.16 raros · 0.04 épicos · madera 50% · cristal 15%
        //   Guardián:  0.52 comunes · 0.23 raros · 0.07 épicos · madera 60% · cristal 25%
        //   Cofre:     0.78 comunes · 0.39 raros · 0.10 épicos · madera 50% · cristal 30% (nunca vacío)
        private static readonly Profile[] Profiles =
        {
            new Profile { AssetName = "LootTable_Enemy_Melee",         Common = 0.025f, Rare = 0.008f, Epic = 0.002f, Material = 0.45f, MaterialMin = 1, MaterialMax = 3, Resource = 0.10f, PotionCommon = 0.10f, PotionRare = 0.03f, PotionEpic = 0.010f, GuaranteeAtLeastOne = false },
            new Profile { AssetName = "LootTable_Enemy_Ranged",        Common = 0.03f,  Rare = 0.012f, Epic = 0.003f, Material = 0.50f, MaterialMin = 1, MaterialMax = 3, Resource = 0.15f, PotionCommon = 0.12f, PotionRare = 0.04f, PotionEpic = 0.010f, GuaranteeAtLeastOne = false },
            new Profile { AssetName = "LootTable_Enemy_PlantGuardian", Common = 0.04f,  Rare = 0.018f, Epic = 0.005f, Material = 0.60f, MaterialMin = 2, MaterialMax = 4, Resource = 0.25f, PotionCommon = 0.18f, PotionRare = 0.06f, PotionEpic = 0.020f, GuaranteeAtLeastOne = false },
            new Profile { AssetName = "LootTable_Chest",               Common = 0.06f,  Rare = 0.03f,  Epic = 0.008f, Material = 0.50f, MaterialMin = 2, MaterialMax = 5, Resource = 0.30f, PotionCommon = 0.25f, PotionRare = 0.10f, PotionEpic = 0.030f, GuaranteeAtLeastOne = true },
        };

        [MenuItem("Game/Items/Generate Loot Tables")]
        public static void Generate()
        {
            var items = LoadAllItems();
            if (items.Count == 0)
            {
                Debug.LogWarning("[LootTableGenerator] No hay ItemSO en el proyecto.");
                return;
            }

            if (!AssetDatabase.IsValidFolder(Folder))
            {
                Debug.LogError($"[LootTableGenerator] No existe la carpeta {Folder}.");
                return;
            }

            foreach (var profile in Profiles)
                WriteTable(profile, items);

            AssetDatabase.SaveAssets();
            Debug.Log($"[LootTableGenerator] {Profiles.Length} tablas generadas con {items.Count} items del proyecto.");
        }

        private static List<ItemSO> LoadAllItems()
        {
            var result = new List<ItemSO>();
            foreach (string guid in AssetDatabase.FindAssets("t:ItemSO"))
            {
                var item = AssetDatabase.LoadAssetAtPath<ItemSO>(AssetDatabase.GUIDToAssetPath(guid));
                if (item != null && !string.IsNullOrEmpty(item.ItemId))
                    result.Add(item);
            }
            result.Sort((a, b) => string.CompareOrdinal(a.ItemId, b.ItemId)); // orden estable entre corridas
            return result;
        }

        private static void WriteTable(Profile profile, List<ItemSO> items)
        {
            string path = $"{Folder}/{profile.AssetName}.asset";
            var table = AssetDatabase.LoadAssetAtPath<LootTableSO>(path);
            if (table == null)
            {
                table = ScriptableObject.CreateInstance<LootTableSO>();
                AssetDatabase.CreateAsset(table, path);
            }

            var so = new SerializedObject(table);
            var entries = so.FindProperty("_entries");
            entries.ClearArray();

            foreach (var item in items)
            {
                if (!TryGetEntry(profile, item, out float chance, out int min, out int max)) continue;

                int i = entries.arraySize;
                entries.InsertArrayElementAtIndex(i);
                var el = entries.GetArrayElementAtIndex(i);
                el.FindPropertyRelative("Item").objectReferenceValue = item;
                el.FindPropertyRelative("Chance").floatValue = chance;
                el.FindPropertyRelative("MinQuantity").intValue = min;
                el.FindPropertyRelative("MaxQuantity").intValue = max;
            }

            so.FindProperty("_guaranteeAtLeastOne").boolValue = profile.GuaranteeAtLeastOne;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(table);
        }

        /// <summary>Probabilidad y cantidades de un item según el perfil. False = no entra a la tabla.</summary>
        private static bool TryGetEntry(Profile p, ItemSO item, out float chance, out int min, out int max)
        {
            min = max = 1;

            if (item.IsEquipment)
            {
                chance = item.Rarity switch
                {
                    Rarity.Rare => p.Rare,
                    Rarity.Epic => p.Epic,
                    _ => p.Common
                };
                return chance > 0f;
            }

            switch (item.Category)
            {
                case ItemCategory.Material:
                    chance = p.Material;
                    min = Mathf.Clamp(p.MaterialMin, 1, item.MaxStack);
                    max = Mathf.Clamp(p.MaterialMax, min, item.MaxStack);
                    return chance > 0f;
                case ItemCategory.Resource:
                    chance = p.Resource;
                    return chance > 0f;
                case ItemCategory.Consumable:
                    // Pociones por rareza. Los consumibles muy raros (ej. la piedra de escape, a
                    // futuro) van a tener su propia regla.
                    if (item is not PotionItemSO) { chance = 0f; return false; }
                    chance = item.Rarity switch
                    {
                        Rarity.Rare => p.PotionRare,
                        Rarity.Epic => p.PotionEpic,
                        _ => p.PotionCommon
                    };
                    max = item.Rarity == Rarity.Common ? Mathf.Min(2, item.MaxStack) : 1;
                    return chance > 0f;
                default:
                    chance = 0f; // Misc: no dropea
                    return false;
            }
        }
    }
}
