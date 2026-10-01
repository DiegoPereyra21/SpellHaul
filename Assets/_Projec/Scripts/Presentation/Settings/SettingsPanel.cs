using System.Collections.Generic;
using Game.Presentation.Audio;
using Game.Presentation.Player;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Presentation.Settings
{
    /// <summary>
    /// Arma el contenido de opciones (Audio, Controls, Display) dentro de un contenedor de UI. Lo
    /// usan el menú principal y el menú de pausa, así que las dos pantallas muestran exactamente lo
    /// mismo. Los estilos están en UI/Settings.uss (cada documento lo incluye).
    /// </summary>
    public static class SettingsPanel
    {
        public static void Build(VisualElement container)
        {
            if (container == null) return;
            container.Clear();
            container.AddToClassList("settings");

            float lastPreview = -10f;
            void PreviewSound()
            {
                if (Time.unscaledTime - lastPreview < 0.15f) return;
                lastPreview = Time.unscaledTime;
                GameAudio.Ui(l => l.UiClick);
            }

            // ---------- Audio ----------
            Section(container, "Audio");
            SliderRow(container, "Master Volume", 0f, 1f, AudioVolumes.Master, v => { AudioVolumes.Master = v; PreviewSound(); }, Percent);
            SliderRow(container, "Effects", 0f, 1f, AudioVolumes.Effects, v => { AudioVolumes.Effects = v; PreviewSound(); }, Percent);
            SliderRow(container, "Interface", 0f, 1f, AudioVolumes.Interface, v => { AudioVolumes.Interface = v; PreviewSound(); }, Percent);

            // ---------- Controls ----------
            Section(container, "Controls");
            SliderRow(container, "Mouse Sensitivity", LookSettings.MinSensitivity, LookSettings.MaxSensitivity,
                LookSettings.Sensitivity, v => LookSettings.Sensitivity = v, v => $"{v:0.00}x");

            // ---------- Display ----------
            Section(container, "Display");

            var modes = new List<string>();
            int modeIndex = 0;
            for (int i = 0; i < DisplaySettings.WindowModes.Length; i++)
            {
                modes.Add(DisplaySettings.WindowModes[i].Label);
                if (DisplaySettings.WindowModes[i].Mode == DisplaySettings.WindowMode) modeIndex = i;
            }
            DropdownRow(container, "Window Mode", modes, modeIndex,
                i => DisplaySettings.SetWindowMode(DisplaySettings.WindowModes[i].Mode));

            var sizes = DisplaySettings.Resolutions();
            var sizeLabels = new List<string>();
            int sizeIndex = 0;
            var current = DisplaySettings.CurrentResolution;
            for (int i = 0; i < sizes.Count; i++)
            {
                sizeLabels.Add($"{sizes[i].x} x {sizes[i].y}");
                if (sizes[i] == current) sizeIndex = i;
            }
            DropdownRow(container, "Resolution", sizeLabels, sizeIndex, i => DisplaySettings.SetResolution(sizes[i]));

            ToggleRow(container, "VSync", DisplaySettings.VSync, v => DisplaySettings.VSync = v);

            var limits = new List<string>();
            int limitIndex = System.Array.IndexOf(DisplaySettings.FrameLimits, DisplaySettings.FrameLimit);
            foreach (int l in DisplaySettings.FrameLimits) limits.Add(l <= 0 ? "Unlimited" : $"{l} FPS");
            DropdownRow(container, "Frame Rate Limit", limits, limitIndex < 0 ? 3 : limitIndex,
                i => DisplaySettings.FrameLimit = DisplaySettings.FrameLimits[i]);

            var qualities = new List<string>(QualitySettings.names);
            if (qualities.Count > 1)
                DropdownRow(container, "Graphics Quality", qualities, DisplaySettings.Quality, i => DisplaySettings.Quality = i);
        }

        /// <summary>Guarda en disco lo cambiado (llamar al cerrar el panel).</summary>
        public static void Save()
        {
            AudioVolumes.Save();
            DisplaySettings.Save();
        }

        private static string Percent(float v) => $"{Mathf.RoundToInt(v * 100f)}%";

        private static void Section(VisualElement container, string title)
        {
            var label = new Label(title.ToUpperInvariant());
            label.AddToClassList("settings-section");
            container.Add(label);
        }

        private static VisualElement Row(VisualElement container, string label)
        {
            var row = new VisualElement();
            row.AddToClassList("settings-row");
            var name = new Label(label);
            name.AddToClassList("settings-label");
            row.Add(name);
            container.Add(row);
            return row;
        }

        private static void SliderRow(VisualElement container, string label, float min, float max, float value,
            System.Action<float> onChange, System.Func<float, string> format)
        {
            var row = Row(container, label);
            var slider = new Slider(min, max) { value = value };
            slider.AddToClassList("settings-slider");
            var valueLabel = new Label(format(value));
            valueLabel.AddToClassList("settings-value");
            slider.RegisterValueChangedCallback(evt =>
            {
                valueLabel.text = format(evt.newValue);
                onChange(evt.newValue);
            });
            row.Add(slider);
            row.Add(valueLabel);
        }

        private static void DropdownRow(VisualElement container, string label, List<string> choices, int index, System.Action<int> onChange)
        {
            var row = Row(container, label);
            var dropdown = new DropdownField(choices, Mathf.Clamp(index, 0, Mathf.Max(0, choices.Count - 1)));
            dropdown.AddToClassList("settings-dropdown");
            dropdown.RegisterValueChangedCallback(_ =>
            {
                onChange(dropdown.index);
                GameAudio.Ui(l => l.UiClick);
            });
            row.Add(dropdown);
        }

        private static void ToggleRow(VisualElement container, string label, bool value, System.Action<bool> onChange)
        {
            var row = Row(container, label);
            var toggle = new Toggle { value = value };
            toggle.AddToClassList("settings-toggle");
            toggle.RegisterValueChangedCallback(evt =>
            {
                onChange(evt.newValue);
                GameAudio.Ui(l => l.UiClick);
            });
            row.Add(toggle);
        }
    }
}
