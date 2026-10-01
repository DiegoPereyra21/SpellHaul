namespace Game.Core.Abilities
{
    /// <summary>
    /// Slots fijos de habilidad del jugador. El índice viaja por red (RPCs, dash en el input
    /// replicado) y define el binding (CastSlot0..2 en PlayerControls): no reordenar.
    /// </summary>
    public static class AbilitySlots
    {
        /// <summary>Clic izquierdo: ataque básico, siempre disponible.</summary>
        public const int Primary = 0;
        /// <summary>Shift: dash.</summary>
        public const int Mobility = 1;
        /// <summary>Clic derecho: habilidad del guante equipado (vacío sin guante).</summary>
        public const int Glove = 2;

        public const int Count = 3;
    }
}
