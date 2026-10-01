using Game.Core.Items;
using UnityEngine.UIElements;

namespace Game.Presentation.UI
{
    /// <summary>
    /// Estilo propio de los guantes en stash, inventario de run y tooltip, para distinguirlos del
    /// resto del equipamiento (Gloves.uss). El color sale de la escuela del guante; el borde
    /// sigue mostrando la rareza como en cualquier item.
    /// </summary>
    public static class GloveVisuals
    {
        public static string SchoolClass(GloveSchool school) => "school-" + school.ToString().ToLowerInvariant();

        /// <summary>Casilla de grilla: silueta redondeada, rombo de escuela y banda inferior.</summary>
        public static void ApplyToSlot(VisualElement slot, ItemSO def)
        {
            if (!(def is GloveItemSO glove)) return;
            slot.AddToClassList("item-glove");
            slot.AddToClassList(SchoolClass(glove.School));

            var band = new VisualElement { pickingMode = PickingMode.Ignore };
            band.AddToClassList("glove-band");
            slot.Add(band);

            var badge = new VisualElement { pickingMode = PickingMode.Ignore };
            badge.AddToClassList("glove-badge");
            slot.Add(badge);
        }

        /// <summary>
        /// Fila del loadout: franja de escuela, rombo en vez de punto y, debajo del nombre, la
        /// habilidad que da el guante.
        /// </summary>
        public static void ApplyToEquipRow(VisualElement row, VisualElement itemWrap, VisualElement dot, Label name, ItemSO def)
        {
            if (!(def is GloveItemSO glove)) return;
            row.AddToClassList("equip-slot-glove");
            row.AddToClassList(SchoolClass(glove.School));
            dot?.AddToClassList("glove-dot");

            if (itemWrap == null || name == null) return;
            var column = new VisualElement { pickingMode = PickingMode.Ignore };
            column.AddToClassList("equip-name-column");
            int index = itemWrap.IndexOf(name);
            itemWrap.Remove(name);
            column.Add(name);

            var sub = new Label(glove.Ability != null ? $"RMB · {glove.Ability.DisplayName}" : "No ability");
            sub.AddToClassList("glove-ability");
            sub.pickingMode = PickingMode.Ignore;
            column.Add(sub);
            itemWrap.Insert(index >= 0 ? index : itemWrap.childCount, column);
        }

        /// <summary>Tooltip: marca de escuela si es guante, limpia si no (el tooltip se reutiliza).</summary>
        public static void ApplyToTooltip(VisualElement tooltip, ItemSO def)
        {
            if (tooltip == null) return;
            tooltip.RemoveFromClassList("tooltip-glove");
            foreach (GloveSchool s in System.Enum.GetValues(typeof(GloveSchool)))
                tooltip.RemoveFromClassList(SchoolClass(s));

            if (def is GloveItemSO glove)
            {
                tooltip.AddToClassList("tooltip-glove");
                tooltip.AddToClassList(SchoolClass(glove.School));
            }
        }
    }
}
