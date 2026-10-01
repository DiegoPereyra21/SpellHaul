using System.Collections.Generic;
using Game.Core.Economy;
using Game.Core.Items;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools.Economy
{
    /// <summary>
    /// Herramientas de la economía del hub:
    /// - Game/Economy/Generate Default Economy: crea los materiales elementales que falten y el
    ///   asset Resources/EconomyConfig con las recetas y ofertas por defecto que no existan (por Id).
    ///   No pisa lo que ya editaste a mano.
    /// - Game/Economy/Copy Economy JSON: copia al portapapeles lo que CloudScript necesita para
    ///   validar (valor y stack de cada item, recetas, ofertas). Se pega en Title Data, clave
    ///   "Economy". Volver a copiarlo cada vez que cambie el asset o se agreguen items.
    /// </summary>
    public static class EconomyTools
    {
        private const string MaterialsFolder = "Assets/_Projec/Scripts/Core/Items/Samples/Material";
        private const string ResourcesFolder = "Assets/_Projec/Resources";
        private const string ConfigPath = ResourcesFolder + "/EconomyConfig.asset";

        // ---------- Generación ----------

        private struct MaterialDef { public string Id, Name, Description; }

        // Un material por escuela de guante: los sueltan enemigos y cofres.
        private static readonly MaterialDef[] ElementalMaterials =
        {
            new MaterialDef { Id = "ember_shard", Name = "Ember Shard", Description = "A splinter of living fire. Used to craft Fire gloves." },
            new MaterialDef { Id = "living_sap", Name = "Living Sap", Description = "Sap that still pulses with life. Used for healing potions and Nature gloves." },
            new MaterialDef { Id = "light_dust", Name = "Light Dust", Description = "Glittering dust that never stops glowing. Used for mana potions and Light gloves." },
            new MaterialDef { Id = "earth_core", Name = "Earth Core", Description = "A dense heart of stone. Used to craft Earth gloves." },
        };

        [MenuItem("Game/Economy/Generate Default Economy")]
        public static void GenerateDefaultEconomy()
        {
            int createdMaterials = CreateMaterials();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            var items = LoadItemsById();
            var config = AssetDatabase.LoadAssetAtPath<EconomyConfigSO>(ConfigPath);
            if (config == null)
            {
                if (!AssetDatabase.IsValidFolder(ResourcesFolder)) AssetDatabase.CreateFolder("Assets/_Projec", "Resources");
                config = ScriptableObject.CreateInstance<EconomyConfigSO>();
                AssetDatabase.CreateAsset(config, ConfigPath);
            }

            var so = new SerializedObject(config);
            int recipes = AddMissingRecipes(so.FindProperty("_recipes"), items, out var missing);
            int offers = AddMissingOffers(so.FindProperty("_offers"), items, missing);
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(config);
            AssetDatabase.SaveAssets();

            if (missing.Count > 0)
                Debug.LogWarning($"[EconomyTools] Faltan items para algunas recetas u ofertas (generá antes el catálogo): {string.Join(", ", missing)}");
            Debug.Log($"[EconomyTools] Materiales creados: {createdMaterials}. Recetas nuevas: {recipes}. Ofertas nuevas: {offers}. Asset: {ConfigPath}");
            Selection.activeObject = config;
        }

        private static int CreateMaterials()
        {
            int created = 0;
            foreach (var m in ElementalMaterials)
            {
                string path = $"{MaterialsFolder}/{m.Id}.asset";
                if (AssetDatabase.LoadAssetAtPath<ItemSO>(path) != null) continue;
                if (FindItem(m.Id) != null) continue; // ya existe en otra carpeta

                var item = ScriptableObject.CreateInstance<ItemSO>();
                var so = new SerializedObject(item);
                so.FindProperty("_itemId").stringValue = m.Id;
                so.FindProperty("_displayName").stringValue = m.Name;
                so.FindProperty("_description").stringValue = m.Description;
                so.FindProperty("_category").enumValueIndex = (int)ItemCategory.Material;
                so.FindProperty("_rarity").enumValueIndex = (int)Rarity.Rare;
                so.FindProperty("_isStackable").boolValue = true;
                so.FindProperty("_maxStack").intValue = 10;
                so.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.CreateAsset(item, path);
                created++;
            }
            return created;
        }

        private struct RecipeDef
        {
            public string Id, Output;
            public (string id, int qty)[] Inputs;
            public int Gold;
        }

        private static List<RecipeDef> DefaultRecipes()
        {
            var list = new List<RecipeDef>();
            void R(string output, int gold, params (string, int)[] inputs)
                => list.Add(new RecipeDef { Id = "craft_" + output, Output = output, Inputs = inputs, Gold = gold });

            // Pociones.
            R("health_potion_common", 10, ("black_wood", 2), ("living_sap", 1));
            R("health_potion_rare", 40, ("black_wood", 2), ("living_sap", 3));
            R("health_potion_epic", 120, ("living_sap", 4), ("red_crystal", 1));
            R("mana_potion_common", 10, ("black_wood", 2), ("light_dust", 1));
            R("mana_potion_rare", 40, ("black_wood", 2), ("light_dust", 3));
            R("mana_potion_epic", 120, ("light_dust", 4), ("red_crystal", 1));

            // Guantes: el Common desde cero; Rare y Epic mejoran el de la rareza anterior.
            void Glove(string family, string material)
            {
                R($"{family}_common", 50, ("black_wood", 3), (material, 2));
                R($"{family}_rare", 150, ($"{family}_common", 1), (material, 4), ("red_crystal", 1));
                R($"{family}_epic", 400, ($"{family}_rare", 1), (material, 6), ("red_crystal", 2));
            }
            Glove("orb_gloves", "ember_shard");
            Glove("mending_gloves", "living_sap");
            Glove("flare_gloves", "light_dust");
            Glove("stone_gloves", "earth_core");
            return list;
        }

        private static int AddMissingRecipes(SerializedProperty prop, Dictionary<string, ItemSO> items, out List<string> missing)
        {
            missing = new List<string>();
            var existing = new HashSet<string>();
            for (int i = 0; i < prop.arraySize; i++)
                existing.Add(prop.GetArrayElementAtIndex(i).FindPropertyRelative("Id").stringValue);

            int added = 0;
            foreach (var def in DefaultRecipes())
            {
                if (existing.Contains(def.Id)) continue;
                if (!items.TryGetValue(def.Output, out var output)) { missing.Add(def.Output); continue; }

                bool ok = true;
                foreach (var (id, _) in def.Inputs)
                    if (!items.ContainsKey(id)) { missing.Add(id); ok = false; }
                if (!ok) continue;

                int idx = prop.arraySize;
                prop.InsertArrayElementAtIndex(idx);
                var el = prop.GetArrayElementAtIndex(idx);
                el.FindPropertyRelative("Id").stringValue = def.Id;
                el.FindPropertyRelative("Output").objectReferenceValue = output;
                el.FindPropertyRelative("OutputQuantity").intValue = 1;
                el.FindPropertyRelative("Gold").intValue = def.Gold;
                var inputs = el.FindPropertyRelative("Inputs");
                inputs.arraySize = def.Inputs.Length;
                for (int i = 0; i < def.Inputs.Length; i++)
                {
                    var input = inputs.GetArrayElementAtIndex(i);
                    input.FindPropertyRelative("Item").objectReferenceValue = items[def.Inputs[i].id];
                    input.FindPropertyRelative("Quantity").intValue = def.Inputs[i].qty;
                }
                added++;
            }
            return added;
        }

        private static int AddMissingOffers(SerializedProperty prop, Dictionary<string, ItemSO> items, List<string> missing)
        {
            var defaults = new (string id, string item, int qty, int price)[]
            {
                ("buy_health_potion_common", "health_potion_common", 1, 40),
                ("buy_mana_potion_common", "mana_potion_common", 1, 40),
                ("buy_black_wood", "black_wood", 1, 15),
            };

            var existing = new HashSet<string>();
            for (int i = 0; i < prop.arraySize; i++)
                existing.Add(prop.GetArrayElementAtIndex(i).FindPropertyRelative("Id").stringValue);

            int added = 0;
            foreach (var d in defaults)
            {
                if (existing.Contains(d.id)) continue;
                if (!items.TryGetValue(d.item, out var item)) { missing.Add(d.item); continue; }
                int idx = prop.arraySize;
                prop.InsertArrayElementAtIndex(idx);
                var el = prop.GetArrayElementAtIndex(idx);
                el.FindPropertyRelative("Id").stringValue = d.id;
                el.FindPropertyRelative("Item").objectReferenceValue = item;
                el.FindPropertyRelative("Quantity").intValue = d.qty;
                el.FindPropertyRelative("Price").intValue = d.price;
                added++;
            }
            return added;
        }

        // ---------- Exportación a Title Data ----------

        [System.Serializable] private class JsonItem { public string ItemId; public int Value; public int MaxStack; }
        [System.Serializable] private class JsonStack { public string ItemId; public int Quantity; }
        [System.Serializable] private class JsonRecipe { public string Id; public string Output; public int OutputQuantity; public List<JsonStack> Inputs = new(); public int Gold; }
        [System.Serializable] private class JsonOffer { public string Id; public string ItemId; public int Quantity; public int Price; }
        [System.Serializable]
        private class JsonEconomy
        {
            public float SellRate;
            public List<JsonItem> Items = new();
            public List<JsonRecipe> Recipes = new();
            public List<JsonOffer> Offers = new();
        }

        [MenuItem("Game/Economy/Copy Economy JSON")]
        public static void CopyEconomyJson()
        {
            var config = AssetDatabase.LoadAssetAtPath<EconomyConfigSO>(ConfigPath);
            if (config == null)
            {
                EditorUtility.DisplayDialog("Economy", $"No existe {ConfigPath}. Corré antes Game/Economy/Generate Default Economy.", "OK");
                return;
            }

            var json = new JsonEconomy { SellRate = config.SellRate };
            foreach (var item in LoadItemsById().Values)
                json.Items.Add(new JsonItem { ItemId = item.ItemId, Value = config.ValueOf(item), MaxStack = item.MaxStack });

            var problems = new List<string>();
            foreach (var r in config.Recipes)
            {
                if (string.IsNullOrEmpty(r.Id) || r.Output == null) { problems.Add($"receta sin Id u Output ({r.Id})"); continue; }
                var jr = new JsonRecipe { Id = r.Id, Output = r.Output.ItemId, OutputQuantity = r.OutputQuantity, Gold = r.Gold };
                foreach (var input in r.Inputs)
                {
                    if (input.Item == null) { problems.Add($"{r.Id}: ingrediente vacío"); continue; }
                    jr.Inputs.Add(new JsonStack { ItemId = input.Item.ItemId, Quantity = input.Quantity });
                }
                json.Recipes.Add(jr);
            }
            foreach (var o in config.Offers)
            {
                if (string.IsNullOrEmpty(o.Id) || o.Item == null) { problems.Add($"oferta sin Id o Item ({o.Id})"); continue; }
                json.Offers.Add(new JsonOffer { Id = o.Id, ItemId = o.Item.ItemId, Quantity = o.Quantity, Price = o.Price });
            }

            string text = JsonUtility.ToJson(json);
            EditorGUIUtility.systemCopyBuffer = text;
            if (problems.Count > 0) Debug.LogWarning($"[EconomyTools] Entradas salteadas: {string.Join("; ", problems)}");
            Debug.Log($"[EconomyTools] Copiado al portapapeles ({text.Length} caracteres): Title Data → Economy.");
            EditorUtility.DisplayDialog("Economy",
                "JSON copiado al portapapeles.\n\nPegalo en PlayFab: Title settings → Title Data, clave \"Economy\".", "OK");
        }

        // ---------- Utilidades ----------

        private static Dictionary<string, ItemSO> LoadItemsById()
        {
            var map = new Dictionary<string, ItemSO>();
            foreach (string guid in AssetDatabase.FindAssets("t:ItemSO"))
            {
                var item = AssetDatabase.LoadAssetAtPath<ItemSO>(AssetDatabase.GUIDToAssetPath(guid));
                if (item != null && !string.IsNullOrEmpty(item.ItemId)) map[item.ItemId] = item;
            }
            return map;
        }

        private static ItemSO FindItem(string id)
        {
            LoadItemsById().TryGetValue(id, out var item);
            return item;
        }
    }
}
