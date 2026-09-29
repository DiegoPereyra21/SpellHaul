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

            GameObject instance = UnityEngine.Object.Instantiate(orbPrefab, origin + dir * 0.5f, Quaternion.LookRotation(dir));
            if (instance.TryGetComponent(out ChargedOrbProjectile orb))
            {
                InstanceFinder.ServerManager.Spawn(instance);
                orb.Initialize(aimPoint, damage, explosionRadius, visualScale, launchSpeed, gravity, casterNetworkId, slot);
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

        public void StartParry(Game.Core.Abilities.Abilities.ParryAbilitySO data, in AbilityCastContext context)
        {
            if (!InstanceFinder.IsServerStarted) return;

            if (!InstanceFinder.ServerManager.Objects.Spawned.TryGetValue(context.CasterNetworkId, out NetworkObject casterNob))
            {
                Debug.LogWarning($"[NetworkAbilityExecutor] Caster no encontrado. ID: {context.CasterNetworkId}");
                return;
            }

            if (casterNob.TryGetComponent(out ParryHandler handler))
                handler.StartParry(data, context.CasterNetworkId, context.AimDirection, context.Slot);
        }
    }
}