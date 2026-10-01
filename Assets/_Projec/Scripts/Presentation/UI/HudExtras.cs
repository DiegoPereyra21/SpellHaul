using System.Collections.Generic;
using FishNet;
using Game.Presentation.Abilities;
using Game.Presentation.Settings;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Presentation.UI
{
    /// <summary>
    /// Agregados del HUD que dependen de las opciones del jugador:
    /// - Mira: color y tamaño (HudSettings).
    /// - Números de daño flotantes donde pegaste (opcional).
    /// - Contador de FPS y ping (opcional).
    /// - Aviso de conexión inestable: si dejan de llegar las respuestas del servidor unos segundos,
    ///   se avisa en vez de que el juego parezca congelado.
    /// Lo agrega HUDController en el dueño.
    /// </summary>
    public class HudExtras : MonoBehaviour
    {
        private const float DamageNumberLifetime = 0.8f;
        private const float DamageNumberRise = 46f;
        private const int MaxDamageNumbers = 24;
        private const float StatsInterval = 0.5f;
        // El ping se mide cada 1 s: sin respuesta en este tiempo, la conexión está trabada.
        private const float UnstableAfterSeconds = 2.5f;

        private VisualElement _root;
        private VisualElement _crosshair;
        private VisualElement _damageLayer;
        private Label _statsLabel;
        private Label _connectionLabel;
        private Camera _camera;
        private AbilityController _abilities;

        private float _statsTimer;
        private int _frames;
        private float _lastPingTime;

        private struct DamageNumber
        {
            public Label Label;
            public Vector2 Origin;
            public float Age;
        }

        private readonly List<DamageNumber> _numbers = new();
        private readonly Stack<Label> _labelPool = new();

        public void Init(VisualElement hudRoot, AbilityController abilities, Camera camera)
        {
            _root = hudRoot;
            _abilities = abilities;
            _camera = camera;
            _crosshair = hudRoot.Q<VisualElement>("crosshair");

            _damageLayer = new VisualElement { pickingMode = PickingMode.Ignore };
            _damageLayer.AddToClassList("damage-number-layer");
            hudRoot.Add(_damageLayer);
            _damageLayer.SendToBack();

            _statsLabel = new Label { pickingMode = PickingMode.Ignore };
            _statsLabel.AddToClassList("net-stats");
            hudRoot.Add(_statsLabel);

            _connectionLabel = new Label("Connection unstable...") { pickingMode = PickingMode.Ignore };
            _connectionLabel.AddToClassList("connection-warning");
            _connectionLabel.style.display = DisplayStyle.None;
            hudRoot.Add(_connectionLabel);

            if (_abilities != null) _abilities.OnDamageDealt += ShowDamageNumber;
            HudSettings.Changed += ApplySettings;

            _lastPingTime = Time.unscaledTime;
            if (InstanceFinder.TimeManager != null) InstanceFinder.TimeManager.OnRoundTripTimeUpdated += OnPing;

            ApplySettings();
        }

        private void OnDestroy()
        {
            if (_abilities != null) _abilities.OnDamageDealt -= ShowDamageNumber;
            HudSettings.Changed -= ApplySettings;
            if (InstanceFinder.TimeManager != null) InstanceFinder.TimeManager.OnRoundTripTimeUpdated -= OnPing;
        }

        private void OnPing(long rtt) => _lastPingTime = Time.unscaledTime;

        private void ApplySettings()
        {
            if (_crosshair != null)
            {
                float size = 6f * HudSettings.CrosshairSize;
                _crosshair.style.width = size;
                _crosshair.style.height = size;
                var radius = new StyleLength(size * 0.5f);
                _crosshair.style.borderTopLeftRadius = radius;
                _crosshair.style.borderTopRightRadius = radius;
                _crosshair.style.borderBottomLeftRadius = radius;
                _crosshair.style.borderBottomRightRadius = radius;
                _crosshair.style.backgroundColor = HudSettings.CrosshairColor;
            }

            if (_statsLabel != null)
                _statsLabel.style.display = HudSettings.ShowNetStats ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void Update()
        {
            UpdateStats();
            UpdateConnectionWarning();
            UpdateDamageNumbers();
        }

        // ---------- FPS / ping ----------

        private void UpdateStats()
        {
            if (_statsLabel == null || !HudSettings.ShowNetStats) return;
            _frames++;
            _statsTimer += Time.unscaledDeltaTime;
            if (_statsTimer < StatsInterval) return;

            int fps = Mathf.RoundToInt(_frames / _statsTimer);
            _frames = 0;
            _statsTimer = 0f;
            long ping = InstanceFinder.TimeManager != null ? InstanceFinder.TimeManager.RoundTripTime : 0;
            _statsLabel.text = $"{fps} FPS · {ping} ms";
        }

        // ---------- Conexión ----------

        private void UpdateConnectionWarning()
        {
            if (_connectionLabel == null) return;
            bool connected = InstanceFinder.ClientManager != null && InstanceFinder.ClientManager.Started;
            bool unstable = connected && !InstanceFinder.IsServerStarted
                            && Time.unscaledTime - _lastPingTime > UnstableAfterSeconds;
            var display = unstable ? DisplayStyle.Flex : DisplayStyle.None;
            if (_connectionLabel.style.display != display) _connectionLabel.style.display = display;
        }

        // ---------- Números de daño ----------

        private void ShowDamageNumber(Vector3 worldPoint, float damage, bool isKill)
        {
            if (!HudSettings.ShowDamageNumbers || _damageLayer == null || _camera == null) return;
            if (_damageLayer.panel == null) return;

            // Detrás de la cámara no se muestra.
            if (Vector3.Dot(worldPoint - _camera.transform.position, _camera.transform.forward) <= 0f) return;

            Vector2 panelPos = RuntimePanelUtils.CameraTransformWorldToPanel(_damageLayer.panel, worldPoint, _camera);
            panelPos += new Vector2(Random.Range(-12f, 12f), Random.Range(-6f, 6f)); // que no se pisen

            if (_numbers.Count >= MaxDamageNumbers) RemoveNumber(0);

            Label label = _labelPool.Count > 0 ? _labelPool.Pop() : CreateNumberLabel();
            label.text = Mathf.CeilToInt(damage).ToString();
            label.EnableInClassList("kill", isKill);
            label.style.opacity = 1f;
            label.style.left = panelPos.x;
            label.style.top = panelPos.y;
            label.style.display = DisplayStyle.Flex;
            _damageLayer.Add(label);
            _numbers.Add(new DamageNumber { Label = label, Origin = panelPos, Age = 0f });
        }

        private Label CreateNumberLabel()
        {
            var label = new Label { pickingMode = PickingMode.Ignore };
            label.AddToClassList("damage-number");
            return label;
        }

        private void UpdateDamageNumbers()
        {
            for (int i = _numbers.Count - 1; i >= 0; i--)
            {
                var n = _numbers[i];
                n.Age += Time.deltaTime;
                if (n.Age >= DamageNumberLifetime)
                {
                    RemoveNumber(i);
                    continue;
                }

                float t = n.Age / DamageNumberLifetime;
                n.Label.style.top = n.Origin.y - DamageNumberRise * Mathf.Sqrt(t);
                n.Label.style.opacity = t < 0.6f ? 1f : 1f - (t - 0.6f) / 0.4f;
                _numbers[i] = n;
            }
        }

        private void RemoveNumber(int index)
        {
            var n = _numbers[index];
            _numbers.RemoveAt(index);
            n.Label.RemoveFromHierarchy();
            _labelPool.Push(n.Label);
        }
    }
}
