using System.Collections;
using FishNet.Object;
using Game.Core.Abilities.Abilities;
using Game.Presentation.Combat;
using UnityEngine;

namespace Game.Presentation.Abilities
{
    /// <summary>
    /// Fases del parry (startup → active → recovery) en el servidor. Detección y VFX
    /// comparten centro y tamaño, ambos definidos en el ParryAbilitySO.
    /// </summary>
    public class ParryHandler : NetworkBehaviour
    {
        private Mana _mana;
        private AbilityController _abilityController;
        private bool _parrying;

        private void Awake()
        {
            _mana = GetComponent<Mana>();
            _abilityController = GetComponent<AbilityController>();
        }

        [Server]
        public void StartParry(ParryAbilitySO data, int casterNetworkId, Vector3 aimDirection, int slot = -1)
        {
            if (_parrying) return;
            StartCoroutine(ParryRoutine(data, aimDirection.normalized, slot));
        }

        /// <summary>Corta un parry en curso (muerte/extracción). Seguro de llamar en cualquier lado.</summary>
        public void CancelParry()
        {
            StopAllCoroutines();
            _parrying = false;
        }

        [Server]
        private IEnumerator ParryRoutine(ParryAbilitySO data, Vector3 aimDir, int slot)
        {
            _parrying = true;

            yield return new WaitForSeconds(data.StartupDuration);

            Vector3 parryPoint = GetParryPoint(data, aimDir);
            ShowParryVFXObserversRpc(ParryPhase.Active, parryPoint, data.VfxScale);

            float elapsed = 0f;
            while (elapsed < data.ActiveDuration)
            {
                if (TryDetectProjectile(parryPoint, data.ParryRadius, aimDir))
                {
                    _mana?.Restore(data.ManaRestore);
                    ShowParryVFXObserversRpc(ParryPhase.Success, parryPoint, data.VfxScale);
                    _abilityController?.NotifyAbilityImpactSfx(parryPoint, slot);
                    break;
                }
                elapsed += Time.deltaTime;
                yield return null;
            }

            yield return new WaitForSeconds(data.RecoveryDuration);
            _parrying = false;
        }

        /// <summary>Centro del parry: altura fija + adelante en la dirección de mirada.</summary>
        private Vector3 GetParryPoint(ParryAbilitySO data, Vector3 aimDir)
            => transform.position + Vector3.up * data.HeightOffset + aimDir * data.ForwardOffset;

        [Server]
        private bool TryDetectProjectile(Vector3 origin, float radius, Vector3 aimDir)
        {
            Collider[] hits = Physics.OverlapSphere(origin, radius, ~0, QueryTriggerInteraction.Ignore);

            foreach (Collider hit in hits)
            {
                if (!hit.TryGetComponent(out Projectile projectile)) continue;
                if (!projectile.TryGetComponent(out NetworkObject _)) continue;

                Vector3 toProjectile = (hit.transform.position - origin).normalized;
                if (Vector3.Dot(aimDir, toProjectile) < -0.2f) continue; // descartar lo que quedó atrás

                projectile.Despawn();
                return true;
            }
            return false;
        }

        [ObserversRpc]
        private void ShowParryVFXObserversRpc(ParryPhase phase, Vector3 point, float vfxScale)
        {
            switch (phase)
            {
                case ParryPhase.Active:
                    VFXManager.PlayParryActive(point, vfxScale);
                    break;
                case ParryPhase.Success:
                    VFXManager.PlayParrySuccess(point, vfxScale);
                    break;
            }
        }

        private enum ParryPhase { Active, Success }
    }
}