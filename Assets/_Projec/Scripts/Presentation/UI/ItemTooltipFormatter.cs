using System.Collections.Generic;
using Game.Core.Items;

namespace Game.Presentation.UI
{
    /// <summary>
    /// Arma el contenido de un tooltip de item (tipo, tamaño si es pocket, buffs/debuffs).
    /// Compartido entre Stash e Inventario para no duplicar el formateo de stats.
    /// </summary>
    public static class ItemTooltipFormatter
    {
        public struct StatLine
        {
            public string Text;
            public int Sign; // 1 = positivo (buff), -1 = negativo (debuff), 0 = neutro
        }

        public static (string type, List<StatLine> stats) Build(ItemSO def)
        {
            string type = def is EquipmentItemSO eq ? SlotDisplayName(eq.Slot) : def.Category.ToString();
            var stats = new List<StatLine>();

            if (def is EquipmentItemSO equip)
            {
                if (equip.Slot.IsPocket() && equip.PocketSlots > 0)
                    stats.Add(new StatLine { Text = $"Size +{equip.PocketSlots}", Sign = 1 });

                foreach (var mod in equip.Modifiers)
                    stats.Add(FormatModifier(mod));
            }

            return (type, stats);
        }

        /// <summary>Clase CSS de color según rareza. Común = color por defecto (sin clase especial).</summary>
        public static string RarityClass(ItemSO def)
        {
            if (def == null) return "rarity-common";
            return def.Rarity switch
            {
                Rarity.Rare => "rarity-rare",
                Rarity.Epic => "rarity-epic",
                _ => "rarity-common"
            };
        }

        private static StatLine FormatModifier(StatModifier mod)
        {
            string statName = StatDisplayName(mod.Stat);

            // Protection se guarda como fracción (0..1); el resto de los stats son valores planos.
            bool isPercent = mod.Stat == StatType.Protection;
            float displayValue = isPercent ? mod.Value * 100f : mod.Value;

            string sign = displayValue >= 0 ? "+" : "";
            string text = $"{sign}{displayValue:0.#}{(isPercent ? "%" : "")} {statName}";
            int signValue = displayValue > 0f ? 1 : (displayValue < 0f ? -1 : 0);
            return new StatLine { Text = text, Sign = signValue };
        }

        private static string StatDisplayName(StatType stat) => stat switch
        {
            StatType.ManaRegen => "Mana Regen",
            StatType.JumpForce => "Jump Force",
            StatType.MoveSpeed => "Move Speed",
            StatType.Protection => "Protection",
            StatType.DamageMultiplier => "Damage",
            StatType.CastSpeedMultiplier => "Cast Speed",
            _ => stat.ToString()
        };

        private static string SlotDisplayName(EquipmentSlot slot) => slot switch
        {
            EquipmentSlot.PocketL => "Pocket",
            EquipmentSlot.PocketR => "Pocket",
            _ => slot.ToString()
        };
    }
}