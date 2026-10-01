using System.Collections.Generic;
using UnityEngine;

namespace Game.Core.Items
{
    /// <summary>
    /// Item que se usa desde un slot de usables (teclas 1-2-3) y se gasta: pociones y, a futuro,
    /// objetos lanzables. Se "canaliza" UseTime segundos (se cancela si el jugador ataca, dashea o
    /// usa el guante) y recién ahí se gasta y aplica su efecto. Cada consumible concreto hereda y
    /// define Apply; el efecto real lo produce el servidor a través de IConsumableExecutor.
    /// </summary>
    public abstract class ConsumableItemSO : ItemSO
    {
        public const int UsableSlotCount = 3;

        [Header("Uso")]
        [Tooltip("Segundos que tarda en usarse (canalización). Se cancela al atacar, dashear o usar el guante.")]
        [SerializeField, Min(0f)] private float _useTime = 1f;
        [Tooltip("Sonido al terminar de usarlo (opcional, 3D).")]
        [SerializeField] private AudioClip _useClip;

        public float UseTime => _useTime;
        public AudioClip UseClip => _useClip;

        /// <summary>Server-only. Aplica el efecto sobre quien lo usó (la unidad ya se gastó).</summary>
        public abstract void Apply(IConsumableExecutor executor, int userNetworkId);

        /// <summary>Líneas de efecto para el tooltip (en inglés, las ve el jugador).</summary>
        public virtual void DescribeEffect(List<string> lines) { }
    }

    /// <summary>
    /// Puerto con el que los consumibles producen efectos sin depender de Presentation (misma idea
    /// que AbilityExecutor). Lo implementa UsableController.
    /// </summary>
    public interface IConsumableExecutor
    {
        /// <summary>Restaura vida y/o maná repartidos en 'duration' segundos (0 = instantáneo).</summary>
        void RestoreOverTime(int targetNetworkId, float health, float mana, float duration);
    }
}
