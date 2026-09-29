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
    }

    /// <summary>Resultado de la run de un jugador que intentó volver y ya no tiene personaje jugable.</summary>
    public enum RunOutcome : byte
    {
        None = 0,
        DiedWhileAway = 1,
        Extracted = 2,
        LeftRun = 3,
    }

    /// <summary>Servidor → cliente: por qué no puede volver a la run (antes de desconectarlo).</summary>
    public struct RunOutcomeBroadcast : IBroadcast
    {
        public RunOutcome Outcome;
        /// <summary>Solo con Extracted: lo que el personaje sacó de la run, para guardarlo.</summary>
        public Game.Core.Items.InventorySnapshot ExtractedLoadout;
    }

    /// <summary>
    /// Authenticator de FishNet: cada conexión dice quién es (EntityId de PlayFab) antes de poder
    /// jugar. Con eso el servidor reconoce a un jugador que se desconectó y vuelve a entrar, y le
    /// devuelve su personaje (ver PlayerSpawnManager). Va en el mismo GameObject que el
    /// NetworkManager: ServerManager lo toma solo con GetComponent.
    ///
    /// Todavía no valida la sesión de PlayFab (eso requiere que el servidor sea dueño de la
    /// persistencia): solo rechaza claves vacías o demasiado largas. Si la misma clave ya tiene
    /// una conexión abierta (típico tras un crash: el servidor tarda en notar la caída), gana la
    /// nueva y la vieja se corta.
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
            NetworkManager.ClientManager.Broadcast(new PlayerIdentityBroadcast { PlayerKey = BuildLocalPlayerKey() });
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

            switch (msg.Outcome)
            {
                case RunOutcome.Extracted:
                    if (msg.ExtractedLoadout != null)
                        Game.Presentation.Run.PlayerLoadoutService.Save(msg.ExtractedLoadout);
                    break;
                case RunOutcome.DiedWhileAway:
                case RunOutcome.LeftRun:
                    Game.Presentation.Run.PlayerLoadoutService.Clear();
                    break;
            }
        }

        // ---------- Servidor ----------

        private void OnPlayerIdentityBroadcast(NetworkConnection conn, PlayerIdentityBroadcast msg, Channel channel)
        {
            if (conn.IsAuthenticated) return; // ya se identificó: ignorar repeticiones

            string key = msg.PlayerKey;
            bool valid = !string.IsNullOrEmpty(key) && key.Length <= MaxKeyLength;

            if (!valid)
            {
                Debug.LogWarning($"[Auth] Conexión {conn.ClientId} rechazada: clave de jugador vacía o demasiado larga.");
            }
            else if (!IsAllowedByMatch(key, out string allowedList))
            {
                valid = false;
                Debug.LogWarning($"[Auth] Conexión {conn.ClientId} rechazada: '{key}' no es jugador de este match. Permitidos: [{allowedList}]");
            }
            else
            {
                KickStaleConnection(key);
                _keysByClientId[conn.ClientId] = key;
            }

            OnAuthenticationResult?.Invoke(conn, valid);
        }

        /// <summary>En la nube solo entran (y vuelven) los jugadores que PlayFab asignó a este servidor.</summary>
        private static bool IsAllowedByMatch(string key, out string allowedList)
        {
            allowedList = string.Empty;
            IList<string> allowed = AllowedKeysProvider?.Invoke();
            if (allowed == null || allowed.Count == 0) return true;

            allowedList = string.Join(", ", allowed);
            foreach (string a in allowed)
                if (string.Equals(a, key, StringComparison.Ordinal)) return true;
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
                _keysByClientId.Remove(conn.ClientId);
        }

        /// <summary>Server-only. Avisa al cliente por qué no puede volver y lo desconecta.</summary>
        public static void RejectWithOutcome(NetworkManager networkManager, NetworkConnection conn, RunOutcome outcome,
            Game.Core.Items.InventorySnapshot extractedLoadout = null)
        {
            networkManager.ServerManager.Broadcast(conn, new RunOutcomeBroadcast { Outcome = outcome, ExtractedLoadout = extractedLoadout });
            conn.Disconnect(false); // no inmediato: deja salir el broadcast
        }
    }
}
