using System.Collections.Generic;

namespace Game.Presentation.Abilities
{
    /// <summary>Algo lanzado por una habilidad que el caster puede re-activar (ej. detonar en el aire).</summary>
    public interface IRecastable
    {
        void Recast();
    }

    /// <summary>
    /// Server-only. Lo lanzado que sigue activo, por (caster, slot): AbilityController lo busca acá
    /// cuando el dueño vuelve a apretar el botón de una habilidad IsRecastable.
    /// </summary>
    public static class RecastRegistry
    {
        private static readonly Dictionary<(int caster, int slot), IRecastable> _active = new();

        public static void Register(int casterNetworkId, int slot, IRecastable recastable)
            => _active[(casterNetworkId, slot)] = recastable;

        /// <summary>Lo saca solo si sigue siendo el mismo (uno nuevo pudo haberlo reemplazado).</summary>
        public static void Unregister(int casterNetworkId, int slot, IRecastable recastable)
        {
            if (_active.TryGetValue((casterNetworkId, slot), out var current) && ReferenceEquals(current, recastable))
                _active.Remove((casterNetworkId, slot));
        }

        public static bool TryRecast(int casterNetworkId, int slot)
        {
            if (!_active.TryGetValue((casterNetworkId, slot), out var recastable)) return false;
            _active.Remove((casterNetworkId, slot));
            recastable.Recast();
            return true;
        }
    }
}
