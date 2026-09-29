using System;
using System.Collections.Generic;
using FishNet.Authenticating;
using FishNet.Broadcast;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Transporting;
using UnityEngine;

namespace Game.Presentation.Bootstrap
{
    /// <summary>Cliente → servidor: quién es este jugador (clave estable entre conexiones).</summary>
    public struct PlayerIdentityBroadcast : IBroadcast
    {
        public string PlayerKey;
        /// <summary>Session ticket de PlayFab: con el servidor dueño del loadout, prueba quién es.</summary>
        public string SessionTicket;
        /// <summary>Dirección y puerto a los que se conectó (los guarda el servidor para la reconexión).</summary>
        public string ServerAddress;
        public ushort ServerPort;
    }

    /// <summary>Servidor → cliente, antes de aceptar la conexión: quién guarda el loadout de esta run.</summary>
    public struct ServerPersistenceBroadcast : IBroadcast
    {
        public bool ServerOwnsProfile;
    }

    /// <summary>Resultado de la run de un jugador que intentó volver y ya no tiene personaje jugable.</summary>
    public enum RunOutcome : byte
    {
        None = 0,
        DiedWhileAway = 1,
        Extracted = 2,
        LeftRun = 3,
        /// <summary>El servidor no tiene a este jugador entre los del match (validación en la nube).</summary>
        NotInMatch = 4,
        /// <summary>El servidor no pudo verificar la sesión de PlayFab o leer el loadout.</summary>
        ProfileUnavailable = 5,
    }

    /// <summary>Servidor → cliente: por qué no puede volver a la run (antes de desconectarlo).</summary>
    public struct RunOutcomeBroadcast : IBroadcast
    {
        public RunOutcome Outcome;
        /// <summary>Solo con Extracted: lo que el personaje sacó de la run, para guardarlo.</summary>
        public Game.Core.Items.InventorySnapshot ExtractedLoadout;
        /// <summary>Detalle para el log del cliente (diagnóstico), nunca se muestra al jugador.</summary>
        public string Detail;
    }

    /// <summary>
    /// Authenticator de FishNet: cada conexión dice quién es (EntityId de PlayFab) antes de poder
    /// jugar. Con eso el servidor reconoce a un jugador que se desconectó y vuelve a entrar, y le
    /// devuelve su personaje (ver PlayerSpawnManager). Va en el mismo GameObject que el
    /// NetworkManager: ServerManager lo toma solo con GetComponent.
    ///
    /// Con el servidor dueño del loadout (ServerProfileStore activo) la identidad se verifica con
    /// el session ticket de PlayFab; sin eso (host / desarrollo) se confía en la clave declarada y
    /// solo se rechazan claves vacías o demasiado largas. Si la misma clave ya tiene una conexión
    /// abierta (típico tras un crash: el servidor tarda en notar la caída), gana la nueva y la
    /// vieja se corta.
    /// </summary>
    public class PlayerIdentityAuthenticator : Authenticator
    {
        private const int MaxKeyLength = 128;

        public override event Action<NetworkConnection, bool> OnAuthenticationResult;

        /// <summary>Client-only. Lo que respondió el servidor al rechazar la vuelta a la run (None si nada).</summary>
        public static RunOutcome LastRunOutcome { get; private set; }
        public static void ConsumeRunOutcome() => LastRunOutcome = RunOutcome.None;

        /// <summary>
        /// Server-only. Jugadores que tiene permitido entrar (en la nube: InitialPlayers del GSDK,
        /// lo inyecta NetworkBootstrap). Null o lista vacía = sin dato, se acepta cualquier clave.
        /// </summary>
        public static Func<IList<string>> AllowedKeysProvider;

        // Server-only: clave de cada conexión autenticada.
        private static readonly Dictionary<int, string> _keysByClientId = new();

        // Server-only, por clave de jugador (sobreviven a la desconexión: el personaje sigue en la run).
        private static readonly Dictionary<string, string> _playFabIdByKey = new();
        private static readonly Dictionary<string, (string address, ushort port)> _endpointByKey = new();

        // Server-only: conexiones con una verificación de sesión en curso.
        private static readonly HashSet<int> _verifying = new();

        /// <summary>Server-only. PlayFabId verificado del jugador (solo con ServerProfileStore activo).</summary>
        public static bool TryGetPlayFabId(string key, out string playFabId)
        {
            playFabId = null;
            return key != null && _playFabIdByKey.TryGetValue(key, out playFabId);
        }

        /// <summary>Server-only. Dirección con la que el jugador llegó a este servidor (para reconectar).</summary>
        public static bool TryGetEndpoint(string key, out string address, out ushort port)
        {
            address = null; port = 0;
            if (key == null || !_endpointByKey.TryGetValue(key, out var ep)) return false;
            address = ep.address; port = ep.port;
            return true;
        }

        /// <summary>Server-only. Clave del jugador detrás de esta conexión.</summary>
        public static bool TryGetPlayerKey(NetworkConnection conn, out string key)
        {
            key = null;
            return conn != null && _keysByClientId.TryGetValue(conn.ClientId, out key);
        }

        public override void InitializeOnce(NetworkManager networkManager)
        {
            base.InitializeOnce(networkManager);

            networkManager.ClientManager.OnClientConnectionState += OnClientConnectionState;
            networkManager.ClientManager.RegisterBroadcast<RunOutcomeBroadcast>(OnRunOutcomeBroadcast);
            networkManager.ClientManager.RegisterBroadcast<ServerPersistenceBroadcast>(OnServerPersistenceBroadcast);

            networkManager.ServerManager.RegisterBroadcast<PlayerIdentityBroadcast>(OnPlayerIdentityBroadcast, requireAuthentication: false);
            networkManager.ServerManager.OnRemoteConnectionState += OnRemoteConnectionState;
        }

        private void OnDestroy()
        {
            if (NetworkManager == null) return;
            NetworkManager.ClientManager.OnClientConnectionState -= OnClientConnectionState;
            NetworkManager.ServerManager.OnRemoteConnectionState -= OnRemoteConnectionState;
        }

        // ---------- Cliente ----------

        private void OnClientConnectionState(ClientConnectionStateArgs args)
        {
            if (args.ConnectionState != LocalConnectionState.Started) return;

            LastRunOutcome = RunOutcome.None;
            Game.Presentation.Run.PlayerLoadoutService.ServerOwnsRun = false;
            NetworkManager.ClientManager.Broadcast(new PlayerIdentityBroadcast
            {
                PlayerKey = BuildLocalPlayerKey(),
                SessionTicket = PlayFabSession.SessionTicket,
                ServerAddress = RunServerEndpoint.Address,
                ServerPort = RunServerEndpoint.Port,
            });
        }

        private static void OnServerPersistenceBroadcast(ServerPersistenceBroadcast msg, Channel channel)
        {
            Game.Presentation.Run.PlayerLoadoutService.ServerOwnsRun = msg.ServerOwnsProfile;
            if (msg.ServerOwnsProfile)
                Debug.Log("[Auth] El servidor guarda el loadout de esta run.");
        }

        /// <summary>
        /// Clave del jugador local: su EntityId de PlayFab (el menú solo deja jugar con la sesión
        /// lista, y es lo que el servidor en la nube compara contra los jugadores del match).
        /// Sin sesión todavía (conexión directa de desarrollo con -client, que conecta antes de que
        /// termine el login) usa una clave de desarrollo: -playerid si se pasó, si no el dispositivo.
        /// Cada camino es consistente consigo mismo, así la reconexión reconoce al mismo jugador.
        /// </summary>
        private static string BuildLocalPlayerKey()
        {
            if (!string.IsNullOrEmpty(PlayFabSession.EntityId))
                return PlayFabSession.EntityId;
            if (!string.IsNullOrEmpty(LaunchArgs.PlayerId))
                return "dev:" + LaunchArgs.PlayerId;
            return "dev:" + SystemInfo.deviceUniqueIdentifier;
        }

        /// <summary>
        /// Client-only. El servidor no nos deja volver a la run. Con la persistencia en el cliente,
        /// acá se aplica el resultado que no pudo llegar mientras estábamos desconectados:
        /// extrajo → se guarda lo extraído; murió o ya no está → se pierde el equipo (como en Tarkov).
        /// </summary>
        private static void OnRunOutcomeBroadcast(RunOutcomeBroadcast msg, Channel channel)
        {
            LastRunOutcome = msg.Outcome;
            if (!string.IsNullOrEmpty(msg.Detail))
                Debug.LogWarning($"[Auth] Servidor: {msg.Outcome} | {msg.Detail}");

            switch (msg.Outcome)
            {
                case RunOutcome.Extracted:
                    if (msg.ExtractedLoadout != null)
                        Game.Presentation.Run.PlayerLoadoutService.ApplyRunResult(msg.ExtractedLoadout);
                    break;
                case RunOutcome.DiedWhileAway:
                    Game.Presentation.Run.PlayerLoadoutService.ApplyRunLost();
                    break;
                case RunOutcome.LeftRun:
                    // Con el servidor dueño, él sabe cómo quedó: releer. Si no, se pierde como antes.
                    if (Game.Presentation.Run.PlayerLoadoutService.ServerOwnsRun)
                        Game.Presentation.Run.PlayerLoadoutService.Invalidate();
                    else
                        Game.Presentation.Run.PlayerLoadoutService.ApplyRunLost();
                    break;
            }
        }

        // ---------- Servidor ----------

        private void OnPlayerIdentityBroadcast(NetworkConnection conn, PlayerIdentityBroadcast msg, Channel channel)
        {
            if (conn.IsAuthenticated) return; // ya se identificó: ignorar repeticiones
            if (_verifying.Contains(conn.ClientId)) return;

            string key = msg.PlayerKey;
            bool valid = !string.IsNullOrEmpty(key) && key.Length <= MaxKeyLength;

            if (!valid)
            {
                Debug.LogWarning($"[Auth] Conexión {conn.ClientId} rechazada: clave de jugador vacía o demasiado larga.");
                OnAuthenticationResult?.Invoke(conn, false);
                return;
            }

            // Con el servidor dueño del loadout, la clave no se cree: se verifica el session
            // ticket con PlayFab y la identidad sale de ahí (nadie puede hacerse pasar por otro).
            if (Game.Presentation.Run.ServerProfileStore.IsActive)
            {
                _ = VerifyAndContinueAsync(conn, msg);
                return;
            }

            ContinueAuthentication(conn, key, null, msg);
        }

        private async System.Threading.Tasks.Task VerifyAndContinueAsync(NetworkConnection conn, PlayerIdentityBroadcast msg)
        {
            _verifying.Add(conn.ClientId);
            Game.Presentation.Run.ServerProfileStore.VerifiedPlayer verified;
            try
            {
                verified = await Game.Presentation.Run.ServerProfileStore.VerifySessionTicketAsync(msg.SessionTicket);
            }
            finally
            {
                _verifying.Remove(conn.ClientId);
            }

            if (!conn.IsActive) return; // se fue mientras se verificaba

            if (!verified.Ok)
            {
                string detail = $"No se pudo verificar la sesión de PlayFab: {verified.Error}";
                Debug.LogWarning($"[Auth] Conexión {conn.ClientId} rechazada: {detail}");
                RejectWithOutcome(NetworkManager, conn, RunOutcome.ProfileUnavailable, null, detail, requireAuthenticated: false);
                return;
            }

            if (!string.Equals(verified.EntityId, msg.PlayerKey, StringComparison.Ordinal))
                Debug.LogWarning($"[Auth] Conexión {conn.ClientId}: la clave declarada '{msg.PlayerKey}' no coincide con la sesión ({verified.EntityId}); se usa la de la sesión.");

            ContinueAuthentication(conn, verified.EntityId, verified.PlayFabId, msg);
        }

        private void ContinueAuthentication(NetworkConnection conn, string key, string playFabId, PlayerIdentityBroadcast msg)
        {
            if (!IsAllowedByMatch(key, out string allowedList))
            {
                string detail = $"'{key}' no es jugador de este match. Permitidos: [{allowedList}]";
                Debug.LogWarning($"[Auth] Conexión {conn.ClientId} rechazada: {detail}");
                // Sin OnAuthenticationResult(false): FishNet cortaría en el acto y el aviso no llegaría.
                RejectWithOutcome(NetworkManager, conn, RunOutcome.NotInMatch, null, detail, requireAuthenticated: false);
                return;
            }

            KickStaleConnection(key);
            _keysByClientId[conn.ClientId] = key;
            if (playFabId != null) _playFabIdByKey[key] = playFabId;
            if (!string.IsNullOrEmpty(msg.ServerAddress) && msg.ServerPort != 0)
                _endpointByKey[key] = (msg.ServerAddress, msg.ServerPort);

            // Antes del resultado (mismo canal confiable): el cliente ya sabe quién guarda al entrar.
            NetworkManager.ServerManager.Broadcast(conn,
                new ServerPersistenceBroadcast { ServerOwnsProfile = playFabId != null }, requireAuthenticated: false);
            OnAuthenticationResult?.Invoke(conn, true);
        }

        /// <summary>En la nube solo entran (y vuelven) los jugadores que PlayFab asignó a este servidor.</summary>
        private static bool IsAllowedByMatch(string key, out string allowedList)
        {
            allowedList = string.Empty;
            IList<string> allowed;
            try { allowed = AllowedKeysProvider?.Invoke(); }
            catch (Exception e)
            {
                Debug.LogWarning($"[Auth] No se pudo leer la lista de jugadores del match, se acepta: {e.Message}");
                return true;
            }
            if (allowed == null || allowed.Count == 0) return true;

            allowedList = string.Join(", ", allowed);
            foreach (string a in allowed)
            {
                if (string.IsNullOrEmpty(a)) continue;
                // Tolerante al formato: igual, o con prefijo de tipo de entidad ("tipo!id", "tipo/id").
                if (string.Equals(a, key, StringComparison.OrdinalIgnoreCase)) return true;
                if (a.Length > key.Length && a.EndsWith(key, StringComparison.OrdinalIgnoreCase))
                {
                    char sep = a[a.Length - key.Length - 1];
                    if (sep == '!' || sep == '/' || sep == ':') return true;
                }
            }
            return false;
        }

        /// <summary>Corta una conexión anterior con la misma clave (sesión colgada tras un crash).</summary>
        private void KickStaleConnection(string key)
        {
            int staleId = -1;
            foreach (var kvp in _keysByClientId)
                if (kvp.Value == key) { staleId = kvp.Key; break; }
            if (staleId < 0) return;

            _keysByClientId.Remove(staleId);
            if (NetworkManager.ServerManager.Clients.TryGetValue(staleId, out NetworkConnection stale))
            {
                Debug.Log($"[Auth] La clave {key} volvió a conectarse: se corta la conexión vieja {staleId}.");
                stale.Disconnect(true);
            }
        }

        private void OnRemoteConnectionState(NetworkConnection conn, RemoteConnectionStateArgs args)
        {
            if (args.ConnectionState == RemoteConnectionState.Stopped)
            {
                _keysByClientId.Remove(conn.ClientId);
                _verifying.Remove(conn.ClientId);
            }
        }

        /// <summary>Server-only. Avisa al cliente por qué no puede volver y lo desconecta.</summary>
        public static void RejectWithOutcome(NetworkManager networkManager, NetworkConnection conn, RunOutcome outcome,
            Game.Core.Items.InventorySnapshot extractedLoadout = null, string detail = null, bool requireAuthenticated = true)
        {
            networkManager.ServerManager.Broadcast(conn,
                new RunOutcomeBroadcast { Outcome = outcome, ExtractedLoadout = extractedLoadout, Detail = detail },
                requireAuthenticated);
            conn.Disconnect(false); // no inmediato: deja salir el broadcast
        }
    }
}
