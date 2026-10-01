using UnityEngine;

namespace Game.Presentation.Player
{
    /// <summary>
    /// Sensibilidad del mouse elegida por el jugador (multiplicador sobre la base del prefab),
    /// guardada en PlayerPrefs. La aplica PlayerMovementController al leer la mirada local.
    /// </summary>
    public static class LookSettings
    {
        private const string SensitivityKey = "SpellHaul.MouseSensitivity";

        public const float MinSensitivity = 0.2f;
        public const float MaxSensitivity = 3f;

        private const string InvertYKey = "SpellHaul.InvertY";

        private static float? _cached;
        private static bool? _invertY;

        /// <summary>Invierte el eje vertical de la mirada.</summary>
        public static bool InvertY
        {
            get => _invertY ??= PlayerPrefs.GetInt(InvertYKey, 0) == 1;
            set
            {
                _invertY = value;
                PlayerPrefs.SetInt(InvertYKey, value ? 1 : 0);
            }
        }

        public static float Sensitivity
        {
            get => _cached ??= Mathf.Clamp(PlayerPrefs.GetFloat(SensitivityKey, 1f), MinSensitivity, MaxSensitivity);
            set
            {
                _cached = Mathf.Clamp(value, MinSensitivity, MaxSensitivity);
                PlayerPrefs.SetFloat(SensitivityKey, _cached.Value);
            }
        }
    }
}
