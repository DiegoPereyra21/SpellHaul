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

        /// <summary>
        /// Spawnea un proyectil lento que al detonar (choque, fin de vida o re-activación del
        /// caster) ciega: a los jugadores según su mirada y distancia, a la IA por un tiempo fijo.
        /// </summary>
        void SpawnFlashProjectile(GameObject prefab, Vector3 origin, Vector3 direction, float speed, float lifetime,
            float flashRadius, float playerBlindSeconds, float aiBlindSeconds, int casterNetworkId, int slot);

        /// <summary>
        /// Levanta un muro de tierra en el suelo apuntado (validado por el servidor) o, si no hay
        /// suelo válido a alcance, a fallbackDistance delante del caster. El muro mira al caster.
        /// </summary>
        void SpawnEarthWall(GameObject prefab, Vector3 origin, Vector3 aimPoint, Vector3 aimDirection,
            float maxRange, float fallbackDistance, float health, float duration, int casterNetworkId);

        /// <summary>Spawnea un orbe cargado con trayectoria balística hacia el punto de mira.</summary>
        void SpawnChargedOrb(GameObject orbPrefab, Vector3 origin, Vector3 aimPoint, float damage, float explosionRadius, float visualScale, float launchSpeed, float gravity, int casterNetworkId, int slot);
    }
}
