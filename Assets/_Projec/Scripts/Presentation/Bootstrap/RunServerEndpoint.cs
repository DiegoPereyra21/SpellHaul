namespace Game.Presentation.Bootstrap
{
    /// <summary>
    /// Client-only. Dirección y puerto del servidor al que se pidió conectar. Hace falta guardarlo
    /// aparte: con el cliente conectado, Tugboat.GetPort() devuelve el puerto LOCAL del socket
    /// (efímero), no el del servidor, y reconectar a ese puerto nunca llega.
    /// </summary>
    public static class RunServerEndpoint
    {
        public static string Address { get; private set; }
        public static ushort Port { get; private set; }

        public static bool IsSet => !string.IsNullOrEmpty(Address) && Port != 0;

        public static void Set(string address, ushort port)
        {
            Address = address;
            Port = port;
        }

        public static void Clear()
        {
            Address = null;
            Port = 0;
        }
    }
}
