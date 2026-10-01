using System.Collections.Generic;
using FishNet;
using FishNet.Object;
using Game.Core.Items;
using Game.Presentation.Abilities;
using Game.Presentation.Player;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Presentation.Combat
{
    /// <summary>
    /// Usables del jugador (teclas 1-2-3): consumibles en los slots de usables de RunInventory.
    ///
    /// Server-authoritative: el dueño pide usar un slot, el servidor valida (vivo, slot con un
    /// consumible, sin otro uso en curso) y arranca la canalización (ConsumableItemSO.UseTime). Si
    /// el jugador castea algo (ataque, guante, dash) o el slot cambia, se cancela sin gastar nada.
    /// Al terminar se gasta una unidad y se aplica el efecto (IConsumableExecutor). Los efectos en
    /// el tiempo (pociones) los corre este mismo componente en el servidor.
    ///
    /// Va en el prefab Player (NetworkBehaviour). Las teclas se leen directo del teclado (sin
    /// acción de input) hasta que exista el rebinding.
    /// </summary>
    public class UsableController : NetworkBehaviour, IConsumableExecutor
    {
        private RunInventory _inventory;
        private AbilityController _abilities;
        private PlayerAvatarState _avatar;
        private Health _health;
        private Mana _mana;

        // ---------- Estado de canalización ----------
        // Servidor: el uso real. Dueño: la copia para la barra del HUD (la confirma o corta el servidor).
        private int _serverSlot = -1;
        private string _serverItemId;
        private float _serverEndTime;

        private int _localSlot = -1;
        private float _localStartTime;
        private float _localDuration;

        /// <summary>Owner. Slot que se está usando (-1 = ninguno).</summary>
        public int ChannelSlot => _localSlot;

        /// <summary>Owner. Progreso 0..1 del uso en curso (0 si no hay ninguno).</summary>
        public float ChannelProgress => _localSlot < 0 || _localDuration <= 0f ? 0f
            : Mathf.Clamp01((Time.time - _localStartTime) / _localDuration);

        private static readonly Key[] SlotKeys = { Key.Digit1, Key.Digit2, Key.Digit3 };

        // ---------- Efectos en el tiempo (servidor) ----------
        private struct Restore
        {
            public float HealthPerSecond;
            public float ManaPerSecond;
            public float EndTime;
        }
        private readonly List<Restore> _restores = new();
        private const float RestoreTickInterval = 0.25f;
        private float _restoreTickTimer;

        private void Awake()
        {
            _inventory = GetComponent<RunInventory>();
            _abilities = GetComponent<AbilityController>();
            _avatar = GetComponent<PlayerAvatarState>();
            _health = GetComponent<Health>();
            _mana = GetComponent<Mana>();
        }

        public override void OnStartNetwork()
        {
            base.OnStartNetwork();
            if (_abilities != null) _abilities.OnAbilityUsed += HandleAbilityUsed;
            _serverSlot = -1;
            _localSlot = -1;
            _restores.Clear();
        }

        public override void OnStopNetwork()
        {
            base.OnStopNetwork();
            if (_abilities != null) _abilities.OnAbilityUsed -= HandleAbilityUsed;
        }

        private bool ControlDisabled => _avatar != null && _avatar.IsControlDisabled;

        // ---------- Input (dueño) ----------

        private void Update()
        {
            if (base.IsServerStarted) ServerUpdate();
            if (!base.IsOwner) return;

            if (ControlDisabled) { _localSlot = -1; return; }
            if (_abilities != null && _abilities.IsInputBlocked) return;

            var keyboard = Keyboard.current;
            if (keyboard == null) return;
            for (int i = 0; i < SlotKeys.Length; i++)
                if (keyboard[SlotKeys[i]].wasPressedThisFrame) TryUse(i);
        }

        private void TryUse(int slot)
        {
            if (_inventory == null || slot >= _inventory.Usables.Count) return;
            if (_localSlot >= 0) return; // ya está usando algo

            ItemStack stack = _inventory.Usables[slot];
            if (stack.IsEmpty || _inventory.Database.GetById(stack.ItemId) is not ConsumableItemSO def) return;

            _localSlot = slot;
            _localStartTime = Time.time;
            _localDuration = def.UseTime;
            UseServerRpc(slot);
        }

        private void HandleAbilityUsed()
        {
            if (base.IsOwner) _localSlot = -1;          // la barra se corta al instante
            if (base.IsServerStarted) ServerCancel();   // el uso real
        }

        // ---------- Servidor ----------

        [ServerRpc]
        private void UseServerRpc(int slot)
        {
            if (ControlDisabled || _inventory == null) { EndedTargetRpc(base.Owner, slot); return; }
            if (_serverSlot >= 0 || slot < 0 || slot >= _inventory.Usables.Count) { EndedTargetRpc(base.Owner, slot); return; }

            ItemStack stack = _inventory.Usables[slot];
            if (stack.IsEmpty || _inventory.Database.GetById(stack.ItemId) is not ConsumableItemSO def)
            {
                EndedTargetRpc(base.Owner, slot);
                return;
            }

            _serverSlot = slot;
            _serverItemId = stack.ItemId;
            _serverEndTime = Time.time + def.UseTime;
        }

        [Server]
        private void ServerCancel()
        {
            if (_serverSlot < 0) return;
            int slot = _serverSlot;
            _serverSlot = -1;
            if (base.Owner.IsActive) EndedTargetRpc(base.Owner, slot);
        }

        private void ServerUpdate()
        {
            if (_serverSlot >= 0)
            {
                ItemStack current = _serverSlot < _inventory.Usables.Count ? _inventory.Usables[_serverSlot] : ItemStack.Empty;
                if (ControlDisabled || current.IsEmpty || current.ItemId != _serverItemId)
                {
                    ServerCancel(); // murió, extrajo, o movió/soltó el consumible mientras lo usaba
                }
                else if (Time.time >= _serverEndTime)
                {
                    int slot = _serverSlot;
                    _serverSlot = -1;
                    if (_inventory.Database.GetById(_serverItemId) is ConsumableItemSO def &&
                        _inventory.TryConsumeUsable(slot, _serverItemId))
                    {
                        def.Apply(this, base.ObjectId);
                        if (def.UseClip != null) UsedObserversRpc(def.ItemId);
                    }
                    if (base.Owner.IsActive) EndedTargetRpc(base.Owner, slot);
                }
            }

            TickRestores();
        }

        [TargetRpc]
        private void EndedTargetRpc(FishNet.Connection.NetworkConnection conn, int slot)
        {
            if (_localSlot == slot) _localSlot = -1;
        }

        [ObserversRpc]
        private void UsedObserversRpc(string itemId)
        {
            // El clip se resuelve en cada cliente por el id del item (un AudioClip no viaja por RPC).
            if (_inventory == null) return;
            if (_inventory.Database.GetById(itemId) is ConsumableItemSO def && def.UseClip != null)
                VFXManager.PlaySfx(def.UseClip, transform.position + Vector3.up);
        }

        // ---------- IConsumableExecutor (servidor) ----------

        public void RestoreOverTime(int targetNetworkId, float health, float mana, float duration)
        {
            if (!base.IsServerStarted) return;
            if (duration <= 0f)
            {
                if (health > 0f && _health != null) _health.ApplyDamage(-health, base.ObjectId);
                if (mana > 0f && _mana != null) _mana.Restore(mana);
                return;
            }
            _restores.Add(new Restore
            {
                HealthPerSecond = health / duration,
                ManaPerSecond = mana / duration,
                EndTime = Time.time + duration,
            });
        }

        private void TickRestores()
        {
            if (_restores.Count == 0) return;
            if (ControlDisabled) { _restores.Clear(); return; }

            _restoreTickTimer += Time.deltaTime;
            if (_restoreTickTimer < RestoreTickInterval) return;
            float dt = _restoreTickTimer;
            _restoreTickTimer = 0f;

            float now = Time.time;
            float health = 0f, mana = 0f;
            for (int i = _restores.Count - 1; i >= 0; i--)
            {
                Restore r = _restores[i];
                // El último tramo no se pasa del final: la poción da exactamente lo que dice.
                float applied = Mathf.Min(dt, Mathf.Max(0f, r.EndTime - (now - dt)));
                health += r.HealthPerSecond * applied;
                mana += r.ManaPerSecond * applied;
                if (now >= r.EndTime) _restores.RemoveAt(i);
            }

            if (health > 0f && _health != null) _health.ApplyDamage(-health, base.ObjectId);
            if (mana > 0f && _mana != null) _mana.Restore(mana);
        }
    }
}
