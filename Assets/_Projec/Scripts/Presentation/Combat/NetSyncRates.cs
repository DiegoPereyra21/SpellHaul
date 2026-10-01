namespace Game.Presentation.Combat
{
    /// <summary>
    /// Ritmos de envío de SyncTypes. Por defecto FishNet junta los cambios y los manda cada 0,1 s
    /// (ServerManager.SyncTypeRate): vida, inventario o loot se veían con hasta 100 ms extra.
    /// Ojo: SyncTypeSettings(0f) es igual a "sin configurar" y vuelve al ritmo por defecto, por eso
    /// "cada tick" es un valor mínimo (se redondea hacia arriba a 1 tick).
    /// </summary>
    public static class NetSyncRates
    {
        public const float EveryTick = 0.001f;
    }
}
