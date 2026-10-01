using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace Game.Presentation.Combat
{
    /// <summary>
    /// Maná server-authoritative con regeneración. El valor se sincroniza a los clientes
    /// vía SyncVar; el gasto y la regeneración solo ocurren en el servidor.
    /// </summary>
    public class Mana : NetworkBehaviour
    {
        [SerializeField] private PlayerStats _stats;
        [SerializeField] private float _maxMana = 100f;
        [SerializeField] private float _regenPerSecond = 8f;

        // Cambia todos los frames (regeneración): cada 3 ticks alcanza (el cliente predice sus gastos).
        private readonly SyncVar<float> _current = new SyncVar<float>(new SyncTypeSettings(0.05f));

        public float Current => _current.Value;
        public float Max => _maxMana;

        public override void OnStartServer()
        {
            _current.Value = _maxMana;
        }

        private void Update()
        {
            if (!base.IsServerStarted) return;
            if (_current.Value >= _maxMana) return;

            float regen = _stats != null ? _stats.ManaRegen : _regenPerSecond;
            _current.Value = Mathf.Min(_maxMana, _current.Value + regen * Time.deltaTime);
        }

        /// <summary>Server-only. Devuelve true y descuenta si hay suficiente; false si no.</summary>
        public bool TrySpend(float amount)
        {
            if (!base.IsServerStarted) return false;
            if (_current.Value < amount) return false;

            _current.Value -= amount;
            return true;
        }

        /// <summary>Server-only. Restaura maná hasta el máximo.</summary>
        [Server]
        public void Restore(float amount)
        {
            if (!base.IsServerStarted) return;
            _current.Value = Mathf.Min(_maxMana, _current.Value + amount);
        }
    }
}