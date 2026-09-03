namespace Game.Core.Items
{
    /// <summary>
    /// Categoría del item. No afecta la mecánica por sí sola; sirve para organizar,
    /// filtrar en la UI de inventario y para lógica de comercio / refugio a futuro.
    /// </summary>
    public enum ItemCategory
    {
        Material,     // apilables comunes (madera negra, polvo mágico)
        Resource,     // recursos raros / valiosos, no apilables
        Equipment,    // vestimenta (botas, sombrero, túnica, pantalón)
        Glove,        // guantes: Swift, Heavy, Focus
        Consumable,   // pociones, pergaminos de un uso (a futuro)
        Misc          // cualquier otra cosa
    }
}