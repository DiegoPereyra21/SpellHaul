using System.IO;
using Game.Presentation.Audio;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools.Audio
{
    /// <summary>
    /// Crea (o completa) Resources/AudioLibrary con una elección inicial de los packs de Kenney.
    /// Solo llena los campos VACÍOS: lo que ya se cambió a mano en el asset no se pisa. Para volver
    /// a la elección por defecto de un campo, vaciarlo y correr el menú de nuevo.
    /// </summary>
    public static class AudioLibraryBuilder
    {
        private const string ResourcesFolder = "Assets/_Projec/Resources";
        private const string AssetPath = ResourcesFolder + "/AudioLibrary.asset";

        private const string Interface = "Assets/_Projec/Audio/KenneySounds/kenney_interface-sounds/Audio/";
        private const string Ui = "Assets/_Projec/Audio/KenneySounds/kenney_ui-audio/Audio/";
        private const string Rpg = "Assets/_Projec/Audio/KenneySounds/kenney_rpg-audio/Audio/";

        [MenuItem("Game/Audio/Build Default Audio Library")]
        public static void Build()
        {
            if (!AssetDatabase.IsValidFolder(ResourcesFolder))
                AssetDatabase.CreateFolder("Assets/_Projec", "Resources");

            var lib = AssetDatabase.LoadAssetAtPath<AudioLibrarySO>(AssetPath);
            bool created = lib == null;
            if (created)
            {
                lib = ScriptableObject.CreateInstance<AudioLibrarySO>();
                AssetDatabase.CreateAsset(lib, AssetPath);
            }

            int missing = 0;

            // UI
            Fill(ref lib.UiClick, Interface + "click_002.ogg", ref missing);
            Fill(ref lib.UiHover, Ui + "rollover2.ogg", ref missing);
            Fill(ref lib.UiOpen, Interface + "open_002.ogg", ref missing);
            Fill(ref lib.UiClose, Interface + "close_002.ogg", ref missing);
            Fill(ref lib.UiError, Interface + "error_004.ogg", ref missing);
            Fill(ref lib.UiNotice, Interface + "question_001.ogg", ref missing);
            Fill(ref lib.MatchFound, Interface + "confirmation_002.ogg", ref missing);

            // Inventario
            Fill(ref lib.InventoryOpen, Rpg + "clothBelt.ogg", ref missing);
            Fill(ref lib.InventoryClose, Rpg + "clothBelt2.ogg", ref missing);
            Fill(ref lib.ItemMove, Interface + "drop_002.ogg", ref missing);
            FillArray(ref lib.ItemEquip, ref missing, Rpg + "cloth1.ogg", Rpg + "cloth2.ogg", Rpg + "cloth3.ogg", Rpg + "cloth4.ogg");
            Fill(ref lib.ItemDrop, Rpg + "dropLeather.ogg", ref missing);
            Fill(ref lib.ItemPickup, Rpg + "handleSmallLeather.ogg", ref missing);
            Fill(ref lib.Sort, Interface + "scroll_002.ogg", ref missing);

            // Loot
            Fill(ref lib.ContainerOpen, Rpg + "beltHandle1.ogg", ref missing);
            Fill(ref lib.RareLoot, Interface + "glass_002.ogg", ref missing);
            Fill(ref lib.EpicLoot, Interface + "glass_006.ogg", ref missing);

            // Combate
            Fill(ref lib.CastRejected, Interface + "error_006.ogg", ref missing);

            // Movimiento
            var steps = new string[10];
            for (int i = 0; i < steps.Length; i++) steps[i] = Rpg + $"footstep0{i}.ogg";
            FillArray(ref lib.Footsteps, ref missing, steps);
            Fill(ref lib.Land, Rpg + "footstep05.ogg", ref missing);

            // Run
            Fill(ref lib.ExtractionComplete, Interface + "confirmation_004.ogg", ref missing);
            Fill(ref lib.Died, Interface + "error_008.ogg", ref missing);

            EditorUtility.SetDirty(lib);
            AssetDatabase.SaveAssets();
            Selection.activeObject = lib;

            Debug.Log($"[AudioLibraryBuilder] {(created ? "Creado" : "Actualizado")} {AssetPath}." +
                      (missing > 0 ? $" {missing} clips no se encontraron (ver warnings)." : string.Empty));
        }

        private static void Fill(ref AudioClip field, string path, ref int missing)
        {
            if (field != null) return;
            field = Load(path, ref missing);
        }

        private static void FillArray(ref AudioClip[] field, ref int missing, params string[] paths)
        {
            if (field != null && field.Length > 0) return;
            var list = new System.Collections.Generic.List<AudioClip>();
            foreach (var p in paths)
            {
                var clip = Load(p, ref missing);
                if (clip != null) list.Add(clip);
            }
            field = list.ToArray();
        }

        private static AudioClip Load(string path, ref int missing)
        {
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            if (clip == null)
            {
                missing++;
                Debug.LogWarning($"[AudioLibraryBuilder] No se encontró {Path.GetFileName(path)} en {Path.GetDirectoryName(path)}.");
            }
            return clip;
        }
    }
}
