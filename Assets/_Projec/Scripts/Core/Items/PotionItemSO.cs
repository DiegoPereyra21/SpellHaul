using System.Collections.Generic;
using UnityEngine;

namespace Game.Core.Items
{
    /// <summary>
    /// Poción: restaura vida y/o maná repartidos en unos segundos después de tomarla. Las rarezas
    /// son assets distintos (Minor / normal / Greater) con más cantidad.
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Items/Potion", fileName = "Potion_")]
    public class PotionItemSO : ConsumableItemSO
    {
        [Header("Poción")]
        [Tooltip("Vida total que restaura.")]
        [SerializeField, Min(0f)] private float _health;
        [Tooltip("Maná total que restaura.")]
        [SerializeField, Min(0f)] private float _mana;
        [Tooltip("Segundos en los que se reparte la restauración (0 = instantánea).")]
        [SerializeField, Min(0f)] private float _duration = 3f;

        public override void Apply(IConsumableExecutor executor, int userNetworkId)
            => executor.RestoreOverTime(userNetworkId, _health, _mana, _duration);

        public override void DescribeEffect(List<string> lines)
        {
            string over = _duration > 0f ? $" over {_duration:0.#}s" : "";
            if (_health > 0f) lines.Add($"Restores {_health:0} health{over}");
            if (_mana > 0f) lines.Add($"Restores {_mana:0} mana{over}");
            lines.Add($"Takes {UseTime:0.#}s to drink");
        }
    }
}
