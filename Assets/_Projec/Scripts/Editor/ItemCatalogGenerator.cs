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
            created += GeneratePotions(ref skipped);

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

            EnsureAbilityAssets();

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

        private struct PotionDef
        {
            public string ItemId, DisplayName, Description;
            public Rarity Rarity;
            public float Health, Mana, Duration, UseTime;
        }

        /// <summary>
        /// Pociones de vida y maná en 3 rarezas (Minor / normal / Greater). Apilan hasta 5, se
        /// toman en 1 s (0.8 / 0.6 las mejores) y restauran en 3 s. Valores de partida.
        /// </summary>
        private static List<PotionDef> BuildPotions()
        {
            var list = new List<PotionDef>();
            void Add(string id, string name, string desc, Rarity r, float hp, float mana, float useTime)
                => list.Add(new PotionDef { ItemId = id, DisplayName = name, Description = desc, Rarity = r, Health = hp, Mana = mana, Duration = 3f, UseTime = useTime });

            Add("health_potion_common", "Minor Health Potion", "Restores health over a few seconds.", Rarity.Common, 35f, 0f, 1f);
            Add("health_potion_rare", "Health Potion", "Restores health over a few seconds.", Rarity.Rare, 55f, 0f, 0.8f);
            Add("health_potion_epic", "Greater Health Potion", "Restores health over a few seconds.", Rarity.Epic, 80f, 0f, 0.6f);
            Add("mana_potion_common", "Minor Mana Potion", "Restores mana over a few seconds.", Rarity.Common, 0f, 30f, 1f);
            Add("mana_potion_rare", "Mana Potion", "Restores mana over a few seconds.", Rarity.Rare, 0f, 50f, 0.8f);
            Add("mana_potion_epic", "Greater Mana Potion", "Restores mana over a few seconds.", Rarity.Epic, 0f, 75f, 0.6f);
            return list;
        }

        private static int GeneratePotions(ref int skipped)
        {
            string folderPath = $"{ItemsRoot}/Consumables";
            if (!AssetDatabase.IsValidFolder(folderPath)) CreateFolderRecursive(folderPath);

            // Sonido al tomarla: el clip "heal" del proyecto si existe (se puede cambiar en el asset).
            AudioClip drinkClip = null;
            foreach (string guid in AssetDatabase.FindAssets("heal t:AudioClip"))
            {
                drinkClip = AssetDatabase.LoadAssetAtPath<AudioClip>(AssetDatabase.GUIDToAssetPath(guid));
                if (drinkClip != null) break;
            }

            int created = 0;
            foreach (var def in BuildPotions())
            {
                string assetPath = $"{folderPath}/{def.ItemId}.asset";
                if (AssetDatabase.LoadAssetAtPath<ItemSO>(assetPath) != null) { skipped++; continue; }

                var instance = ScriptableObject.CreateInstance<PotionItemSO>();
                var so = new SerializedObject(instance);
                so.FindProperty("_itemId").stringValue = def.ItemId;
                so.FindProperty("_displayName").stringValue = def.DisplayName;
                so.FindProperty("_description").stringValue = def.Description;
                so.FindProperty("_category").enumValueIndex = (int)ItemCategory.Consumable;
                so.FindProperty("_rarity").enumValueIndex = (int)def.Rarity;
                so.FindProperty("_isStackable").boolValue = true;
                so.FindProperty("_maxStack").intValue = 5;
                so.FindProperty("_useTime").floatValue = def.UseTime;
                so.FindProperty("_useClip").objectReferenceValue = drinkClip;
                so.FindProperty("_health").floatValue = def.Health;
                so.FindProperty("_mana").floatValue = def.Mana;
                so.FindProperty("_duration").floatValue = def.Duration;
                so.ApplyModifiedProperties();

                AssetDatabase.CreateAsset(instance, assetPath);
                created++;
            }
            return created;
        }

        private const string AbilitiesFolder = "Assets/_Projec/ScriptableObjects/Abilities";

        /// <summary>
        /// Crea las AbilitySO que usan los guantes y todavía no existen (por AbilityId), con valores
        /// de partida. Lo que necesita arrastrarse a mano (prefab, VFX, audio) queda avisado en consola.
        /// </summary>
        private static void EnsureAbilityAssets()
        {
            var existing = new HashSet<string>();
            foreach (string guid in AssetDatabase.FindAssets("t:AbilitySO"))
            {
                var a = AssetDatabase.LoadAssetAtPath<Game.Core.Abilities.AbilitySO>(AssetDatabase.GUIDToAssetPath(guid));
                if (a != null && !string.IsNullOrEmpty(a.AbilityId)) existing.Add(a.AbilityId);
            }

            if (!existing.Contains("id_flashorb"))
            {
                if (!AssetDatabase.IsValidFolder(AbilitiesFolder)) CreateFolderRecursive(AbilitiesFolder);
                var flash = ScriptableObject.CreateInstance<Game.Core.Abilities.Abilities.FlashOrbAbilitySO>();
                var so = new SerializedObject(flash);
                so.FindProperty("_abilityId").stringValue = "id_flashorb";
                so.FindProperty("_displayName").stringValue = "Flash Orb";
                so.FindProperty("_cooldown").floatValue = 14f;     // largo a propósito: es control fuerte
                so.FindProperty("_resourceCost").floatValue = 30f;
                so.ApplyModifiedProperties();
                AssetDatabase.CreateAsset(flash, $"{AbilitiesFolder}/Ability_FlashOrb_.asset");
                Debug.LogWarning("[ItemCatalogGenerator] Creado Ability_FlashOrb_: asignale el prefab del proyectil (FlashProjectile) y, si querés, los sonidos de casteo/detonación.", flash);
            }

            if (!existing.Contains("id_earthwall"))
            {
                if (!AssetDatabase.IsValidFolder(AbilitiesFolder)) CreateFolderRecursive(AbilitiesFolder);
                var wall = ScriptableObject.CreateInstance<Game.Core.Abilities.Abilities.EarthWallAbilitySO>();
                var so = new SerializedObject(wall);
                so.FindProperty("_abilityId").stringValue = "id_earthwall";
                so.FindProperty("_displayName").stringValue = "Earth Wall";
                so.FindProperty("_cooldown").floatValue = 12f;
                so.FindProperty("_resourceCost").floatValue = 35f;
                so.ApplyModifiedProperties();
                AssetDatabase.CreateAsset(wall, $"{AbilitiesFolder}/Ability_EarthWall_.asset");
                Debug.LogWarning("[ItemCatalogGenerator] Creado Ability_EarthWall_: asignale el prefab del muro (EarthWall).", wall);
            }
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
                GloveSchool.Fire, "id_chargedorb", new[]
            {
                (Rarity.Common, 1.0f, 1.0f),
                (Rarity.Rare,   1.2f, 0.9f),
                (Rarity.Epic,   1.45f, 0.8f),
            });

            AddFamily("mending_gloves", "Mending Gloves",
                "Right click to heal yourself instantly.",
                GloveSchool.Nature, "id_heal", new[]
            {
                (Rarity.Common, 1.0f, 1.0f),
                (Rarity.Rare,   1.3f, 0.9f),
                (Rarity.Epic,   1.6f, 0.8f),
            });

            AddFamily("flare_gloves", "Flare Gloves",
                "Right click to launch a slow orb of light. Right click again to detonate it mid-air; it also bursts on impact. The flash blinds everyone who looks at it, you included.",
                GloveSchool.Light, "id_flashorb", new[]
            {
                (Rarity.Common, 1.0f, 1.0f),
                (Rarity.Rare,   1.2f, 0.9f),
                (Rarity.Epic,   1.4f, 0.8f),
            });

            AddFamily("stone_gloves", "Stone Gloves",
                "Right click to raise an earth wall where you aim (or right in front of you). It blocks movement, projectiles and sight for everyone until it breaks or crumbles.",
                GloveSchool.Earth, "id_earthwall", new[]
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