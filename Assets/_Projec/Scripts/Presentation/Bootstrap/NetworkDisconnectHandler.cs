using System;
using FishNet;
using FishNet.Transporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Presentation.Bootstrap
{
    /// <summary>
    /// Devuelve al jugador al menú cuando pierde la conexión con el servidor de la run, en vez de
    /// dejarlo en una escena vacía sin cámara ni forma de salir. Vive en el NetworkManager
    /// (DontDestroyOnLoad) para seguir escuchando a través del cambio de escena.
    ///
    /// No actúa cuando la desconexión fue decisión del jugador (ver NotifyIntentionalDisconnect):
    /// en ese caso quien lo llamó ya se encarga de la navegación.
    /// </summary>
    public class NetworkDisconnectHandler : MonoBehaviour
    {
        [SerializeField] private string _menuSceneName = "MainMenu";

        /// <summary>Motivo de la última desconexión inesperada, para mostrárselo al jugador al
        /// volver al menú. Null si no hubo ninguna o si ya se consumió.</summary>
        public static string LastDisconnectMessage { get; private set; }

        /// <summary>Se dispara al detectar una desconexión inesperada, para quien ya esté en el
        /// menú (ej. falló la conexión inicial y nunca se cambió de escena).</summary>
        public static event Action<string> OnUnexpectedDisconnect;

        private static bool _intentionalDisconnect;
        private static bool _localRunFinished;
        private bool _wasConnected;

        /// <summary>Avisar ANTES de cortar la conexión a propósito (volver al menú desde la
        /// pantalla de resultados, salir del juego, etc.).</summary>
        public static void NotifyIntentionalDisconnect() => _intentionalDisconnect = true;

        /// <summary>Avisar cuando el jugador local terminó su participación (murió o extrajo).
        /// Si después el servidor cierra la conexión (fin de la run: el proceso se apaga solo),
        /// no es una caída: se vuelve al menú con un aviso normal en vez de "Connection lost".</summary>
        public static void NotifyLocalRunFinished() => _localRunFinished = true;

        /// <summary>Marca el mensaje como ya mostrado.</summary>
        public static void ConsumeDisconnectMessage() => LastDisconnectMessage = null;

        private void OnEnable()
        {
            if (InstanceFinder.ClientManager != null)
                InstanceFinder.ClientManager.OnClientConnectionState += HandleConnectionState;
        }

        private void OnDisable()
        {
            if (InstanceFinder.ClientManager != null)
                InstanceFinder.ClientManager.OnClientConnectionState -= HandleConnectionState;
        }

        private void HandleConnectionState(ClientConnectionStateArgs args)
        {
            if (args.ConnectionState == LocalConnectionState.Started)
            {
                _wasConnected = true;
                _localRunFinished = false;
                return;
            }

            if (args.ConnectionState != LocalConnectionState.Stopped) return;

            bool hadConnected = _wasConnected;
            _wasConnected = false;

            if (_intentionalDisconnect)
            {
                _intentionalDisconnect = false;
                return;
            }

            bool runFinished = _localRunFinished;
            _localRunFinished = false;

            // El servidor no nos dejó volver a la run (reconexión) y dijo por qué.
            RunOutcome outcome = PlayerIdentityAuthenticator.LastRunOutcome;
            PlayerIdentityAuthenticator.ConsumeRunOutcome();

            if (outcome != RunOutcome.None)
            {
                LastDisconnectMessage = outcome switch
                {
                    RunOutcome.DiedWhileAway => "Your character died while you were away.",
                    RunOutcome.Extracted => "You already extracted from this run.",
                    RunOutcome.NotInMatch => "This server doesn't have you in its match. Try finding a new run.",
                    _ => "You can no longer rejoin this run.",
                };
                Debug.Log($"[Disconnect] {LastDisconnectMessage}");
            }
            else if (runFinished)
            {
                LastDisconnectMessage = "The run has ended.";
                Debug.Log($"[Disconnect] {LastDisconnectMessage}");
            }
            else
            {
                LastDisconnectMessage = hadConnected
                    ? "Connection to the run was lost."
                    : "Could not reach the run server.";
                Debug.LogWarning($"[Disconnect] {LastDisconnectMessage}");
            }

            // Si seguimos en el menú (falló la conexión inicial), no hay que navegar: basta con
            // avisarle a la pantalla que ya está en pantalla.
            if (SceneManager.GetActiveScene().name == _menuSceneName)
            {
                OnUnexpectedDisconnect?.Invoke(LastDisconnectMessage);
                return;
            }

            SceneManager.LoadScene(_menuSceneName);
        }
    }
}