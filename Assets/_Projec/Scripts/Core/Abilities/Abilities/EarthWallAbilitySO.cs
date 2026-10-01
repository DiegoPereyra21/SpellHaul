using UnityEngine;

namespace Game.Core.Abilities.Abilities
{
    /// <summary>
    /// Muro de tierra: levanta un muro del suelo donde apunta el caster (si es suelo y está a
    /// alcance) o, si no, a una distancia fija delante de él. Mira hacia el caster, dura un tiempo
    /// y se puede romper. Bloquea proyectiles, movimiento y visión de todos. La potencia del guante
    /// escala vida y duración.
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Abilities/Earth Wall Ability", fileName = "Ability_EarthWall_")]
    public class EarthWallAbilitySO : AbilitySO
    {
        [Header("Muro")]
        [Tooltip("Prefab con EarthWall + Health + NetworkObject.")]
        [SerializeField] private GameObject _wallPrefab;
        [Tooltip("Vida base del muro (antes de la rareza).")]
        [SerializeField] private float _health = 150f;
        [Tooltip("Segundos que dura el muro (antes de la rareza).")]
        [SerializeField] private float _duration = 6f;

        [Header("Ubicación")]
        [Tooltip("Distancia máxima (m) al suelo apuntado.")]
        [SerializeField] private float _maxRange = 15f;
        [Tooltip("Si no se apunta a un suelo válido a alcance: distancia (m) delante del jugador.")]
        [SerializeField] private float _fallbackDistance = 4f;

        public override void Execute(AbilityExecutor executor, in AbilityCastContext context)
        {
            executor.SpawnEarthWall(
                _wallPrefab,
                context.Origin,
                context.AimPoint,
                context.AimDirection,
                _maxRange,
                _fallbackDistance,
                _health * context.AbilityPower,
                _duration * context.AbilityPower,
                context.CasterNetworkId);
        }

        public override void DescribeEffect(float power, System.Collections.Generic.List<string> lines)
        {
            lines.Add($"Wall health {_health * power:0}");
            lines.Add($"Lasts {_duration * power:0.#}s");
            lines.Add($"Range {_maxRange:0} m");
        }
    }
}
