namespace Game.Core.Items
{
    /// <summary>
    /// Escuela de magia de un guante: agrupa los guantes por rol (ofensivo, curación, etc.).
    /// Se muestra en el tooltip y en el HUD. Serializado por número: los valores nuevos van
    /// SIEMPRE al final.
    /// </summary>
    public enum GloveSchool
    {
        Destruction, // daño (orbe cargado)
        Restoration, // curación
        Illusion     // control: cegar (orbe de destello)
    }
}
