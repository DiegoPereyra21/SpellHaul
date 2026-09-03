using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Game.Core.Items;

namespace Game.EditorTools.Items
{
    /// <summary>
    /// Genera el catálogo completo de equipamiento base (Boots/Hat/Robe/Glove, 3 arquetipos
    /// x 3 rarezas cada uno) leyendo una tabla de datos hardcodeada acá abajo. No pisa nada
    /// que ya exista (matchea por path); correrlo de nuevo después de agregar más entradas
    /// a BuildCatalog() solo crea lo nuevo.
    /// </summary>
    public class ItemCatalogGenerator
    {
        private struct StatEntry
        {
            public StatType Stat;
            public float Value;
            public StatEntry(StatType stat, float value) { Stat = stat; Value = value; }
        }

        private struct ItemDef
        {
            public string Folder;
            public EquipmentSlot Slot;
            public string GloveType; // null si no es Glove
            public string ItemId;
            public string DisplayName;
            public Rarity Rarity;
            public StatEntry[] Modifiers;
        }

        [MenuItem("Game/Items/Generate Equipment Catalog")]
        public static void Generate()
        {
            var defs = BuildCatalog();
            int created = 0, skipped = 0;

            foreach (var def in defs)
            {
                string folderPath = $"Assets/_Projec/Scripts/Core/Items/Samples/{def.Folder}";
                if (!AssetDatabase.IsValidFolder(folderPath))
                    CreateFolderRecursive(folderPath);

                string assetPath = $"{folderPath}/{def.ItemId}.asset";
                if (AssetDatabase.LoadAssetAtPath<ItemSO>(assetPath) != null)
                {
                    skipped++;
                    continue;
                }

                ScriptableObject instance = def.GloveType != null
                    ? ScriptableObject.CreateInstance<GloveItemSO>()
                    : ScriptableObject.CreateInstance<EquipmentItemSO>();

                var so = new SerializedObject(instance);
                so.FindProperty("_itemId").stringValue = def.ItemId;
                so.FindProperty("_displayName").stringValue = def.DisplayName;
                so.FindProperty("_category").enumValueIndex =
                    (int)(def.Slot == EquipmentSlot.Glove ? ItemCategory.Glove : ItemCategory.Equipment);
                so.FindProperty("_rarity").enumValueIndex = (int)def.Rarity;
                so.FindProperty("_isStackable").boolValue = false;
                so.FindProperty("_maxStack").intValue = 1;
                so.FindProperty("_slot").enumValueIndex = (int)def.Slot;
                so.FindProperty("_pocketSlots").intValue = 0;

                var modsProp = so.FindProperty("_modifiers");
                modsProp.arraySize = def.Modifiers.Length;
                for (int i = 0; i < def.Modifiers.Length; i++)
                {
                    var el = modsProp.GetArrayElementAtIndex(i);
                    el.FindPropertyRelative("Stat").enumValueIndex = (int)def.Modifiers[i].Stat;
                    el.FindPropertyRelative("Value").floatValue = def.Modifiers[i].Value;
                }

                if (def.GloveType != null)
                {
                    so.FindProperty("_gloveType").enumValueIndex =
                        (int)System.Enum.Parse(typeof(GloveType), def.GloveType);
                }

                so.ApplyModifiedProperties();
                AssetDatabase.CreateAsset(instance, assetPath);
                created++;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[ItemCatalogGenerator] Creados: {created}, ya existían (salteados): {skipped}.");
        }

        private static void CreateFolderRecursive(string path)
        {
            var parts = path.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        private static List<ItemDef> BuildCatalog()
        {
            var list = new List<ItemDef>();

            void AddFamily(string folder, EquipmentSlot slot, string gloveType, string idPrefix, string namePrefix,
                (Rarity rarity, StatEntry[] mods)[] tiers)
            {
                foreach (var (rarity, mods) in tiers)
                {
                    list.Add(new ItemDef
                    {
                        Folder = folder,
                        Slot = slot,
                        GloveType = gloveType,
                        ItemId = $"{idPrefix}_{rarity.ToString().ToLowerInvariant()}",
                        DisplayName = $"{rarity} {namePrefix}",
                        Rarity = rarity,
                        Modifiers = mods
                    });
                }
            }

            AddFamily("Boots", EquipmentSlot.Boots, null, "swift_boots", "Swift Boots", new[]
            {
                (Rarity.Common, new[]{ new StatEntry(StatType.MoveSpeed, 0.75f) }),
                (Rarity.Rare,   new[]{ new StatEntry(StatType.MoveSpeed, 1.2f) }),
                (Rarity.Epic,   new[]{ new StatEntry(StatType.MoveSpeed, 1.8f) }),
            });
            AddFamily("Boots", EquipmentSlot.Boots, null, "heavy_boots", "Heavy Boots", new[]
            {
                (Rarity.Common, new[]{ new StatEntry(StatType.MoveSpeed, -0.5f), new StatEntry(StatType.Protection, 0.08f) }),
                (Rarity.Rare,   new[]{ new StatEntry(StatType.MoveSpeed, -0.8f), new StatEntry(StatType.Protection, 0.12f) }),
                (Rarity.Epic,   new[]{ new StatEntry(StatType.MoveSpeed, -1.2f), new StatEntry(StatType.Protection, 0.18f) }),
            });
            AddFamily("Boots", EquipmentSlot.Boots, null, "focus_boots", "Focus Boots", new[]
            {
                (Rarity.Common, new[]{ new StatEntry(StatType.JumpForce, 1.3f) }),
                (Rarity.Rare,   new[]{ new StatEntry(StatType.JumpForce, 2f) }),
                (Rarity.Epic,   new[]{ new StatEntry(StatType.JumpForce, 3f) }),
            });

            AddFamily("Hat", EquipmentSlot.Hat, null, "swift_hat", "Swift Hat", new[]
            {
                (Rarity.Common, new[]{ new StatEntry(StatType.CastSpeedMultiplier, 0.05f) }),
                (Rarity.Rare,   new[]{ new StatEntry(StatType.CastSpeedMultiplier, 0.08f) }),
                (Rarity.Epic,   new[]{ new StatEntry(StatType.CastSpeedMultiplier, 0.12f) }),
            });
            AddFamily("Hat", EquipmentSlot.Hat, null, "heavy_hat", "Heavy Hat", new[]
            {
                (Rarity.Common, new[]{ new StatEntry(StatType.CastSpeedMultiplier, -0.03f), new StatEntry(StatType.Protection, 0.08f) }),
                (Rarity.Rare,   new[]{ new StatEntry(StatType.CastSpeedMultiplier, -0.05f), new StatEntry(StatType.Protection, 0.12f) }),
                (Rarity.Epic,   new[]{ new StatEntry(StatType.CastSpeedMultiplier, -0.08f), new StatEntry(StatType.Protection, 0.18f) }),
            });
            AddFamily("Hat", EquipmentSlot.Hat, null, "focus_hat", "Focus Hat", new[]
            {
                (Rarity.Common, new[]{ new StatEntry(StatType.ManaRegen, 1.3f) }),
                (Rarity.Rare,   new[]{ new StatEntry(StatType.ManaRegen, 2f) }),
                (Rarity.Epic,   new[]{ new StatEntry(StatType.ManaRegen, 3f) }),
            });

            AddFamily("Robe", EquipmentSlot.Robe, null, "swift_robe", "Swift Robe", new[]
            {
                (Rarity.Common, new[]{ new StatEntry(StatType.DamageMultiplier, 0.05f) }),
                (Rarity.Rare,   new[]{ new StatEntry(StatType.DamageMultiplier, 0.08f) }),
                (Rarity.Epic,   new[]{ new StatEntry(StatType.DamageMultiplier, 0.12f) }),
            });
            AddFamily("Robe", EquipmentSlot.Robe, null, "heavy_robe", "Heavy Robe", new[]
            {
                (Rarity.Common, new[]{ new StatEntry(StatType.DamageMultiplier, -0.03f), new StatEntry(StatType.Protection, 0.08f) }),
                (Rarity.Rare,   new[]{ new StatEntry(StatType.DamageMultiplier, -0.05f), new StatEntry(StatType.Protection, 0.12f) }),
                (Rarity.Epic,   new[]{ new StatEntry(StatType.DamageMultiplier, -0.08f), new StatEntry(StatType.Protection, 0.18f) }),
            });
            AddFamily("Robe", EquipmentSlot.Robe, null, "focus_robe", "Focus Robe", new[]
            {
                (Rarity.Common, new[]{ new StatEntry(StatType.ManaRegen, 1.3f) }),
                (Rarity.Rare,   new[]{ new StatEntry(StatType.ManaRegen, 2f) }),
                (Rarity.Epic,   new[]{ new StatEntry(StatType.ManaRegen, 3f) }),
            });

            AddFamily("Gloves", EquipmentSlot.Glove, "Swift", "swift_gloves", "Swift Gloves", new[]
            {
                (Rarity.Common, new[]{ new StatEntry(StatType.DamageMultiplier, -0.2f), new StatEntry(StatType.CastSpeedMultiplier, 0.25f) }),
                (Rarity.Rare,   new[]{ new StatEntry(StatType.DamageMultiplier, -0.3f), new StatEntry(StatType.CastSpeedMultiplier, 0.4f) }),
                (Rarity.Epic,   new[]{ new StatEntry(StatType.DamageMultiplier, -0.45f), new StatEntry(StatType.CastSpeedMultiplier, 0.6f) }),
            });
            AddFamily("Gloves", EquipmentSlot.Glove, "Heavy", "heavy_gloves", "Heavy Gloves", new[]
            {
                (Rarity.Common, new[]{ new StatEntry(StatType.DamageMultiplier, 0.3f), new StatEntry(StatType.CastSpeedMultiplier, -0.2f) }),
                (Rarity.Rare,   new[]{ new StatEntry(StatType.DamageMultiplier, 0.5f), new StatEntry(StatType.CastSpeedMultiplier, -0.3f) }),
                (Rarity.Epic,   new[]{ new StatEntry(StatType.DamageMultiplier, 0.75f), new StatEntry(StatType.CastSpeedMultiplier, -0.45f) }),
            });
            AddFamily("Gloves", EquipmentSlot.Glove, "Focus", "focus_gloves", "Focus Gloves", new[]
            {
                (Rarity.Common, new[]{ new StatEntry(StatType.ManaRegen, 0.15f) }),
                (Rarity.Rare,   new[]{ new StatEntry(StatType.ManaRegen, 0.2f) }),
                (Rarity.Epic,   new[]{ new StatEntry(StatType.ManaRegen, 0.3f) }),
            });

            return list;
        }
    }
}