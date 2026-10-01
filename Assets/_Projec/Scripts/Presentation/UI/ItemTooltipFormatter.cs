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
            var stats = new List<StatLine>();

            // Guante: escuela + habilidad del clic derecho con la potencia de su rareza.
            if (def is GloveItemSO glove)
            {
                BuildGlove(glove, stats);
                return ($"{glove.Rarity} {glove.School} Glove", stats);
            }

            string type = def is EquipmentItemSO eq ? SlotDisplayName(eq.Slot) : def.Category.ToString();

            if (def is EquipmentItemSO equip)
            {
                if (equip.Slot.IsPocket() && equip.PocketSlots > 0)
                    stats.Add(new StatLine { Text = $"Size +{equip.PocketSlots}", Sign = 1 });

                foreach (var mod in equip.Modifiers)
                    stats.Add(FormatModifier(mod));
            }

            return ($"{def.Rarity} {type}", stats);
        }

        /// <summary>
        /// Etiqueta de rareza ("COMMON" / "RARE" / "EPIC") para las filas del equipo: se ve de un
        /// vistazo qué rareza tiene cada pieza puesta. Estilo en ItemCommon.uss.
        /// </summary>
        public static UnityEngine.UIElements.Label CreateRarityTag(ItemSO def)
        {
            var tag = new UnityEngine.UIElements.Label(def != null ? def.Rarity.ToString().ToUpperInvariant() : "");
            tag.AddToClassList("rarity-tag");
            tag.AddToClassList("rarity-tag--" + (def != null ? def.Rarity.ToString().ToLowerInvariant() : "common"));
            tag.pickingMode = UnityEngine.UIElements.PickingMode.Ignore;
            return tag;
        }

        private static readonly List<string> _effectLines = new List<string>();

        private static void BuildGlove(GloveItemSO glove, List<StatLine> stats)
        {
            var ability = glove.Ability;
            if (ability == null)
            {
                stats.Add(new StatLine { Text = "No ability", Sign = 0 });
                return;
            }

            stats.Add(new StatLine { Text = $"RMB: {ability.DisplayName}", Sign = 0 });

            // Rareza por encima de la base: las líneas de efecto se marcan como mejora.
            int powerSign = glove.AbilityPower > 1.001f ? 1 : (glove.AbilityPower < 0.999f ? -1 : 0);
            _effectLines.Clear();
            ability.DescribeEffect(glove.AbilityPower, _effectLines);
            foreach (string line in _effectLines)
                stats.Add(new StatLine { Text = line, Sign = powerSign });

            float cooldown = ability.Cooldown * glove.CooldownMultiplier;
            int cdSign = glove.CooldownMultiplier < 0.999f ? 1 : (glove.CooldownMultiplier > 1.001f ? -1 : 0);
            stats.Add(new StatLine { Text = $"Cooldown {cooldown:0.#}s", Sign = cdSign });
            if (ability.ResourceCost > 0f)
                stats.Add(new StatLine { Text = $"Mana {ability.ResourceCost:0}", Sign = 0 });

            // Un guante puede tener además modificadores pasivos (hoy ninguno los usa).
            foreach (var mod in glove.Modifiers)
                stats.Add(FormatModifier(mod));
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