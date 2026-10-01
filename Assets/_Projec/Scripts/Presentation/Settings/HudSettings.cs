using UnityEngine;

namespace Game.Presentation.Settings
{
    /// <summary>
    /// Preferencias del HUD de la run: mira (color y tamaño), números de daño y contador de FPS /
    /// ping. Guardadas en PlayerPrefs; el HUD escucha Changed para aplicarlas al instante.
    /// </summary>
    public static class HudSettings
    {
        private const string CrosshairColorKey = "SpellHaul.Hud.CrosshairColor";
        private const string CrosshairSizeKey = "SpellHaul.Hud.CrosshairSize";
        private const string DamageNumbersKey = "SpellHaul.Hud.DamageNumbers";
        private const string NetStatsKey = "SpellHaul.Hud.NetStats";

        public static event System.Action Changed;

        /// <summary>Colores de mira ofrecidos, en el orden del menú.</summary>
        public static readonly (string Label, Color Color)[] CrosshairColors =
        {
            ("White", new Color(0.93f, 0.93f, 0.96f, 0.95f)),
            ("Green", new Color(0.35f, 1f, 0.45f, 0.95f)),
            ("Cyan", new Color(0.3f, 0.9f, 1f, 0.95f)),
            ("Yellow", new Color(1f, 0.92f, 0.3f, 0.95f)),
            ("Magenta", new Color(1f, 0.35f, 0.9f, 0.95f)),
            ("Red", new Color(1f, 0.3f, 0.3f, 0.95f)),
        };

        public const float MinCrosshairSize = 0.5f;
        public const float MaxCrosshairSize = 2.5f;

        public static int CrosshairColorIndex
        {
            get => Mathf.Clamp(PlayerPrefs.GetInt(CrosshairColorKey, 0), 0, CrosshairColors.Length - 1);
            set { PlayerPrefs.SetInt(CrosshairColorKey, Mathf.Clamp(value, 0, CrosshairColors.Length - 1)); Changed?.Invoke(); }
        }

        public static Color CrosshairColor => CrosshairColors[CrosshairColorIndex].Color;

        public static float CrosshairSize
        {
            get => Mathf.Clamp(PlayerPrefs.GetFloat(CrosshairSizeKey, 1f), MinCrosshairSize, MaxCrosshairSize);
            set { PlayerPrefs.SetFloat(CrosshairSizeKey, Mathf.Clamp(value, MinCrosshairSize, MaxCrosshairSize)); Changed?.Invoke(); }
        }

        public static bool ShowDamageNumbers
        {
            get => PlayerPrefs.GetInt(DamageNumbersKey, 1) == 1;
            set { PlayerPrefs.SetInt(DamageNumbersKey, value ? 1 : 0); Changed?.Invoke(); }
        }

        public static bool ShowNetStats
        {
            get => PlayerPrefs.GetInt(NetStatsKey, 0) == 1;
            set { PlayerPrefs.SetInt(NetStatsKey, value ? 1 : 0); Changed?.Invoke(); }
        }
    }
}
