using FishNet.Object;
using Game.Core.Abilities;
using Game.Presentation.Abilities;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace Game.Presentation.Bootstrap
{
    public class GameLifetimeScope : LifetimeScope
    {
        protected override void Configure(IContainerBuilder builder)
        {
            builder.Register<AbilityExecutor, NetworkAbilityExecutor>(Lifetime.Singleton);
        }

        // Llamar desde el propio NetworkBehaviour (ej. AbilityController.OnStartNetwork)
        // pasando el objeto recién spawneado, ya que VContainer no lo inyecta automáticamente.
        public void InjectSpawnedObject(NetworkObject spawned)
        {
            Container.InjectGameObject(spawned.gameObject);
        }
    }
}