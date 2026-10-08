using UnityEngine;

namespace Game.Core.Abilities
{
    public readonly struct AbilityCastContext
    {
        public readonly int CasterNetworkId;
        public readonly Vector3 Origin;
        public readonly Vector3 AimDirection;
        public readonly Vector3 AimPoint;
        public readonly uint Tick;
        public readonly float DamageMultiplier;
        public readonly int Slot;             // slot casteado; sirve para resolver el AudioClip localmente en cada cliente
        public readonly float ChargeNormalized; // 0..1 de carga sostenida (solo habilidades cargables)
        public readonly float AbilityPower;     // potencia que aporta quien da la habilidad (rareza del guante); 1 = base
        public readonly uint PlayerViewTick;    // lag comp: tick del servidor en que el tirador veía a los otros jugadores (0 = sin dato)
        public readonly uint AiViewTick;        // lag comp: ídem para la IA (otra interpolación); 0 = sin dato

        public AbilityCastContext(int casterNetworkId, Vector3 origin, Vector3 aimDirection, Vector3 aimPoint, uint tick, float damageMultiplier, int slot, float chargeNormalized = 0f, float abilityPower = 1f, uint playerViewTick = 0, uint aiViewTick = 0)
        {
            CasterNetworkId = casterNetworkId;
            Origin = origin;
            AimDirection = aimDirection;
            AimPoint = aimPoint;
            Tick = tick;
            DamageMultiplier = damageMultiplier;
            Slot = slot;
            ChargeNormalized = chargeNormalized;
            AbilityPower = abilityPower;
            PlayerViewTick = playerViewTick;
            AiViewTick = aiViewTick;
        }
    }
}