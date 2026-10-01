namespace Game.Core.Items
{
    /// <summary>
    /// Escuela (elemento) de un guante: agrupa las habilidades por temática. Se muestra en el
    /// tooltip, el HUD y la UI de items. Serializado por número: renombrar en la misma posición
    /// es seguro; los valores nuevos van SIEMPRE al final.
    /// </summary>
    public enum GloveSchool
    {
        Fire,   // daño (orbe cargado). Antes "Destruction"
        Nature, // curación. Antes "Restoration"
        Light,  // control: cegar (orbe de destello). Antes "Illusion"
        Earth   // defensa: muros (muro de tierra)
    }
}
