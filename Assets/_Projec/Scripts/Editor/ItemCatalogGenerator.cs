using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Game.Core.Items;

namespace Game.EditorTools.Items
{
    /// <summary>
    /// Genera el catálogo completo de equipamiento base (Boots/Hat/Robe: 3 arquetipos x 3
    /// rarezas) y de guantes (una familia por habilidad x 3 rarezas) leyendo las tablas de
    /// datos de acá abajo. No pisa nada que ya exista (matchea por path); correrlo de nuevo
    /// después de agregar más entradas solo crea lo nuevo.
    ///
    /// Guante nuevo: agregar una familia en BuildGloves() con su escuela y el AbilityId de una
    /// AbilitySO existente; la rareza solo cambia potencia y cooldown.
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
            public string ItemId;
            public string DisplayName;
            public Rarity Rarity;
            public StatEntry[] Modifiers;
        }

        private struct GloveDef
        {
            public string ItemId;
            public string DisplayName;
            public string Description;
            public GloveSchool School;
            public string AbilityId;   // AbilitySO._abilityId
            public Rarity Rarity;
            public float Power;        // multiplicador de daño/curación
            public float Cooldown;     // multiplicador de cooldown
        }

        private const string ItemsRoot = "Assets/_Projec/Scripts/Core/Items/Samples";

        [MenuItem("Game/Items/Generate Equipment Catalog")]
        public static void Generate()
        {
            var defs = BuildCatalog();
            int created = 0, skipped = 0;

            foreach (var def in defs)
            {
                string folderPath = $"{ItemsRoot}/{def.Folder}";
                if (!AssetDatabase.IsValidFolder(folderPath))
                    CreateFolderRecursive(folderPath);

                string assetPath = $"{folderPath}/{def.ItemId}.asset";
                if (AssetDatabase.LoadAssetAtPath<ItemSO>(assetPath) != null)
                {
                    skipped++;
                    continue;
                }

                ScriptableObject instance = ScriptableObject.CreateInstance<EquipmentItemSO>();

                var so = new SerializedObject(instance);
                so.FindProperty("_itemId").stringValue = def.ItemId;
                so.FindProperty("_displayName").stringValue = def.DisplayName;
                so.FindProperty("_category").enumValueIndex = (int)ItemCategory.Equipment;
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

                so.ApplyModifiedProperties();
                AssetDatabase.CreateAsset(instance, assetPath);
                created++;
            }

            int glovesCreated = GenerateGloves(ref skipped);
            created += glovesCreated;

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[ItemCatalogGenerator] Creados: {created}, ya existían (salteados): {skipped}.");
        }

        /// <summary>Crea los guantes de BuildGloves() que falten. Devuelve cuántos creó.</summary>
        private static int GenerateGloves(ref int skipped)
        {
            string folderPath = $"{ItemsRoot}/Gloves";
            if (!AssetDatabase.IsValidFolder(folderPath))
                CreateFolderRecursive(folderPath);

            var abilities = new Dictionary<string, Game.Core.Abilities.AbilitySO>();
            foreach (string guid in AssetDatabase.FindAssets("t:AbilitySO"))
            {
                var ability = AssetDatabase.LoadAssetAtPath<Game.Core.Abilities.AbilitySO>(AssetDatabase.GUIDToAssetPath(guid));
                if (ability != null && !string.IsNullOrEmpty(ability.AbilityId))
                    abilities[ability.AbilityId] = ability;
            }

            int created = 0;
            foreach (var def in BuildGloves())
            {
                string assetPath = $"{folderPath}/{def.ItemId}.asset";
                if (AssetDatabase.LoadAssetAtPath<ItemSO>(assetPath) != null)
                {
                    skipped++;
                    continue;
                }

                if (!abilities.TryGetValue(def.AbilityId, out var abilityAsset))
                {
                    Debug.LogError($"[ItemCatalogGenerator] {def.ItemId}: no existe una AbilitySO con AbilityId '{def.AbilityId}'. No se crea.");
                    continue;
                }

                var instance = ScriptableObject.CreateInstance<GloveItemSO>();
                var so = new SerializedObject(instance);
                so.FindProperty("_itemId").stringValue = def.ItemId;
                so.FindProperty("_displayName").stringValue = def.DisplayName;
                so.FindProperty("_description").stringValue = def.Description;
                so.FindProperty("_category").enumValueIndex = (int)ItemCategory.Glove;
                so.FindProperty("_rarity").enumValueIndex = (int)def.Rarity;
                so.FindProperty("_isStackable").boolValue = false;
                so.FindProperty("_maxStack").intValue = 1;
                so.FindProperty("_slot").enumValueIndex = (int)EquipmentSlot.Glove;
                so.FindProperty("_pocketSlots").intValue = 0;
                so.FindProperty("_modifiers").arraySize = 0;
                so.FindProperty("_school").enumValueIndex = (int)def.School;
                so.FindProperty("_ability").objectReferenceValue = abilityAsset;
                so.FindProperty("_abilityPower").floatValue = def.Power;
                so.FindProperty("_cooldownMultiplier").floatValue = def.Cooldown;
                so.ApplyModifiedProperties();

                AssetDatabase.CreateAsset(instance, assetPath);
                created++;
            }
            return created;
        }

        /// <summary>
        /// Guantes: una familia por habilidad. La rareza escala la potencia (daño/curación) y
        /// acorta el cooldown. Valores de partida para balancear jugando.
        /// </summary>
        private static List<GloveDef> BuildGloves()
        {
            var list = new List<GloveDef>();

            void AddFamily(string idPrefix, string displayName, string description, GloveSchool school, string abilityId,
                (Rarity rarity, float power, float cooldown)[] tiers)
            {
                foreach (var (rarity, power, cooldown) in tiers)
                {
                    list.Add(new GloveDef
                    {
                        ItemId = $"{idPrefix}_{rarity.ToString().ToLowerInvariant()}",
                        DisplayName = displayName,
                        Description = description,
                        School = school,
                        AbilityId = abilityId,
                        Rarity = rarity,
                        Power = power,
                        Cooldown = cooldown
                    });
                }
            }

            AddFamily("orb_gloves", "Orb Gloves",
                "Hold right click to charge an orb, release to throw it. It explodes on impact; a longer charge hits harder and flies farther.",
                GloveSchool.Destruction, "id_chargedorb", new[]
            {
                (Rarity.Common, 1.0f, 1.0f),
                (Rarity.Rare,   1.2f, 0.9f),
                (Rarity.Epic,   1.45f, 0.8f),
            });

            AddFamily("mending_gloves", "Mending Gloves",
                "Right click to heal yourself instantly.",
                GloveSchool.Restoration, "id_heal", new[]
            {
                (Rarity.Common, 1.0f, 1.0f),
                (Rarity.Rare,   1.3f, 0.9f),
                (Rarity.Epic,   1.6f, 0.8f),
            });

            return list;
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

            void AddFamily(string folder, EquipmentSlot slot, string idPrefix, string namePrefix,
                (Rarity rarity, StatEntry[] mods)[] tiers)
            {
                foreach (var (rarity, mods) in tiers)
                {
                    list.Add(new ItemDef
                    {
                        Folder = folder,
                        Slot = slot,
                        ItemId = $"{idPrefix}_{rarity.ToString().ToLowerInvariant()}",
                        // Sin la rareza en el nombre: la rareza ya se lee por el color de la celda/texto.
                        DisplayName = namePrefix,
                        Rarity = rarity,
                        Modifiers = mods
                    });
                }
            }

            AddFamily("Boots", EquipmentSlot.Boots, "swift_boots", "Swift Boots", new[]
            {
                (Rarity.Common, new[]{ new StatEntry(StatType.MoveSpeed, 0.75f) }),
                (Rarity.Rare,   new[]{ new StatEntry(StatType.MoveSpeed, 1.2f) }),
                (Rarity.Epic,   new[]{ new StatEntry(StatType.MoveSpeed, 1.8f) }),
            });
            AddFamily("Boots", EquipmentSlot.Boots, "heavy_boots", "Heavy Boots", new[]
            {
                (Rarity.Common, new[]{ new StatEntry(StatType.MoveSpeed, -0.5f), new StatEntry(StatType.Protection, 0.08f) }),
                (Rarity.Rare,   new[]{ new StatEntry(StatType.MoveSpeed, -0.8f), new StatEntry(StatType.Protection, 0.12f) }),
                (Rarity.Epic,   new[]{ new StatEntry(StatType.MoveSpeed, -1.2f), new StatEntry(StatType.Protection, 0.18f) }),
            });
            AddFamily("Boots", EquipmentSlot.Boots, "focus_boots", "Focus Boots", new[]
            {
                (Rarity.Common, new[]{ new StatEntry(StatType.JumpForce, 1.3f) }),
                (Rarity.Rare,   new[]{ new StatEntry(StatType.JumpForce, 2f) }),
                (Rarity.Epic,   new[]{ new StatEntry(StatType.JumpForce, 3f) }),
            });

            AddFamily("Hat", EquipmentSlot.Hat, "swift_hat", "Swift Hat", new[]
            {
                (Rarity.Common, new[]{ new StatEntry(StatType.CastSpeedMultiplier, 0.05f) }),
                (Rarity.Rare,   new[]{ new StatEntry(StatType.CastSpeedMultiplier, 0.08f) }),
                (Rarity.Epic,   new[]{ new StatEntry(StatType.CastSpeedMultiplier, 0.12f) }),
            });
            AddFamily("Hat", EquipmentSlot.Hat, "heavy_hat", "Heavy Hat", new[]
            {
                (Rarity.Common, new[]{ new StatEntry(StatType.CastSpeedMultiplier, -0.03f), new StatEntry(StatType.Protection, 0.08f) }),
                (Rarity.Rare,   new[]{ new StatEntry(StatType.CastSpeedMultiplier, -0.05f), new StatEntry(StatType.Protection, 0.12f) }),
                (Rarity.Epic,   new[]{ new StatEntry(StatType.CastSpeedMultiplier, -0.08f), new StatEntry(StatType.Protection, 0.18f) }),
            });
            AddFamily("Hat", EquipmentSlot.Hat, "focus_hat", "Focus Hat", new[]
            {
                (Rarity.Common, new[]{ new StatEntry(StatType.ManaRegen, 1.3f) }),
                (Rarity.Rare,   new[]{ new StatEntry(StatType.ManaRegen, 2f) }),
                (Rarity.Epic,   new[]{ new StatEntry(StatType.ManaRegen, 3f) }),
            });

            AddFamily("Robe", EquipmentSlot.Robe, "swift_robe", "Swift Robe", new[]
            {
                (Rarity.Common, new[]{ new StatEntry(StatType.DamageMultiplier, 0.05f) }),
                (Rarity.Rare,   new[]{ new StatEntry(StatType.DamageMultiplier, 0.08f) }),
                (Rarity.Epic,   new[]{ new StatEntry(StatType.DamageMultiplier, 0.12f) }),
            });
            AddFamily("Robe", EquipmentSlot.Robe, "heavy_robe", "Heavy Robe", new[]
            {
                (Rarity.Common, new[]{ new StatEntry(StatType.DamageMultiplier, -0.03f), new StatEntry(StatType.Protection, 0.08f) }),
                (Rarity.Rare,   new[]{ new StatEntry(StatType.DamageMultiplier, -0.05f), new StatEntry(StatType.Protection, 0.12f) }),
                (Rarity.Epic,   new[]{ new StatEntry(StatType.DamageMultiplier, -0.08f), new StatEntry(StatType.Protection, 0.18f) }),
            });
            AddFamily("Robe", EquipmentSlot.Robe, "focus_robe", "Focus Robe", new[]
            {
                (Rarity.Common, new[]{ new StatEntry(StatType.ManaRegen, 1.3f) }),
                (Rarity.Rare,   new[]{ new StatEntry(StatType.ManaRegen, 2f) }),
                (Rarity.Epic,   new[]{ new StatEntry(StatType.ManaRegen, 3f) }),
            });

            return list;
        }
    }
}