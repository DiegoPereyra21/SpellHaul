using System;
using UnityEngine;

namespace Game.Presentation.Bootstrap
{
    /// <summary>
    /// Rol de red con el que arranca este proceso. Valores explícitos: el enum se serializa por
    /// número en NetworkBootstrap, no reordenar ni insertar en el medio.
    /// </summary>
    public enum NetworkRole
    {
        None = 0,
        Server = 1,
        Client = 2,
        Host = 3,
    }

    /// <summary>
    /// Lee los argumentos de línea de comandos con los que arrancó el proceso. Es la fuente de
    /// verdad de "¿soy un server dedicado?" — se parsea una sola vez y no depende del orden de
    /// ejecución de scripts, así cualquiera puede consultarla desde Awake/Start sin condiciones
    /// de carrera. En el editor no hay argumentos: el rol lo define NetworkBootstrap a mano.
    /// </summary>
    public static class LaunchArgs
    {
        private static bool _parsed;
        private static NetworkRole _role = NetworkRole.None;
        private static string _address = "127.0.0.1";
        private static ushort _port = 7770;
        private static string _playerId;

        /// <summary>Rol pedido por línea de comandos (None si no se especificó ninguno).</summary>
        public static NetworkRole Role { get { EnsureParsed(); return _role; } }

        /// <summary>Dirección a la que conectarse como cliente (-address).</summary>
        public static string Address { get { EnsureParsed(); return _address; } }

        /// <summary>Puerto de escucha (server) o de conexión (cliente) (-port).</summary>
        public static ushort Port { get { EnsureParsed(); return _port; } }

        /// <summary>True en cualquier proceso que sea un servidor dedicado: un build hecho con el
        /// target Dedicated Server siempre lo es, con o sin argumentos. Sirve para saltear todo lo
        /// que es exclusivo de cliente (login a PlayFab, UI, etc.).</summary>
        public static bool IsDedicatedServer
        {
            get
            {
#if UNITY_SERVER
                return true;
#else
                return Role == NetworkRole.Server;
#endif
            }
        }

        /// <summary>Identidad de PlayFab forzada por línea de comandos (-playerid). TEMPORAL: sirve
        /// para levantar varias instancias en la misma máquina con cuentas distintas, ya que
        /// deviceUniqueIdentifier es el mismo para todas. Muere cuando entre LoginWithSteam.</summary>
        public static string PlayerId { get { EnsureParsed(); return _playerId; } }

        private static void EnsureParsed()
        {
            if (_parsed) return;
            _parsed = true;

            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "-server":
                        _role = NetworkRole.Server;
                        break;
                    case "-client":
                        _role = NetworkRole.Client;
                        break;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    // Solo desarrollo: host (servidor + cliente en el mismo proceso) para iterar con
                    // Multiplayer Play Mode. En builds de release no existe el modo host.
                    case "-host":
                        _role = NetworkRole.Host;
                        break;
#endif
                    case "-address":
                        if (i + 1 < args.Length) _address = args[i + 1];
                        break;
                    case "-port":
                        if (i + 1 < args.Length && ushort.TryParse(args[i + 1], out ushort parsedPort))
                            _port = parsedPort;
                        break;
                    case "-playerid":
                        if (i + 1 < args.Length) _playerId = args[i + 1];
                        break;
                }
            }

            if (_role != NetworkRole.None)
                Debug.Log($"[LaunchArgs] Rol: {_role} | address: {_address} | port: {_port}");
        }
    }
}