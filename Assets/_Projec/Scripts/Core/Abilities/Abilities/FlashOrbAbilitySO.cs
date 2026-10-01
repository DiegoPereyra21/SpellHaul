using UnityEngine;

namespace Game.Core.Abilities.Abilities
{
    /// <summary>
    /// Orbe de destello: un proyectil lento que detona al chocar con cualquier cosa, al terminar
    /// su vida o cuando el caster vuelve a apretar el botón mientras vuela. La detonación no hace
    /// daño: ciega a quien la ve (jugadores según mirada y distancia, el propio caster incluido) y
    /// a la IA alcanzada. La potencia del guante escala la duración del cegado.
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Abilities/Flash Orb Ability", fileName = "Ability_FlashOrb_")]
    public class FlashOrbAbilitySO : AbilitySO
    {
        [Header("Proyectil")]
        [Tooltip("Prefab con FlashProjectile + NetworkObject.")]
        [SerializeField] private GameObject _projectilePrefab;
        [Tooltip("Velocidad del orbe (lento a propósito: se lo ve venir).")]
        [SerializeField] private float _speed = 12f;
        [Tooltip("Segundos de vuelo antes de detonar solo. También es la ventana para detonarlo a mano.")]
        [SerializeField] private float _lifetime = 3f;

        [Header("Destello")]
        [Tooltip("Radio (m) dentro del cual el destello afecta.")]
        [SerializeField] private float _flashRadius = 14f;
        [Tooltip("Segundos máximos de ceguera de un jugador que mira el destello de cerca (base, antes de la rareza).")]
        [SerializeField] private float _playerBlindSeconds = 3f;
        [Tooltip("Segundos que la IA alcanzada queda ciega (pierde el objetivo y no ataca).")]
        [SerializeField] private float _aiBlindSeconds = 4f;

        public override bool IsRecastable => true;
        public override float RecastWindow => _lifetime;

        public override void Execute(AbilityExecutor executor, in AbilityCastContext context)
        {
            // Sale del SpellOrigin autoritativo y converge hacia el punto de mira.
            Vector3 toAim = context.AimPoint - context.Origin;
            Vector3 direction = toAim.sqrMagnitude > 0.0001f ? toAim.normalized : context.AimDirection;

            executor.SpawnFlashProjectile(
                _projectilePrefab,
                context.Origin,
                direction,
                _speed,
                _lifetime,
                _flashRadius,
                _playerBlindSeconds * context.AbilityPower,
                _aiBlindSeconds * context.AbilityPower,
                context.CasterNetworkId,
                context.Slot);
        }

        public override void DescribeEffect(float power, System.Collections.Generic.List<string> lines)
        {
            lines.Add($"Blinds up to {_playerBlindSeconds * power:0.#}s");
            lines.Add($"Flash radius {_flashRadius:0} m");
            lines.Add("Click again to detonate mid-air");
        }
    }
}
