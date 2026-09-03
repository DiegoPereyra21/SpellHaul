namespace Game.Core.Items
{
    /// <summary>Variantes de guante, cada una con su perfil de combate.</summary>
    public enum GloveType
    {
        Swift,  // casteo rápido, menos daño
        Heavy,  // más daño, más lento
        Focus   // regeneración de maná, sin tocar daño ni velocidad
    }
}