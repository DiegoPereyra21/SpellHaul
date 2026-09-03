using UnityEngine;

namespace Game.Core.Items
{
    /// <summary>
    /// Guante: el 'arma' del mago. Es equipamiento del slot Glove con un perfil
    /// (Swift / Heavy / Focus) que modifica daño y velocidad de casteo.
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Items/Glove", fileName = "Glove_")]
    public class GloveItemSO : EquipmentItemSO
    {
        [Header("Guante")]
        [SerializeField] private GloveType _gloveType;

        public GloveType GloveType => _gloveType;
    }
}