using FishNet.Object;
using UnityEngine;
using Game.Presentation.Player;

namespace Game.Presentation.Combat
{
    /// <summary>
    /// Maneja la muerte del jugador en un extraction: sin respawn. Al morir, el jugador
    /// queda eliminado de la run. Por ahora: desactiva control y colisión, y avisa al dueño.
    /// (Feedback visual / cámara spectator se agregan después.)
    /// </summary>
    [RequireComponent(typeof(Health))]
    public class PlayerDeathHandler : NetworkBehaviour
    {
        [SerializeField] private Game.Presentation.Player.PlayerAvatarState _avatar;

        private Health _health;

        private void Awake()
        {
            _health = GetComponent<Health>();
        }

        public override void OnStartServer()
        {
            _health.OnDied += HandleDiedServer;
        }

        public override void OnStopServer()
        {
            if (_health != null) _health.OnDied -= HandleDiedServer;
        }

        private void HandleDiedServer(int instigatorNetworkId)
        {
            // Práctica: no se pierde nada; el personaje reaparece con el mismo equipo.
            if (Game.Presentation.Run.PracticeSession.Active)
            {
                DieObserversRpc();
                Game.Presentation.Bootstrap.PlayerSpawnManager.Instance?.ServerRespawnAfter(base.NetworkObject, PracticeRespawnSeconds);
                return;
            }

            if (Game.Presentation.Run.RunManager.Instance != null)
                Game.Presentation.Run.RunManager.Instance.SetDead(base.ObjectId);

            // Resumen para la pantalla de resultados del dueño, ANTES de soltar el loot (DropAll
            // corre en DieObserversRpc y vacía el inventario). Mismo canal confiable: llega primero.
            if (base.Owner.IsActive && TryGetComponent(out RunInventory inventory))
                DeathSummaryTargetRpc(base.Owner, DescribeDeathCause(instigatorNetworkId), inventory.TakeSnapshot());

            _leavingRun = false;
            DieObserversRpc();
        }

        private const float PracticeRespawnSeconds = 3f;

        // ---------- Salir de la run (menú de pausa) ----------

        private bool _leavingRun;

        /// <summary>
        /// El jugador abandona la run desde el menú de pausa: es una muerte (suelta todo lo que lleva
        /// en un contenedor y pierde el equipo), con su propio texto en la pantalla de resultados.
        /// </summary>
        [ServerRpc]
        public void LeaveRunServerRpc()
        {
            if (_health == null || _health.IsDead) return;
            if (_avatar != null && _avatar.IsControlDisabled) return; // ya extrajo
            _leavingRun = true;
            _health.ApplyDamage(1_000_000f, base.ObjectId);
            _leavingRun = false; // si algo impidió la muerte, que no quede marcado
        }

        [Server]
        private string DescribeDeathCause(int instigatorNetworkId)
        {
            if (instigatorNetworkId == base.ObjectId)
            {
                if (_leavingRun) return "You left the run.";
                var run = Game.Presentation.Run.RunManager.Instance;
                if (run != null && run.Phase == Game.Core.Run.RunPhase.DangerPhase) return "The danger phase caught you.";
                return "You fell to your death.";
            }

            if (!FishNet.InstanceFinder.ServerManager.Objects.Spawned.TryGetValue(instigatorNetworkId, out NetworkObject killer))
                return "You were killed.";

            if (killer.GetComponent<PlayerAvatarState>() != null) return "Killed by another mage.";
            if (killer.GetComponent<PlantGuardianAI>() != null) return "Killed by a Plant Guardian.";
            if (killer.GetComponent<RangedEnemyAI>() != null) return "Killed by a ranged enemy.";
            if (killer.GetComponent<EnemyAI>() != null) return "Killed by a melee enemy.";
            return "You were killed.";
        }

        [TargetRpc]
        private void DeathSummaryTargetRpc(FishNet.Connection.NetworkConnection conn, string cause, Game.Core.Items.InventorySnapshot carried)
        {
            Game.Presentation.UI.RunSummary.SetDeath(cause, carried);
        }

        [ObserversRpc(RunLocally = true)]
        private void DieObserversRpc()
        {
            if (_avatar != null)
                _avatar.DisableControl();

            // Soltar el loot de la run: solo el servidor (DropAll es [Server]; en los clientes
            // solo generaba un warning por muerte).
            if (base.IsServerInitialized && !Game.Presentation.Run.PracticeSession.Active &&
                TryGetComponent(out Game.Core.Run.IRunInventory inventory))
                inventory.DropAll();


            if (base.IsOwner && !Game.Presentation.Run.PracticeSession.Active)
            {
                var result = FindFirstObjectByType<Game.Presentation.UI.ResultScreenController>();
                if (result != null) result.Show(false); // murió
            }
        }
    }
}