namespace Game.Presentation.Run
{
    /// <summary>
    /// Campo de práctica: partida local (host en este mismo proceso) para probar el equipo sin
    /// matchmaking. Mientras está activo NADA se persiste: morir no vacía el loadout, no se suelta
    /// loot, no se marca run en curso. Lo activa NetworkBootstrap.StartPractice y se apaga al
    /// volver al menú.
    /// </summary>
    public static class PracticeSession
    {
        public static bool Active { get; private set; }

        public static void Begin() => Active = true;
        public static void End() => Active = false;
    }
}
