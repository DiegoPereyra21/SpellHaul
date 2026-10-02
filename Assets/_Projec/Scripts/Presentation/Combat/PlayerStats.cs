using System.Collections.Generic;
using FishNet.Object;
using Game.Core.Items;
using UnityEngine;

namespace Game.Presentation.Combat
{
    /// <summary>
    /// Agrega los modificadores del equipamiento actual y expone los stats finales.
    /// Los sistemas del jugador (Mana, Movement, Health, habilidades) LEEN de acá; nadie
    /// les escribe directo desde el inventario. Recalcula cuando cambia el equipamiento.
    /// Corre en servidor y cliente (ambos tienen el equipo sincronizado vía SyncList).
    /// </summary>
    [RequireComponent(typeof(RunInventory))]
    public class PlayerStats : NetworkBehaviour
    {
        [SerializeField] private ItemDatabase _database;

        [Header("Valores base (sin equipamiento)")]
        [SerializeField] private float _baseManaRegen = 8f;
        [SerializeField] private float _baseJumpForce = 6f;
        [SerializeField] private float _baseMoveSpeed = 6f;
        [SerializeField] private float _baseDamageMultiplier = 1f;
        [SerializeField] private float _baseCastSpeedMultiplier = 1f;

        [Header("Protección")]
        [SerializeField, Range(0f, 0.9f)] private float _protectionCap = 0.6f; // techo de reducción

        [Header("Pisos de seguridad (evitan que equipo negativo rompa un stat)")]
        [SerializeField] private float _minDamageMultiplier = 0.1f;
        [SerializeField] private float _minCastSpeedMultiplier = 0.1f;
        [SerializeField] private float _minMoveSpeed = 0.5f;

        private RunInventory _inventory;

        // Stats finales calculados.
        public float ManaRegen { get; private set; }
        public float JumpForce { get; private set; }
        public float MoveSpeed { get; private set; }
        public float DamageMultiplier { get; private set; }
        public float CastSpeedMultiplier { get; private set; }
        public float ProtectionPercent { get; private set; } // 0..cap
        /// <summary>Vida máxima extra del equipo (plana; la usa Health).</summary>
        public float MaxHealthBonus { get; private set; }

        public event System.Action OnStatsChanged;

        private void Awake()
        {
            _inventory = GetComponent<RunInventory>();
            RecalculateToBase();
        }

        public override void OnStartNetwork()
        {
            _inventory.OnInventoryChanged += Recalculate;
            Recalculate();
        }

        public override void OnStopNetwork()
        {
            if (_inventory != null) _inventory.OnInventoryChanged -= Recalculate;
        }

        private void RecalculateToBase()
        {
            ManaRegen = _baseManaRegen;
            JumpForce = _baseJumpForce;
            MoveSpeed = _baseMoveSpeed;
            DamageMultiplier = _baseDamageMultiplier;
            CastSpeedMultiplier = _baseCastSpeedMultiplier;
            ProtectionPercent = 0f;
            MaxHealthBonus = 0f;
        }

        private void Recalculate()
        {
            float manaRegen = _baseManaRegen;
            float jump = _baseJumpForce;
            float move = _baseMoveSpeed;
            float dmgMul = _baseDamageMultiplier;
            float castMul = _baseCastSpeedMultiplier;
            float protectionSum = 0f;
            float maxHealth = 0f;

            foreach (ItemStack eq in _inventory.Equipment)
            {
                if (eq.IsEmpty) continue;
                if (_database.GetById(eq.ItemId) is not EquipmentItemSO def) continue;

                foreach (StatModifier mod in def.Modifiers)
                {
                    switch (mod.Stat)
                    {
                        case StatType.ManaRegen:
                            manaRegen += mod.Value; break;
                        case StatType.JumpForce:
                            jump += mod.Value; break;
                        case StatType.MoveSpeed:
                            move += mod.Value; break;
                        case StatType.DamageMultiplier:
                            dmgMul += mod.Value; break;
                        case StatType.CastSpeedMultiplier:
                            castMul += mod.Value; break;
                        case StatType.Protection:
                            protectionSum += mod.Value; break; // protección: suma de %
                        case StatType.MaxHealth:
                            maxHealth += mod.Value; break;
                    }
                }
            }

            // Pisos de seguridad: sin esto, suficiente equipo negativo podría llevar un stat
            // a 0 o negativo. Es especialmente grave en DamageMultiplier — negativo
            // convertiría una habilidad ofensiva en curación para el objetivo.
            ManaRegen = Mathf.Max(0f, manaRegen);
            JumpForce = Mathf.Max(0f, jump);
            MoveSpeed = Mathf.Max(_minMoveSpeed, move);
            DamageMultiplier = Mathf.Max(_minDamageMultiplier, dmgMul);
            CastSpeedMultiplier = Mathf.Max(_minCastSpeedMultiplier, castMul);
            ProtectionPercent = Mathf.Clamp(protectionSum, 0f, _protectionCap);
            MaxHealthBonus = Mathf.Max(0f, maxHealth);

            OnStatsChanged?.Invoke();
        }

        // Para aditivo devuelve el valor; para multiplicativo, lo convierte a delta sobre la base.
        // Simplificación: tratamos ambos como contribución sumable al acumulador.

        /// <summary>
        /// Diferencias contra los valores base, listas para mostrar en UI. Solo devuelve
        /// stats que realmente cambiaron (equipo vacío = lista vacía). label ya viene
        /// formateado en inglés (texto visible al jugador); isPositive determina el color (verde/rojo) en la UI.
        /// </summary>
        public System.Collections.Generic.List<(string Label, bool IsPositive)> GetActiveModifierSummaries()
        {
            var result = new System.Collections.Generic.List<(string, bool)>();

            AddIfChanged(result, "Damage", DamageMultiplier, _baseDamageMultiplier, asPercent: true);
            AddIfChanged(result, "Cast Speed", CastSpeedMultiplier, _baseCastSpeedMultiplier, asPercent: true);
            AddIfChanged(result, "Move Speed", MoveSpeed, _baseMoveSpeed, asPercent: true);
            AddIfChanged(result, "Jump", JumpForce, _baseJumpForce, asPercent: true);
            AddIfChanged(result, "Mana Regen", ManaRegen, _baseManaRegen, asPercent: true);

            if (ProtectionPercent > 0.001f)
                result.Add(($"Protection +{ProtectionPercent * 100f:0}%", true));
            if (MaxHealthBonus > 0.001f)
                result.Add(($"Max Health +{MaxHealthBonus:0}", true));

            return result;
        }

        private void AddIfChanged(System.Collections.Generic.List<(string, bool)> result, string label, float current, float baseValue, bool asPercent)
        {
            if (Mathf.Approximately(current, baseValue) || baseValue == 0f) return;

            float deltaPercent = (current - baseValue) / baseValue * 100f;
            bool positive = deltaPercent > 0f;
            string sign = positive ? "+" : "";
            result.Add(($"{label} {sign}{deltaPercent:0}%", positive));
        }
    }
}