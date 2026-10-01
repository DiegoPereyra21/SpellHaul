using UnityEngine;

namespace Game.Core.Abilities.Abilities
{
    [CreateAssetMenu(menuName = "Game/Abilities/Self Heal Ability", fileName = "Ability_SelfHeal_")]
    public class SelfHealAbilitySO : AbilitySO
    {
        [Header("Curación")]
        [SerializeField] private float _healAmount = 30f;

        public override void Execute(AbilityExecutor executor, in AbilityCastContext context)
        {
            executor.ApplySelfEffect(context.CasterNetworkId, _healAmount * context.AbilityPower);
        }

        public override void DescribeEffect(float power, System.Collections.Generic.List<string> lines)
        {
            lines.Add($"Heals {_healAmount * power:0}");
        }
    }
}
