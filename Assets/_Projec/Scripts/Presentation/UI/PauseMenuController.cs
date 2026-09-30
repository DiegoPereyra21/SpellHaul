using Game.Presentation.Abilities;
using Game.Presentation.Audio;
using Game.Presentation.Combat;
using Game.Presentation.Player;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Game.Presentation.UI
{
    /// <summary>
    /// Menú de pausa de la run (Esc): volúmenes, sensibilidad del mouse y "Leave Run". Es
    /// multijugador, así que no detiene el juego: solo libera el cursor y bloquea el control del
    /// personaje mientras está abierto. Esc con el inventario abierto primero cierra el inventario.
    /// Lo agrega HUDController (solo en el dueño) sobre el panel "pause-root" del HUD.
    /// </summary>
    public class PauseMenuController : MonoBehaviour
    {
        /// <summary>True mientras el menú está abierto (el inventario no se abre con Tab).</summary>
        public static bool IsPaused { get; private set; }

        private VisualElement _pauseRoot;
        private Button _leaveButton;
        private bool _leaveArmed;
        private bool _leaving;
        private float _lastPreview;

        private PlayerMovementController _movement;
        private AbilityController _abilities;
        private PlayerInteraction _interaction;
        private InventoryUIController _inventory;
        private PlayerDeathHandler _deathHandler;
        private PlayerAvatarState _avatar;

        public void Init(VisualElement hudRoot)
        {
            _pauseRoot = hudRoot.Q<VisualElement>("pause-root");
            if (_pauseRoot == null) { enabled = false; return; }

            _movement = GetComponent<PlayerMovementController>();
            _abilities = GetComponent<AbilityController>();
            _interaction = GetComponent<PlayerInteraction>();
            _inventory = GetComponent<InventoryUIController>();
            _deathHandler = GetComponent<PlayerDeathHandler>();
            _avatar = GetComponent<PlayerAvatarState>();

            BindSlider("pause-volume-master", () => AudioVolumes.Master, v => AudioVolumes.Master = v);
            BindSlider("pause-volume-effects", () => AudioVolumes.Effects, v => AudioVolumes.Effects = v);
            BindSlider("pause-volume-interface", () => AudioVolumes.Interface, v => AudioVolumes.Interface = v);
            BindSlider("pause-sensitivity", () => LookSettings.Sensitivity, v => LookSettings.Sensitivity = v, preview: false);

            var resume = _pauseRoot.Q<Button>("pause-resume");
            if (resume != null) resume.clicked += () => SetPaused(false);
            _leaveButton = _pauseRoot.Q<Button>("pause-leave");
            if (_leaveButton != null) _leaveButton.clicked += OnLeaveClicked;

            GameAudio.AttachButtonSounds(_pauseRoot);
            _pauseRoot.style.display = DisplayStyle.None;
        }

        private void OnDisable()
        {
            if (IsPaused) IsPaused = false;
        }

        private void Update()
        {
            // Murió, extrajo o salió de la run: la pantalla de resultados toma el control.
            if (_leaving) return;
            if (_avatar != null && _avatar.IsControlDisabled)
            {
                if (IsPaused) HideWithoutResuming();
                return;
            }
            if (Keyboard.current == null || !Keyboard.current.escapeKey.wasPressedThisFrame) return;

            if (IsPaused) { SetPaused(false); return; }
            if (_inventory != null && _inventory.IsOpen) { _inventory.Close(); return; }
            SetPaused(true);
        }

        private void SetPaused(bool paused)
        {
            if (_pauseRoot == null || paused == IsPaused) return;
            IsPaused = paused;
            _pauseRoot.style.display = paused ? DisplayStyle.Flex : DisplayStyle.None;

            UnityEngine.Cursor.lockState = paused ? CursorLockMode.None : CursorLockMode.Locked;
            UnityEngine.Cursor.visible = paused;
            BlockInput(paused);

            GameAudio.Ui(l => paused ? l.UiOpen : l.UiClose);
            if (!paused)
            {
                AudioVolumes.Save();
                DisarmLeave();
            }
        }

        private void BlockInput(bool blocked)
        {
            if (_movement != null) _movement.SetInputBlocked(blocked);
            if (_abilities != null) _abilities.SetInputBlocked(blocked);
            if (_interaction != null) _interaction.SetInputBlocked(blocked);
        }

        /// <summary>Cierra el menú sin devolver el control (lo maneja la pantalla de resultados).</summary>
        private void HideWithoutResuming()
        {
            IsPaused = false;
            _leaving = false;
            if (_pauseRoot != null) _pauseRoot.style.display = DisplayStyle.None;
            AudioVolumes.Save();
            DisarmLeave();
        }

        // ---------- Leave Run ----------

        private void OnLeaveClicked()
        {
            if (!_leaveArmed)
            {
                _leaveArmed = true;
                _leaveButton.text = "Confirm: lose your gear";
                _leaveButton.AddToClassList("armed");
                return;
            }

            if (_deathHandler == null) return;

            // Desde acá la run terminó para este jugador: nada más que hacer en ella. No se espera
            // al servidor para mostrarlo (que lo resuelve como una muerte: suelta el equipo y lo
            // pierde); su confirmación solo actualiza la misma pantalla.
            _leaving = true;
            _deathHandler.LeaveRunServerRpc();

            RunSummary.SetDeath("You left the run.", SnapshotLocalInventory());
            HideWithoutResuming();
            _leaving = true; // HideWithoutResuming lo limpia: sigue saliendo

            if (_inventory != null) _inventory.Close();
            if (_avatar != null) _avatar.DisableControl(); // local: sin control ni cuerpo visible

            var result = FindFirstObjectByType<ResultScreenController>();
            if (result != null) result.Show(false);
        }

        /// <summary>Lo que el jugador lleva según su copia sincronizada del inventario.</summary>
        private Game.Core.Items.InventorySnapshot SnapshotLocalInventory()
        {
            var snap = new Game.Core.Items.InventorySnapshot();
            var inv = GetComponent<RunInventory>();
            if (inv == null) return snap;
            foreach (var s in inv.Equipment) snap.Equipment.Add(s);
            foreach (var s in inv.PocketL) if (!s.IsEmpty) snap.PocketL.Add(s);
            foreach (var s in inv.PocketR) if (!s.IsEmpty) snap.PocketR.Add(s);
            return snap;
        }

        private void DisarmLeave()
        {
            _leaveArmed = false;
            if (_leaveButton == null) return;
            _leaveButton.text = "Leave Run";
            _leaveButton.RemoveFromClassList("armed");
            _leaveButton.SetEnabled(true);
        }

        // ---------- Opciones ----------

        private void BindSlider(string name, System.Func<float> get, System.Action<float> set, bool preview = true)
        {
            var slider = _pauseRoot.Q<Slider>(name);
            if (slider == null) return;
            slider.SetValueWithoutNotify(get());
            slider.RegisterValueChangedCallback(evt =>
            {
                set(evt.newValue);
                if (!preview || Time.unscaledTime - _lastPreview < 0.15f) return;
                _lastPreview = Time.unscaledTime;
                GameAudio.Ui(l => l.UiClick);
            });
        }
    }
}
