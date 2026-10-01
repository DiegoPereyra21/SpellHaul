using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace Game.Core.Items
{
    /// <summary>
    /// Kit de principiante: el equipo e items con los que arranca un jugador nuevo
    /// (inventario propio inicial la primera vez). Configurable en el Editor.
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Items/Starting Kit", fileName = "StartingKit")]
    public class StartingKitSO : ScriptableObject
    {
        [System.Serializable]
        public struct EquipEntry
        {
            public EquipmentSlot Slot;
            public EquipmentItemSO Item;
        }

        [System.Serializable]
        public struct StartingItemEntry
        {
            public ItemSO Item;
            [Min(1)] public int Quantity;
        }

        [SerializeField] private List<EquipEntry> _equipment = new List<EquipEntry>();

        // FormerlySerializedAs preserva los datos ya guardados en StartingKit_Basic.asset
        // (hoy serializados bajo "_backpack") aunque el campo cambie de nombre acá.
        [FormerlySerializedAs("_backpack")]
        [SerializeField] private List<StartingItemEntry> _startingItems = new List<StartingItemEntry>();

        [Tooltip("Consumibles en los slots de usables (teclas 1-2-3), en orden. Máximo 3.")]
        [SerializeField] private List<StartingItemEntry> _usables = new List<StartingItemEntry>();

        public IReadOnlyList<EquipEntry> Equipment => _equipment;
        public IReadOnlyList<StartingItemEntry> Usables => _usables;
        public IReadOnlyList<StartingItemEntry> StartingItems => _startingItems;
    }
}