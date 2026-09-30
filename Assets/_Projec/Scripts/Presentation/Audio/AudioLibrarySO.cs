using UnityEngine;

namespace Game.Presentation.Audio
{
    /// <summary>
    /// Todos los sonidos de UI y de juego (fuera de los de cada habilidad, que viven en su AbilitySO)
    /// en un solo lugar: para cambiar un sonido se arrastra otro clip acá. La instancia vive en
    /// Resources/AudioLibrary (la crea Game/Audio/Build Default Audio Library) y la carga GameAudio.
    /// Un campo vacío simplemente no suena.
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Audio/Audio Library", fileName = "AudioLibrary")]
    public class AudioLibrarySO : ScriptableObject
    {
        [Header("UI")]
        public AudioClip UiClick;
        public AudioClip UiHover;
        public AudioClip UiOpen;
        public AudioClip UiClose;
        public AudioClip UiError;
        public AudioClip UiNotice;
        public AudioClip MatchFound;

        [Header("Inventario")]
        public AudioClip InventoryOpen;
        public AudioClip InventoryClose;
        public AudioClip ItemMove;
        public AudioClip[] ItemEquip;
        public AudioClip ItemDrop;
        public AudioClip ItemPickup;
        public AudioClip Sort;

        [Header("Loot")]
        public AudioClip ContainerOpen;
        [Tooltip("Suena al abrir un contenedor con al menos un item raro.")]
        public AudioClip RareLoot;
        [Tooltip("Suena al abrir un contenedor con al menos un item épico (en vez del raro).")]
        public AudioClip EpicLoot;

        [Header("Combate")]
        [Tooltip("Hechizo rechazado: sin maná o en cooldown.")]
        public AudioClip CastRejected;

        [Header("Movimiento")]
        public AudioClip[] Footsteps;
        public AudioClip Land;

        [Header("Run")]
        public AudioClip ExtractionComplete;
        public AudioClip Died;

        [Header("Volúmenes")]
        [Range(0f, 1f)] public float UiVolume = 0.6f;
        [Range(0f, 1f)] public float HoverVolume = 0.25f;
        [Range(0f, 1f)] public float FootstepVolume = 0.45f;
    }
}
