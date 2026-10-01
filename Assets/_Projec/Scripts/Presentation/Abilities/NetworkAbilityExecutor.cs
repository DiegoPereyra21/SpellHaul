using FishNet;
using FishNet.Object;
using Game.Core.Abilities;
using UnityEngine;
using FishNet.Connection;

namespace Game.Presentation.Abilities
{
    /// <summary>
    /// Implementación real de AbilityExecutor. Solo debe ejecutarse server-side
    /// (las AbilitySO.Execute se llaman desde CastServerRpc, que ya corre en el servidor).
    /// Usa el pooling NATIVO de Fish-Net para prefabs con NetworkObject: para que reutilice
    /// instancias en vez de Instantiate/Destroy, activar "Enable Pooling" en el NetworkObject
    /// del prefab del proyectil (Inspector) — no hace falta pooling manual acá.
    /// </summary>
    public class NetworkAbilityExecutor : AbilityExecutor
    {
        public void SpawnProjectile(GameObject projectilePrefab, Vector3 origin, Vector3 direction, float speed, float damage, float radius, int casterNetworkId, uint fireTick, int slot)
        {
            if (!InstanceFinder.IsServerStarted) return;

            // Owner = conexión del caster. El NT es server-authoritative, así que dar owner NO
            // cede autoridad: solo sirve para que el cliente del tirador reconozca su proyectil
            // (base.IsOwner) y lo oculte a favor de su cosmético local.
            NetworkConnection owner = null;
            if (InstanceFinder.ServerManager.Objects.Spawned.TryGetValue(casterNetworkId, out NetworkObject casterNob))
                owner = casterNob.Owner;

            GameObject instance = InstanceFinder.NetworkManager.GetPooledInstantiated(
                projectilePrefab.GetComponent<NetworkObject>(), asServer: true).gameObject;

            Vector3 spawnPos = origin + direction.normalized * 0.5f;
            instance.transform.SetPositionAndRotation(spawnPos, Quaternion.LookRotation(direction));

            if (instance.TryGetComponent(out Projectile projectile))
                projectile.Initialize(direction, speed, damage, radius, casterNetworkId, fireTick, slot);

            InstanceFinder.ServerManager.Spawn(instance, owner);
        }


        public void SpawnChargedOrb(GameObject orbPrefab, Vector3 origin, Vector3 aimPoint, float damage, float explosionRadius, float visualScale, float launchSpeed, float gravity, int casterNetworkId, int slot)
        {
            if (!InstanceFinder.IsServerStarted) return;
            if (orbPrefab == null) return;

            Vector3 toAim = aimPoint - origin;
            Vector3 dir = toAim.sqrMagnitude > 0.0001f ? toAim.normalized : Vector3.forward;

            NetworkObject nob = InstanceFinder.NetworkManager.GetPooledInstantiated(
                orbPrefab.GetComponent<NetworkObject>(), origin + dir * 0.5f, Quaternion.LookRotation(dir), true);
            if (nob.TryGetComponent(out ChargedOrbProjectile orb))
            {
                InstanceFinder.ServerManager.Spawn(nob);
                orb.Initialize(aimPoint, damage, explosionRadius, visualScale, launchSpeed, gravity, casterNetworkId, slot);
            }
        }

        public void SpawnFlashProjectile(GameObject prefab, Vector3 origin, Vector3 direction, float speed, float lifetime,
            float flashRadius, float playerBlindSeconds, float aiBlindSeconds, int casterNetworkId, int slot)
        {
            if (!InstanceFinder.IsServerStarted) return;
            if (prefab == null)
            {
                Debug.LogWarning("[NetworkAbilityExecutor] Orbe de destello sin prefab: asignarlo en el FlashOrbAbilitySO.");
                return;
            }

            Vector3 dir = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.forward;
            NetworkObject nob = InstanceFinder.NetworkManager.GetPooledInstantiated(
                prefab.GetComponent<NetworkObject>(), origin + dir * 0.5f, Quaternion.LookRotation(dir), true);
            if (nob.TryGetComponent(out FlashProjectile flash))
            {
                InstanceFinder.ServerManager.Spawn(nob);
                flash.Initialize(dir, speed, lifetime, flashRadius, playerBlindSeconds, aiBlindSeconds, casterNetworkId, slot);
            }
            else
            {
                Debug.LogWarning("[NetworkAbilityExecutor] El prefab del orbe de destello no tiene FlashProjectile.");
            }
        }

        // Suelo válido para el muro: lo bastante plano (normal hacia arriba).
        private const float MinGroundNormalY = 0.6f;

        public void SpawnEarthWall(GameObject prefab, Vector3 origin, Vector3 aimPoint, Vector3 aimDirection,
            float maxRange, float fallbackDistance, float health, float duration, int casterNetworkId)
        {
            if (!InstanceFinder.IsServerStarted) return;
            if (prefab == null)
            {
                Debug.LogWarning("[NetworkAbilityExecutor] Muro de tierra sin prefab: asignarlo en el EarthWallAbilitySO.");
                return;
            }
            if (!InstanceFinder.ServerManager.Objects.Spawned.TryGetValue(casterNetworkId, out NetworkObject casterNob)) return;

            Vector3 feet = casterNob.transform.position;
            int ground = LayerMask.GetMask("Ground");

            // El punto apuntado lo calcula el cliente: el servidor lo re-valida con su propio rayo
            // (desde el SpellOrigin autoritativo hacia ese punto, solo contra Ground).
            Vector3 toAim = aimPoint - origin;
            bool placed = false;
            Vector3 point = default;
            if (toAim.sqrMagnitude > 0.01f &&
                Physics.Raycast(origin, toAim.normalized, out RaycastHit hit, maxRange + 1f, ground, QueryTriggerInteraction.Ignore) &&
                hit.normal.y >= MinGroundNormalY &&
                Vector3.Distance(feet, hit.point) <= maxRange)
            {
                point = hit.point;
                placed = true;
            }

            if (!placed)
            {
                // Delante del jugador a distancia fija, apoyado en el suelo que haya ahí.
                Vector3 flat = new Vector3(aimDirection.x, 0f, aimDirection.z);
                if (flat.sqrMagnitude < 0.0001f) flat = casterNob.transform.forward;
                flat.Normalize();
                Vector3 probe = feet + flat * fallbackDistance;
                point = Physics.Raycast(probe + Vector3.up * 2f, Vector3.down, out RaycastHit down, 6f, ground, QueryTriggerInteraction.Ignore)
                    ? down.point
                    : new Vector3(probe.x, feet.y, probe.z);
            }

            // Mirando al caster (solo en horizontal).
            Vector3 face = feet - point;
            face.y = 0f;
            if (face.sqrMagnitude < 0.0001f) face = -casterNob.transform.forward;
            Quaternion rotation = Quaternion.LookRotation(face.normalized, Vector3.up);

            NetworkObject nob = InstanceFinder.NetworkManager.GetPooledInstantiated(
                prefab.GetComponent<NetworkObject>(), point, rotation, true);
            if (nob.TryGetComponent(out EarthWall wall))
            {
                InstanceFinder.ServerManager.Spawn(nob);
                wall.ServerInitialize(health, duration);
            }
            else
            {
                Debug.LogWarning("[NetworkAbilityExecutor] El prefab del muro no tiene EarthWall.");
            }
        }

        public void StartDash(int casterNetworkId, Vector3 direction, float speed, float duration)
        {
            if (!InstanceFinder.IsServerStarted) return;
            if (!InstanceFinder.ServerManager.Objects.Spawned.TryGetValue(casterNetworkId, out NetworkObject nob)) return;
            if (nob.TryGetComponent(out Player.PlayerMovementController movement))
                movement.StartDash(direction, speed, duration);
        }

        public void ApplySelfEffect(int casterNetworkId, float healAmount)
        {
            if (!InstanceFinder.IsServerStarted) return;
            if (!InstanceFinder.ServerManager.Objects.Spawned.TryGetValue(casterNetworkId, out NetworkObject nob)) return;

            if (nob.TryGetComponent(out IDamageable damageable))
                damageable.ApplyDamage(-healAmount, casterNetworkId);
        }
    }
}