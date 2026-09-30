using UnityEngine;

namespace Game.Presentation.Audio
{
    /// <summary>
    /// Volúmenes del jugador por categoría, guardados en PlayerPrefs:
    /// - Master: todo el juego (AudioListener).
    /// - Effects: sonidos del mundo (hechizos, impactos, pasos, golpes).
    /// - Interface: menús, inventario y avisos.
    /// Quien reproduce un sonido multiplica por la categoría que corresponde (ver GameAudio,
    /// VFXManager, HUDController).
    /// </summary>
    public static class AudioVolumes
    {
        private const string MasterKey = "SpellHaul.Volume.Master";
        private const string EffectsKey = "SpellHaul.Volume.Effects";
        private const string InterfaceKey = "SpellHaul.Volume.Interface";

        public static float Master
        {
            get => PlayerPrefs.GetFloat(MasterKey, 0.8f);
            set { PlayerPrefs.SetFloat(MasterKey, Mathf.Clamp01(value)); Apply(); }
        }

        public static float Effects
        {
            get => PlayerPrefs.GetFloat(EffectsKey, 1f);
            set => PlayerPrefs.SetFloat(EffectsKey, Mathf.Clamp01(value));
        }

        public static float Interface
        {
            get => PlayerPrefs.GetFloat(InterfaceKey, 1f);
            set => PlayerPrefs.SetFloat(InterfaceKey, Mathf.Clamp01(value));
        }

        /// <summary>Guarda en disco (llamar al cerrar el panel de opciones, no en cada movimiento del slider).</summary>
        public static void Save() => PlayerPrefs.Save();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Apply() => AudioListener.volume = Master;
    }
}
