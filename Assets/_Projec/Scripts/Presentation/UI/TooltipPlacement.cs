using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Presentation.UI
{
    /// <summary>
    /// Ubica un tooltip junto a su slot sin que se salga del panel. Prefiere arriba del slot;
    /// si no entra, abajo; si tampoco, al costado. Siempre queda dentro del contenedor.
    /// Se posiciona un frame después de mostrarlo, cuando ya se conoce su tamaño real (antes
    /// dependía de GeometryChanged, que no se dispara si el tamaño no cambió).
    /// </summary>
    public static class TooltipPlacement
    {
        private const float Gap = 10f;    // aire entre slot y tooltip
        private const float Margin = 8f;  // distancia mínima al borde del panel

        public static void Show(VisualElement tooltip, VisualElement anchor)
        {
            if (tooltip == null || anchor == null) return;
            tooltip.style.visibility = Visibility.Hidden; // invisible hasta ubicarlo: sin parpadeo
            tooltip.style.display = DisplayStyle.Flex;
            tooltip.schedule.Execute(() => Place(tooltip, anchor));
        }

        private static void Place(VisualElement tooltip, VisualElement anchor)
        {
            if (tooltip.style.display.value == DisplayStyle.None || tooltip.parent == null) return; // se ocultó antes de ubicarlo

            Rect area = tooltip.parent.worldBound;
            Rect a = anchor.worldBound;
            float w = tooltip.layout.width;
            float h = tooltip.layout.height;

            float minX = area.xMin + Margin, maxX = area.xMax - Margin - w;
            float minY = area.yMin + Margin, maxY = area.yMax - Margin - h;

            float x = Mathf.Clamp(a.xMin, minX, Mathf.Max(minX, maxX));
            float y;
            if (a.yMin - Gap - h >= minY)
                y = a.yMin - Gap - h;                 // arriba
            else if (a.yMax + Gap <= maxY)
                y = a.yMax + Gap;                     // abajo
            else
            {
                // Ni arriba ni abajo: al costado (derecha si entra, si no izquierda).
                x = a.xMax + Gap <= maxX ? a.xMax + Gap : a.xMin - Gap - w;
                x = Mathf.Clamp(x, minX, Mathf.Max(minX, maxX));
                y = Mathf.Clamp(a.center.y - h * 0.5f, minY, Mathf.Max(minY, maxY));
            }

            tooltip.style.left = x - area.xMin;
            tooltip.style.top = y - area.yMin;
            tooltip.style.visibility = Visibility.Visible;
        }
    }
}
