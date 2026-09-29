namespace Game.Core.Abilities
{
    /// <summary>
    /// Contrato mínimo para recibir daño/curación (valor negativo = cura). Lo implementa Health.
    /// </summary>
    public interface IDamageable
    {
        void ApplyDamage(float amount, int instigatorNetworkId);
    }
}
