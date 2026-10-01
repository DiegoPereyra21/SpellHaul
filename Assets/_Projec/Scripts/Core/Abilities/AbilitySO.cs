using UnityEngine;

namespace Game.Core.Abilities
{
    /// <summary>
    /// Contrato común para toda habilidad. Se implementa vía ScriptableObject para
    /// permitir diseño data-driven (nuevas habilidades sin tocar código de sistema).
    /// </summary>
    public interface IAbility
    {
        string AbilityId { get; }
        float Cooldown { get; }
        float ResourceCost { get; }

        /// <summary>
        /// Ejecuta el efecto de la habilidad. Se llama SIEMPRE desde el servidor
        /// (autoridad) para el efecto real; el cliente solo predice feedback local (VFX/anim).
        /// </summary>
        void Execute(AbilityExecutor executor, in AbilityCastContext context);
    }

    /// <summary>
    /// Clase base abstracta para todas las habilidades como asset (ScriptableObject).
    /// Cada habilidad concreta hereda de acá e implementa su propio comportamiento:
    /// composición sobre herencia para EFECTOS (buffs/debuffs), pero herencia simple
    /// aquí porque todas comparten el mismo contrato de ejecución.
    /// </summary>
    public abstract class AbilitySO : ScriptableObject, IAbility
    {
        [Header("Identidad")]
        [SerializeField] private string _abilityId;
        [SerializeField] private string _displayName;
        [SerializeField] private Sprite _icon;

        [Header("Costos")]
        [SerializeField] private float _cooldown = 1f;
        [SerializeField] private float _resourceCost = 10f;
        [Tooltip("Segundos de carga antes de que el efecto ejecute de verdad. 0 = instantáneo (sin cambios). " +
                 "Server-authoritative: el cooldown y el maná se cobran al empezar la carga, no al terminar.")]
        [SerializeField] private float _windupDuration = 0f;
        public float WindupDuration => _windupDuration;

        [Header("VFX")]
        [SerializeField] private GameObject _muzzlePrefab;
        public GameObject MuzzlePrefab => _muzzlePrefab;

        [Header("Audio")]
        [Tooltip("Sonido al castear. Audible para todos los jugadores cercanos (3D).")]
        [SerializeField] private AudioClip _castClip;
        [Tooltip("Sonido al impactar/tener éxito: proyectil = impacto, orbe = explosión.")]
        [SerializeField] private AudioClip _impactClip;
        [Tooltip("Solo proyectil: sonido al pegar en geometría (pared/piso) en vez de un objetivo. Opcional.")]
        [SerializeField] private AudioClip _surfaceImpactClip;
        public AudioClip CastClip => _castClip;
        public AudioClip ImpactClip => _impactClip;
        public AudioClip SurfaceImpactClip => _surfaceImpactClip;
        
        public string AbilityId => _abilityId;
        public string DisplayName => _displayName;
        public Sprite Icon => _icon;
        public float Cooldown => _cooldown;
        public float ResourceCost => _resourceCost;

        /// <summary>
        /// Datos del proyectil cosmético local (visual-only) que el cliente del tirador spawnea
        /// para feedback instantáneo. Por defecto ninguna: solo las habilidades de proyectil lo dan.
        /// </summary>
        public virtual bool TryGetCosmeticProjectile(out GameObject prefab, out float speed)
        {
            prefab = null;
            speed = 0f;
            return false;
        }

        /// <summary>
        /// Datos de dash para que el OWNER lo prediga localmente al castear (Prediction v2:
        /// cliente predice, server aplica autoritativo, reconcile alinea). Por defecto no es dash.
        /// </summary>
        public virtual bool TryGetOwnerDash(Vector3 aimDirection, out Vector3 direction, out float speed, out float duration)
        {
            direction = default; speed = 0f; duration = 0f;
            return false;
        }
        /// <summary>
        /// True si la habilidad se castea manteniendo el botón y se dispara al soltar.
        /// El tiempo cargado lo mide el servidor, nunca el cliente.
        /// </summary>
        public virtual bool IsChargeable => false;

        /// <summary>Segundos hasta carga máxima (solo si IsChargeable).</summary>
        public virtual float MaxChargeDuration => 0f;

        /// <summary>True si esta habilidad quiere mostrar una previsualización de trayectoria mientras se carga.</summary>
        public virtual bool ShowTrajectoryPreview => false;

        /// <summary>
        /// Velocidad de lanzamiento y gravedad para una fracción de carga dada. El tiempo de
        /// vuelo se deriva de la distancia real (velocidad/distancia), no es un valor fijo —
        /// así un tiro lejano y uno cerca se sienten con la misma "fuerza de tiro".
        /// Solo si ShowTrajectoryPreview.
        /// </summary>
        public virtual void GetLaunchForCharge(float chargeNormalized, out float launchSpeed, out float gravity)
        {
            launchSpeed = 0f;
            gravity = 0f;
        }

        /// <summary>
        /// True si, mientras lo lanzado sigue activo, volver a apretar el botón lo activa (ej.
        /// detonar el orbe de destello en el aire) en vez de castear de nuevo.
        /// </summary>
        public virtual bool IsRecastable => false;

        /// <summary>Segundos después de castear durante los que se puede re-activar (solo si IsRecastable).</summary>
        public virtual float RecastWindow => 0f;

        /// <summary>
        /// Líneas de efecto para el tooltip (en inglés, las ve el jugador), con la potencia que
        /// aporta el guante ya aplicada. Ej: "Heals 39". Por defecto ninguna.
        /// </summary>
        public virtual void DescribeEffect(float power, System.Collections.Generic.List<string> lines) { }

        public abstract void Execute(AbilityExecutor executor, in AbilityCastContext context);
    }
}
