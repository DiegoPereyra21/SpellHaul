using System.Collections.Generic;
using UnityEngine;

namespace Game.Presentation.Settings
{
    /// <summary>
    /// Ajustes de pantalla del jugador. Modo de ventana y resolución los guarda Unity solo (se
    /// aplican con Screen); VSync, límite de FPS y calidad gráfica se guardan acá (PlayerPrefs) y se
    /// aplican al arrancar. En un servidor dedicado no se toca nada.
    /// </summary>
    public static class DisplaySettings
    {
        private const string VSyncKey = "SpellHaul.Display.VSync";
        private const string FrameLimitKey = "SpellHaul.Display.FrameLimit";
        private const string QualityKey = "SpellHaul.Display.Quality";

        public const int DefaultFrameLimit = 144;

        /// <summary>Opciones de modo de ventana, en el orden del menú.</summary>
        public static readonly (string Label, FullScreenMode Mode)[] WindowModes =
        {
            ("Fullscreen", FullScreenMode.ExclusiveFullScreen),
            ("Borderless", FullScreenMode.FullScreenWindow),
            ("Windowed", FullScreenMode.Windowed),
        };

        /// <summary>Límites de FPS ofrecidos (0 = sin límite).</summary>
        public static readonly int[] FrameLimits = { 30, 60, 120, 144, 165, 240, 0 };

        private static bool IsHeadless => Application.isBatchMode || Game.Presentation.Bootstrap.LaunchArgs.IsDedicatedServer;

        // ---------- Ventana y resolución ----------

        public static FullScreenMode WindowMode => Screen.fullScreenMode;

        public static void SetWindowMode(FullScreenMode mode)
        {
            if (IsHeadless) return;
            // Pantalla completa sin bordes usa la resolución nativa del monitor; el resto, la elegida.
            if (mode == FullScreenMode.FullScreenWindow)
                Screen.SetResolution(Display.main.systemWidth, Display.main.systemHeight, mode);
            else
                Screen.SetResolution(Screen.width, Screen.height, mode);
        }

        /// <summary>Resoluciones disponibles (sin repetir por frecuencia), de mayor a menor.</summary>
        public static List<Vector2Int> Resolutions()
        {
            var list = new List<Vector2Int>();
            foreach (var r in Screen.resolutions)
            {
                var size = new Vector2Int(r.width, r.height);
                if (!list.Contains(size)) list.Add(size);
            }
            list.Sort((a, b) => b.x != a.x ? b.x.CompareTo(a.x) : b.y.CompareTo(a.y));
            if (list.Count == 0) list.Add(new Vector2Int(Screen.width, Screen.height));
            return list;
        }

        public static Vector2Int CurrentResolution => new Vector2Int(Screen.width, Screen.height);

        public static void SetResolution(Vector2Int size)
        {
            if (IsHeadless) return;
            Screen.SetResolution(size.x, size.y, Screen.fullScreenMode);
        }

        // ---------- VSync, FPS y calidad ----------

        public static bool VSync
        {
            get => PlayerPrefs.GetInt(VSyncKey, 0) == 1;
            set { PlayerPrefs.SetInt(VSyncKey, value ? 1 : 0); ApplyFrameSettings(); }
        }

        /// <summary>Límite de FPS (0 = sin límite). No aplica con VSync activo.</summary>
        public static int FrameLimit
        {
            get => PlayerPrefs.GetInt(FrameLimitKey, DefaultFrameLimit);
            set { PlayerPrefs.SetInt(FrameLimitKey, Mathf.Max(0, value)); ApplyFrameSettings(); }
        }

        public static int Quality
        {
            get => Mathf.Clamp(PlayerPrefs.GetInt(QualityKey, QualitySettings.GetQualityLevel()), 0, QualitySettings.names.Length - 1);
            set
            {
                int level = Mathf.Clamp(value, 0, QualitySettings.names.Length - 1);
                PlayerPrefs.SetInt(QualityKey, level);
                if (!IsHeadless) QualitySettings.SetQualityLevel(level, true);
                ApplyFrameSettings(); // cambiar de nivel de calidad puede pisar el VSync
            }
        }

        /// <summary>FPS objetivo según los ajustes (-1 = sin límite / lo maneja el VSync).</summary>
        public static int TargetFrameRate => VSync || FrameLimit <= 0 ? -1 : FrameLimit;

        public static void Save() => PlayerPrefs.Save();

        private static void ApplyFrameSettings()
        {
            if (IsHeadless) return;
            QualitySettings.vSyncCount = VSync ? 1 : 0;
            Application.targetFrameRate = TargetFrameRate;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void ApplyOnStartup()
        {
            if (IsHeadless) return;
            if (PlayerPrefs.HasKey(QualityKey)) QualitySettings.SetQualityLevel(Quality, true);
            ApplyFrameSettings();

            // FishNet fija su propio límite de FPS al conectar (500 en el cliente): se vuelve a
            // aplicar el del jugador cada vez que algo lo cambia.
            var go = new GameObject("DisplaySettings");
            Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideInHierarchy;
            go.AddComponent<FrameRateKeeper>();
        }

        private sealed class FrameRateKeeper : MonoBehaviour
        {
            private void LateUpdate()
            {
                int target = TargetFrameRate;
                if (Application.targetFrameRate != target) Application.targetFrameRate = target;
                int vsync = VSync ? 1 : 0;
                if (QualitySettings.vSyncCount != vsync) QualitySettings.vSyncCount = vsync;
            }
        }
    }
}
