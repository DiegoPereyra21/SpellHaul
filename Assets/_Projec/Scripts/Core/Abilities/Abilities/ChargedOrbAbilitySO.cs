using UnityEngine;

namespace Game.Core.Abilities.Abilities
{
    /// <summary>
    /// Orbe de carga sostenida: se mantiene presionado el botón para que crezca, y al soltar
    /// sale con trayectoria balística hacia el punto de mira. Tamaño/radio/daño escalan con la
    /// carga (más carga = más grande y potente); el arco escala al revés (más carga = más plano
    /// y rápido, menos carga = más lobbed). El tiempo de carga real lo mide el SERVIDOR.
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Abilities/Charged Orb Ability", fileName = "Ability_ChargedOrb_")]
    public class ChargedOrbAbilitySO : AbilitySO
    {
        [Header("Orbe")]
        [Tooltip("Prefab con ChargedOrbProjectile + NetworkObject.")]
        [SerializeField] private GameObject _orbPrefab;

        [Header("Carga")]
        [Tooltip("Segundos hasta la carga máxima. Al llegar se queda lleno; NO se auto-dispara.")]
        [SerializeField] private float _maxChargeDuration = 1.5f;

        [Header("Escalado por carga (mínimo = tap instantáneo, máximo = carga completa)")]
        [SerializeField] private float _minDamage = 15f;
        [SerializeField] private float _maxDamage = 55f;
        [SerializeField] private float _minExplosionRadius = 1.5f;
        [SerializeField] private float _maxExplosionRadius = 5f;
        [SerializeField] private float _minVisualScale = 0.4f;
        [SerializeField] private float _maxVisualScale = 1.6f;

        [Header("Trayectoria balística (según carga)")]
        [Tooltip("Velocidad de lanzamiento con tap (carga mínima): cae rápido, no llega lejos — como un tiro flojo.")]
        [SerializeField] private float _minChargeLaunchSpeed = 10f;
        [Tooltip("Velocidad de lanzamiento con carga máxima: vuela más recto y lejos antes de que la gravedad lo tumbe.")]
        [SerializeField] private float _maxChargeLaunchSpeed = 28f;
        [Tooltip("Gravedad del orbe (negativo). Constante — no cambia con la carga, solo la velocidad.")]
        [SerializeField] private float _gravity = -14f;

        public override float MaxChargeDuration => _maxChargeDuration;
        public override bool IsChargeable => true;

        public override bool ShowTrajectoryPreview => true;

        public override void GetLaunchForCharge(float chargeNormalized, out float launchSpeed, out float gravity)
        {
            float t = Mathf.Clamp01(chargeNormalized);
            launchSpeed = Mathf.Lerp(_minChargeLaunchSpeed, _maxChargeLaunchSpeed, t);
            gravity = _gravity; // fija: solo la velocidad cambia con la carga
        }

        public override void DescribeEffect(float power, System.Collections.Generic.List<string> lines)
        {
            lines.Add($"Damage {_minDamage * power:0}-{_maxDamage * power:0} (charge)");
            lines.Add($"Blast radius {_minExplosionRadius:0.#}-{_maxExplosionRadius:0.#} m");
        }

        public override void Execute(AbilityExecutor executor, in AbilityCastContext context)
        {
            float t = Mathf.Clamp01(context.ChargeNormalized);

            float damage = Mathf.Lerp(_minDamage, _maxDamage, t) * context.DamageMultiplier * context.AbilityPower;
            float radius = Mathf.Lerp(_minExplosionRadius, _maxExplosionRadius, t);
            float scale = Mathf.Lerp(_minVisualScale, _maxVisualScale, t);
            GetLaunchForCharge(t, out float launchSpeed, out float gravity);

            executor.SpawnChargedOrb(
                _orbPrefab,
                context.Origin,
                context.AimPoint,
                damage,
                radius,
                scale,
                launchSpeed,
                gravity,
                context.CasterNetworkId,
                context.Slot
            );
        }
    }
}