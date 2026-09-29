using Game.Core.Items;
using Game.Presentation.Run;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools.Items
{
    /// <summary>
    /// Copia al portapapeles el loadout inicial (StartingKitSO) en el mismo JSON que se guarda en
    /// PlayFab. Se pega en Title Data con la clave "StarterKit": de ahí lo toma CloudScript
    /// (EnsureProfile) para los jugadores nuevos. Volver a copiarlo cada vez que cambie el kit.
    /// </summary>
    public static class StarterKitExporter
    {
        [MenuItem("Game/Items/Copy Starter Kit JSON")]
        public static void CopyStarterKitJson()
        {
            string[] guids = AssetDatabase.FindAssets("t:StartingKitSO");
            if (guids.Length == 0)
            {
                EditorUtility.DisplayDialog("Starter Kit", "No se encontró ningún StartingKitSO en el proyecto.", "OK");
                return;
            }
            if (guids.Length > 1)
                Debug.LogWarning($"[StarterKitExporter] Hay {guids.Length} StartingKitSO; se usa el primero: {AssetDatabase.GUIDToAssetPath(guids[0])}");

            var kit = AssetDatabase.LoadAssetAtPath<StartingKitSO>(AssetDatabase.GUIDToAssetPath(guids[0]));
            InventorySnapshot snapshot = PlayerLoadoutService.CreateStartingSnapshot(kit);
            string json = JsonUtility.ToJson(snapshot);

            EditorGUIUtility.systemCopyBuffer = json;
            Debug.Log($"[StarterKitExporter] Copiado al portapapeles (Title Data → StarterKit):\n{json}");
            EditorUtility.DisplayDialog("Starter Kit",
                "JSON copiado al portapapeles.\n\nPegalo en PlayFab: Title settings → Title Data, clave \"StarterKit\".", "OK");
        }
    }
}
