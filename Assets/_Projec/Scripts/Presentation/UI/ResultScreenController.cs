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
            Game.Presentation.Audio.GameAudio.AttachButtonSounds(_root);
        }

        /// <summary>Muestra la pantalla con el resultado. extracted=true si extrajo, false si murió.</summary>
        public void Show(bool extracted)
        {
            // Desde acá, que el servidor corte la conexión (fin de la run) es lo esperado.
            NetworkDisconnectHandler.NotifyLocalRunFinished();

            // Puede llegar dos veces (salir de la run lo muestra al instante y después llega la
            // confirmación del servidor): la segunda solo actualiza el contenido.
            bool alreadyShown = _root.style.display == DisplayStyle.Flex;
            _root.style.display = DisplayStyle.Flex;
            if (!alreadyShown)
                Game.Presentation.Audio.GameAudio.Ui(l => extracted ? l.ExtractionComplete : l.Died);

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
                bool left = RunSummary.DeathCause == "You left the run.";
                _title.text = left ? "RUN ABANDONED" : "ELIMINATED";
                _title.AddToClassList("died");
                _subtitle.text = string.IsNullOrEmpty(RunSummary.DeathCause)
                    ? "You fell in the run. You lost what you carried."
                    : $"{RunSummary.DeathCause} You lost what you carried.";
            }

            FillSummary(extracted);

            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;
        }

        /// <summary>Tiempo, enemigos derrotados y la lista de lo que sacó (o perdió), con color de rareza.</summary>
        private void FillSummary(bool extracted)
        {
            var root = _root;
            int seconds = Mathf.FloorToInt(RunSummary.ElapsedSeconds);
            SetText(root, "stat-time", $"{seconds / 60:00}:{seconds % 60:00}");
            SetText(root, "stat-kills", RunSummary.Kills.ToString());

            var snapshot = extracted ? RunSummary.ExtractedItems : RunSummary.LostItems;
            var items = new System.Collections.Generic.List<Game.Core.Items.ItemStack>();
            if (snapshot != null)
            {
                foreach (var s in snapshot.Equipment) if (!s.IsEmpty) items.Add(s);
                foreach (var s in snapshot.PocketL) if (!s.IsEmpty) items.Add(s);
                foreach (var s in snapshot.PocketR) if (!s.IsEmpty) items.Add(s);
                if (snapshot.Usables != null) foreach (var s in snapshot.Usables) if (!s.IsEmpty) items.Add(s);
            }

            var db = RunSummary.Database;
            if (db != null)
                items.Sort((a, b) => Game.Core.Items.ItemSorting.Compare(a, db.GetById(a.ItemId), b, db.GetById(b.ItemId), Game.Core.Items.ItemSortMode.Rarity));

            int total = 0;
            foreach (var s in items) total += s.Quantity;
            SetText(root, "stat-items", total.ToString());
            SetText(root, "stat-items-label", extracted ? "Items extracted" : "Items lost");
            SetText(root, "items-header", extracted ? "EXTRACTED" : "LOST");

            var list = root.Q<ScrollView>("items-list");
            if (list == null) return;
            list.Clear();

            if (items.Count == 0)
            {
                var empty = new Label(extracted ? "You came back empty-handed." : "You carried nothing.");
                empty.AddToClassList("result-items-empty");
                list.Add(empty);
                return;
            }

            foreach (var stack in items)
            {
                var def = db != null ? db.GetById(stack.ItemId) : null;
                var row = new VisualElement();
                row.AddToClassList("result-item-row");

                var name = new Label(def != null ? def.DisplayName : stack.ItemId);
                name.AddToClassList("result-item-name");
                name.AddToClassList(ItemTooltipFormatter.RarityClass(def));
                row.Add(name);

                if (stack.Quantity > 1)
                {
                    var qty = new Label($"x{stack.Quantity}");
                    qty.AddToClassList("result-item-qty");
                    row.Add(qty);
                }
                list.Add(row);
            }
        }

        private static void SetText(VisualElement root, string name, string text)
        {
            var label = root.Q<Label>(name);
            if (label != null) label.text = text;
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