namespace Game.Core.Run
{
    /// <summary>
    /// Run en curso con el loadout del jugador en juego. Se guarda junto con el loadout (misma
    /// escritura): mientras esté activa, el equipo está "adentro" y solo vuelve reconectando y
    /// extrayendo; si la run termina sin el jugador (murió, abandonó o nunca volvió) se pierde.
    /// </summary>
    [System.Serializable]
    public struct ActiveRunInfo
    {
        public bool Active;
        public string Address;          // servidor de la run, para reconectar
        public int Port;
        public long StartedUnixSeconds; // para dar por terminada una run muy vieja
        /// <summary>Identifica la run (la genera el servidor al marcarla). El resultado solo se
        /// guarda si la marca guardada sigue siendo la de esa run: si el jugador la abandonó o ya
        /// está en otra, el servidor viejo no pisa lo nuevo.</summary>
        public string RunId;
    }
}
