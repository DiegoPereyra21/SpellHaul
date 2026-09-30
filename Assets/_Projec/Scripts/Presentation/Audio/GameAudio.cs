using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Presentation.Audio
{
    /// <summary>
    /// Reproductor de sonidos sueltos (UI en 2D, mundo en 3D) sin tener que armar AudioSources en
    /// escenas ni prefabs: crea los suyos la primera vez que se usa y los mantiene entre escenas.
    /// Los clips salen de AudioLibrarySO (Resources/AudioLibrary). En un servidor dedicado no hace nada.
    /// </summary>
    public static class GameAudio
    {
        private const int PoolSize3D = 12;

        private static bool _initialized;
        private static AudioLibrarySO _library;
        private static AudioSource _source2D;
        private static AudioSource[] _pool3D;
        private static int _next3D;

        /// <summary>Biblioteca de sonidos (null si no existe o si es un servidor dedicado).</summary>
        public static AudioLibrarySO Library
        {
            get { EnsureInitialized(); return _library; }
        }

        private static void EnsureInitialized()
        {
            if (_initialized) return;
            _initialized = true;

            if (Application.isBatchMode || Game.Presentation.Bootstrap.LaunchArgs.IsDedicatedServer) return;

            _library = Resources.Load<AudioLibrarySO>("AudioLibrary");
            if (_library == null)
            {
                Debug.LogWarning("[GameAudio] No existe Resources/AudioLibrary: correr Game/Audio/Build Default Audio Library.");
                return;
            }

            var go = new GameObject("GameAudio");
            Object.DontDestroyOnLoad(go);

            _source2D = go.AddComponent<AudioSource>();
            _source2D.playOnAwake = false;
            _source2D.spatialBlend = 0f;

            _pool3D = new AudioSource[PoolSize3D];
            for (int i = 0; i < PoolSize3D; i++)
            {
                var child = new GameObject($"Source3D_{i}");
                child.transform.SetParent(go.transform);
                var src = child.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.spatialBlend = 1f;
                src.rolloffMode = AudioRolloffMode.Linear;
                src.minDistance = 2f;
                src.maxDistance = 35f;
                src.dopplerLevel = 0f;
                _pool3D[i] = src;
            }
        }

        /// <summary>Sonido de UI / del jugador local (sin posición).</summary>
        public static void Play2D(AudioClip clip, float volume = 1f, float pitchVariance = 0f)
        {
            EnsureInitialized();
            if (clip == null || _source2D == null) return;
            _source2D.pitch = pitchVariance > 0f ? 1f + Random.Range(-pitchVariance, pitchVariance) : 1f;
            _source2D.PlayOneShot(clip, volume);
        }

        /// <summary>Sonido en el mundo (se oye más fuerte cerca y hacia un lado u otro).</summary>
        public static void Play3D(AudioClip clip, Vector3 position, float volume = 1f, float pitchVariance = 0f)
        {
            EnsureInitialized();
            if (clip == null || _pool3D == null) return;

            AudioSource src = _pool3D[_next3D];
            _next3D = (_next3D + 1) % _pool3D.Length;

            src.transform.position = position;
            src.pitch = pitchVariance > 0f ? 1f + Random.Range(-pitchVariance, pitchVariance) : 1f;
            src.PlayOneShot(clip, volume);
        }

        /// <summary>Uno al azar de un grupo (ej. pasos, equipar).</summary>
        public static AudioClip Pick(AudioClip[] clips)
            => clips == null || clips.Length == 0 ? null : clips[Random.Range(0, clips.Length)];

        // ---------- Atajos de UI ----------

        public static void Ui(System.Func<AudioLibrarySO, AudioClip> select, float volumeScale = 1f)
        {
            var lib = Library;
            if (lib == null) return;
            Play2D(select(lib), lib.UiVolume * volumeScale);
        }

        /// <summary>
        /// Click y hover en todos los botones de este árbol de UI. Llamar una vez por documento
        /// (después de que exista su contenido). Los botones creados más tarde no quedan cubiertos.
        /// </summary>
        public static void AttachButtonSounds(VisualElement root)
        {
            if (root == null) return;
            root.Query<Button>().ForEach(button =>
            {
                if (button.ClassListContains("has-ui-sound")) return;
                button.AddToClassList("has-ui-sound");
                button.clicked += () => Ui(l => l.UiClick);
                button.RegisterCallback<PointerEnterEvent>(_ =>
                {
                    if (!button.enabledInHierarchy) return;
                    var lib = Library;
                    if (lib != null) Play2D(lib.UiHover, lib.HoverVolume);
                });
            });
        }
    }
}
