using FishNet;
using FishNet.Managing.Scened;
using Game.Presentation.Bootstrap;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Presentation.UI
{
    /// <summary>
    /// Pantalla de resultados de la run (Extraído / Eliminado). La dispara el estado individual
    /// del jugador (muerte o extracción). El botón vuelve al menú.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class ResultScreenController : MonoBehaviour
    {
        [SerializeField] private string _menuSceneName = "MainMenu";

        private UIDocument _document;
        private VisualElement _root;
        private Label _title;
        private Label _subtitle;

        private void OnEnable()
        {
            _document = GetComponent<UIDocument>();
            _root = _document.rootVisualElement.Q<VisualElement>("result-root");
            _title = _root.Q<Label>("result-title");
            _subtitle = _root.Q<Label>("result-subtitle");

            var returnBtn = _root.Q<Button>("return-button");
            if (returnBtn != null)
                returnBtn.clicked += OnReturnClicked;
        }

        /// <summary>Muestra la pantalla con el resultado. extracted=true si extrajo, false si murió.</summary>
        public void Show(bool extracted)
        {
            // Desde acá, que el servidor corte la conexión (fin de la run) es lo esperado.
            NetworkDisconnectHandler.NotifyLocalRunFinished();

            _root.style.display = DisplayStyle.Flex;

            _title.RemoveFromClassList("extracted");
            _title.RemoveFromClassList("died");

            if (extracted)
            {
                _title.text = "EXTRACTED";
                _title.AddToClassList("extracted");
                _subtitle.text = "You survived. Your loot is safe.";
            }
            else
            {
                _title.text = "ELIMINATED";
                _title.AddToClassList("died");
                _subtitle.text = "You fell in the run. You lost what you carried.";
            }

            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;
        }

            private void OnReturnClicked()
        {
            // Salida voluntaria: que el handler de desconexión no la trate como caída.
            NetworkDisconnectHandler.NotifyIntentionalDisconnect();

            if (InstanceFinder.IsServerStarted) InstanceFinder.ServerManager.StopConnection(true);
            if (InstanceFinder.IsClientStarted) InstanceFinder.ClientManager.StopConnection();

            UnityEngine.SceneManagement.SceneManager.LoadScene(_menuSceneName);
        }
    }
}