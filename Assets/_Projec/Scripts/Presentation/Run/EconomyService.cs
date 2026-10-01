using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Game.Presentation.Bootstrap;
using UnityEngine;

namespace Game.Presentation.Run
{
    /// <summary>
    /// Client-only. Oro del jugador y operaciones de la economía del hub (crafteo y vendedor).
    /// El oro vive en la Player Data de solo lectura "Wallet": lo escribe solo CloudScript (Craft,
    /// Sell, Buy). Cada operación espera a que el stash esté guardado, llama a CloudScript y relee
    /// stash + oro: la pantalla muestra siempre lo que dice PlayFab.
    /// </summary>
    public static class EconomyService
    {
        [Serializable]
        private class WalletData { public int Gold; }

        public static int Gold { get; private set; }
        public static bool GoldLoaded { get; private set; }

        /// <summary>Se dispara al cambiar el oro o el stash por una operación (redibujar).</summary>
        public static event Action OnChanged;

        /// <summary>True mientras haya una operación en curso (la UI no deja lanzar otra).</summary>
        public static bool Busy { get; private set; }

        /// <summary>La economía necesita la sesión de PlayFab (CloudScript).</summary>
        public static bool Available => PlayFabSession.IsReady;

        public static async Task<bool> ReloadGoldAsync()
        {
            if (!Available) return false;
            try
            {
                var data = await PlayFabUserData.ReadAsync(PlayFabUserData.WalletKey);
                Gold = data.TryGetValue(PlayFabUserData.WalletKey, out string json)
                    ? Mathf.Max(0, JsonUtility.FromJson<WalletData>(json)?.Gold ?? 0)
                    : 0;
                GoldLoaded = true;
                OnChanged?.Invoke();
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[EconomyService] No se pudo leer el oro: {e.Message}");
                return false;
            }
        }

        public static Task<string> CraftAsync(string recipeId)
            => RunAsync(PlayFabUserData.CraftFunction, new Dictionary<string, string> { { "RecipeId", recipeId } });

        public static Task<string> SellAsync(int stashIndex, string itemId)
            => RunAsync(PlayFabUserData.SellFunction, new Dictionary<string, string>
            {
                { "SlotIndex", stashIndex.ToString() },
                { "ItemId", itemId }, // si el slot cambió, el servidor no vende otra cosa
            });

        public static Task<string> BuyAsync(string offerId)
            => RunAsync(PlayFabUserData.BuyFunction, new Dictionary<string, string> { { "OfferId", offerId } });

        private const float SaveWaitSeconds = 15f;

        /// <summary>Null si salió bien; si no, el mensaje para el jugador (en inglés).</summary>
        private static async Task<string> RunAsync(string function, Dictionary<string, string> args)
        {
            if (!Available) return "Not connected to the game service.";
            if (Busy) return "Please wait...";
            Busy = true;
            try
            {
                // CloudScript lee el stash guardado: lo último que se movió tiene que haber llegado.
                float giveUpAt = Time.realtimeSinceStartup + SaveWaitSeconds;
                while (ProfileSaveQueue.Busy && Time.realtimeSinceStartup < giveUpAt)
                    await Task.Delay(100);
                if (ProfileSaveQueue.Busy) return "Your stash is still saving. Try again in a moment.";

                string error = null;
                try
                {
                    await PlayFabUserData.CallAsync(function, args);
                }
                catch (PlayFabUserData.RejectedException rejected)
                {
                    error = MessageFor(rejected.Reason);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[EconomyService] {function} falló: {e.Message}");
                    error = "Could not reach the game service. Try again.";
                }

                // Siempre releer: si falló, el cliente pudo estar desactualizado.
                await StashService.ReloadAsync();
                await ReloadGoldAsync();
                OnChanged?.Invoke();
                return error;
            }
            finally
            {
                Busy = false;
            }
        }

        private static string MessageFor(string reason) => reason switch
        {
            "not_enough_gold" => "Not enough gold.",
            "missing_materials" => "You don't have the materials in your stash.",
            "stash_full" => "Your stash is full.",
            "in_run" => "You can't do that while a run is in progress.",
            "no_economy" => "The trader is unavailable right now.",
            "slot_changed" or "empty_slot" => "That item is no longer there.",
            _ => "That action was rejected.",
        };
    }
}
