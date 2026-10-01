using UnityEngine;

namespace Game.Core.Abilities
{
    /// <summary>
    /// Puerto (en el sentido de arquitectura hexagonal) que las AbilitySO usan para producir
    /// efectos concretos. La implementación real vive en Presentation y sabe de FishNet/pooling;
    /// las habilidades en Core nunca dependen de eso directamente. Se inyecta con VContainer.
    /// </summary>
    public interface AbilityExecutor
    {
        /// <summary>Spawnea un proyectil de red desde el pool (server-authoritative). slot: para resolver audio localmente en cada cliente.</summary>
        void SpawnProjectile(GameObject projectilePrefab, Vector3 origin, Vector3 direction, float speed, float damage, float radius, int casterNetworkId, uint fireTick, int slot);

        /// <summary>Aplica un impulso de movimiento al caster fuera de su input (el dash del jugador viaja como input predicho; ver PlayerMovementController).</summary>
        void StartDash(int casterNetworkId, Vector3 direction, float speed, float duration);

        /// <summary>Aplica curación/buff directo al caster.</summary>
        void ApplySelfEffect(int casterNetworkId, float healAmount);

        /// <summary>Spawnea un orbe cargado con trayectoria balística hacia el punto de mira.</summary>
        void SpawnChargedOrb(GameObject orbPrefab, Vector3 origin, Vector3 aimPoint, float damage, float explosionRadius, float visualScale, float launchSpeed, float gravity, int casterNetworkId, int slot);
    }
}
