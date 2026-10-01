using Game.Core.Abilities;
using UnityEngine;

namespace Game.Core.Items
{
    /// <summary>
    /// Guante: el 'arma' del mago. Ocupa el slot Glove y define la habilidad del botón
    /// secundario (clic derecho). Sin guante equipado no hay habilidad secundaria.
    ///
    /// Varios guantes pueden compartir la misma AbilitySO: lo que cambia entre rarezas es la
    /// potencia (daño/curación) y el cooldown, configurados acá. Un guante nuevo = un asset
    /// nuevo (o una entrada en ItemCatalogGenerator), sin tocar código; una habilidad nueva =
    /// una AbilitySO nueva.
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Items/Glove", fileName = "Glove_")]
    public class GloveItemSO : EquipmentItemSO
    {
        [Header("Guante")]
        [Tooltip("Escuela de magia (rol del guante). Se muestra en tooltip y HUD.")]
        [SerializeField] private GloveSchool _school;

        [Tooltip("Habilidad del botón secundario mientras este guante está equipado.")]
        [SerializeField] private AbilitySO _ability;

        [Tooltip("Multiplicador de potencia de la habilidad (daño o curación). 1 = valor base de la habilidad. Sube con la rareza.")]
        [SerializeField, Min(0.1f)] private float _abilityPower = 1f;

        [Tooltip("Multiplicador del cooldown de la habilidad. 1 = cooldown base; 0.8 = 20% más rápido. Baja con la rareza.")]
        [SerializeField, Min(0.1f)] private float _cooldownMultiplier = 1f;

        public GloveSchool School => _school;
        public AbilitySO Ability => _ability;
        public float AbilityPower => _abilityPower;
        public float CooldownMultiplier => _cooldownMultiplier;
    }
}
