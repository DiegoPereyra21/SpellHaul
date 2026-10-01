using FishNet.Object;
using Game.Presentation.Abilities;
using Game.Presentation.Combat;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Presentation.UI
{
    /// <summary>
    /// HUD del jugador local: barra de vida + cooldowns. Puramente cliente/visual,
    /// lee el estado ya sincronizado (Health SyncVar, cooldowns locales del AbilityController).
    /// Va en el prefab Player, junto a un UIDocument.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class HUDController : NetworkBehaviour
    {
        private UIDocument _document;
        private Health _health;
        private Mana _mana;
        private PlayerExtractionState _extraction;
        private AbilityController _abilities;
        private PlayerStats _stats;
        private VisualElement _statModifiers;
        private VisualElement _healthFill;
        private VisualElement _manaFill;
        private Label _manaText;
        private Label _healthText;
        //para la extraccion
        private VisualElement _extractionContainer;
        private VisualElement _extractionFill;
        private Label _extractionLabel;
        private Label _runTimer;
        private Label _runDanger;
        private Label _runCounter;
        private PlayerInteraction _interaction;
        private Label _interactPrompt;
        private readonly VisualElement[] _cooldownOverlays = new VisualElement[Game.Core.Abilities.AbilitySlots.Count];

        private readonly Label[] _cooldownTexts = new Label[Game.Core.Abilities.AbilitySlots.Count];

        // Slot del guante (clic derecho): nombre de la habilidad o "No Glove", color por escuela.
        private VisualElement _gloveSlot;
        private VisualElement _gloveIcon;
        private Label _gloveName;
        private Game.Core.Items.GloveItemSO _shownGlove;
        private bool _gloveShownOnce;
        private VisualElement _dangerFrame;

        // Últimos valores mostrados: los textos solo se reescriben cuando cambia lo que muestran
        // (antes se armaban ~10 strings nuevos por frame, basura para el GC).
        private int _lastHp = int.MinValue, _lastHpMax = int.MinValue;
        private int _lastMana = int.MinValue, _lastManaMax = int.MinValue;
        private int _lastTimerSeconds = int.MinValue;
        private int _lastAlive = -1, _lastExtracted = -1, _lastDead = -1;
        private readonly int[] _lastCooldownKey = { int.MinValue, int.MinValue, int.MinValue }; // por AbilitySlots. MinValue = sin dibujar aún, -1 = oculto

        private VisualElement _dashRing;
        private float _dashRingProgress; // 0 = vacío (en cooldown), 1 = lleno (listo)

        [Header("Hitmarker")]
        [SerializeField] private AudioSource _hitMarkerAudio;
        [SerializeField] private AudioClip _hitMarkerClip;
        [SerializeField] private AudioClip _killMarkerClip;
        [SerializeField] private float _hitMarkerDuration = 0.15f;

        private VisualElement _hitMarker;
        private float _hitMarkerStartTime = -999f;
        private bool _hitMarkerIsKill;
        private bool _hitMarkerActive;


        [Header("Damage received")]
        [SerializeField] private AudioSource _damageAudio;
        [SerializeField] private AudioClip _damageClip;
        private const float DamageFlashDuration = 0.4f;
        private const float DamageDirectionDuration = 0.9f;
        private const float DamageFlashPeakWidth = 36f;
        private const float DamageFlashPopPhase = 0.06f;
        private const float DamageFlashRiseFraction = 0.15f;

        private VisualElement _damageFlash;
        private bool _damageFlashActive;
        private float _damageFlashStartTime = -999f;

        private VisualElement _damageIndicator;
        private bool _damageDirectionActive;
        private float _damageDirectionStartTime = -999f;
        private Vector3 _damageDirectionWorldPos;
        private Camera _cam;


        private void Awake()
        {
            _document = GetComponent<UIDocument>();
            _health = GetComponent<Health>();
            _mana = GetComponent<Mana>();
            _extraction = GetComponent<PlayerExtractionState>();
            _abilities = GetComponent<AbilityController>();
            _interaction = GetComponent<PlayerInteraction>();
            _stats = GetComponent<PlayerStats>();
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            ApplyOwnershipClient();
        }

        // Se reevalúa al cambiar de dueño: al reconectar, el cliente recibe su personaje primero
        // sin dueño (el servidor le devuelve el control un instante después) y OnStartClient ya
        // corrió como si fuera de otro jugador.
        public override void OnOwnershipClient(FishNet.Connection.NetworkConnection prevOwner)
        {
            base.OnOwnershipClient(prevOwner);
            ApplyOwnershipClient();
        }

        private bool _ownerSetupDone;

        private void ApplyOwnershipClient()
        {
            // Solo el dueño ve su propio HUD. Los demás Players no deben renderizar UI.
            if (!base.IsOwner)
            {
                _document.rootVisualElement.style.display = DisplayStyle.None;
                enabled = false;
                return;
            }

            _document.rootVisualElement.style.display = DisplayStyle.Flex;
            enabled = true;
            if (_ownerSetupDone) return;
            _ownerSetupDone = true;

            var root = _document.rootVisualElement;
            _cam = GetComponentInChildren<Camera>();
            _healthFill = root.Q<VisualElement>("health-bar-fill");
            _healthText = root.Q<Label>("health-text");
            _manaFill = root.Q<VisualElement>("mana-bar-fill");
            _manaText = root.Q<Label>("mana-text");
            _extractionContainer = root.Q<VisualElement>("extraction-container");
            _extractionFill = root.Q<VisualElement>("extraction-bar-fill");
            _extractionLabel = root.Q<Label>("extraction-label");
            _runTimer = root.Q<Label>("run-timer");
            _runDanger = root.Q<Label>("run-danger");
            _runCounter = root.Q<Label>("run-counter");
            _interactPrompt = root.Q<Label>("interact-prompt");
            _dashRing = root.Q<VisualElement>("dash-ring");
                        if (_dashRing != null)
                            _dashRing.generateVisualContent += DrawDashRing;

            _hitMarker = root.Q<VisualElement>("hitmarker");
            _statModifiers = root.Q<VisualElement>("stat-modifiers");
            if (_stats != null) _stats.OnStatsChanged += RefreshStatModifiers;
            RefreshStatModifiers();


            if (_hitMarker != null)
                _hitMarker.generateVisualContent += DrawHitMarker;

            if (_abilities != null)
                _abilities.OnHitConfirmed += PlayHitMarker;

            _damageFlash = root.Q<VisualElement>("damage-flash");
            _damageIndicator = root.Q<VisualElement>("damage-indicator");
            if (_damageIndicator != null)
                _damageIndicator.generateVisualContent += DrawDamageIndicator;

            if (_health != null)
                _health.OnDamagedWithDirection += PlayDamageIndicator;

            for (int i = 0; i < _cooldownOverlays.Length; i++)
                _cooldownOverlays[i] = root.Q<VisualElement>($"slot-{i}-cd");

            for (int i = 0; i < _cooldownTexts.Length; i++)
                _cooldownTexts[i] = root.Q<Label>($"slot-{i}-cd-text");

            _gloveSlot = root.Q<VisualElement>($"slot-{Game.Core.Abilities.AbilitySlots.Glove}");
            _gloveIcon = root.Q<VisualElement>("slot-glove-icon");
            _gloveName = root.Q<Label>("slot-glove-name");

            _dangerFrame = root.Q<VisualElement>("danger-frame");

            // Menú de pausa (Esc), solo para el dueño.
            if (root.Q<VisualElement>("pause-root") != null)
            {
                var pause = GetComponent<PauseMenuController>();
                if (pause == null) pause = gameObject.AddComponent<PauseMenuController>();
                pause.Init(root);
            }

            // Mira configurable, números de daño, FPS/ping y aviso de conexión inestable.
            var extras = GetComponent<HudExtras>();
            if (extras == null) extras = gameObject.AddComponent<HudExtras>();
            extras.Init(root, _abilities, _cam);

            // Cegado por el orbe de destello (lo calcula este cliente para su propia cámara).
            var flash = GetComponent<FlashOverlay>();
            if (flash == null) flash = gameObject.AddComponent<FlashOverlay>();
            flash.Init(root, _cam);
        }

        /// <summary>Owner. Actualiza el slot del clic derecho solo cuando cambia el guante equipado.</summary>
        private void RefreshGloveSlot()
        {
            if (_abilities == null || _gloveSlot == null) return;
            var glove = _abilities.CurrentGlove;
            if (_gloveShownOnce && glove == _shownGlove) return;
            _gloveShownOnce = true;
            _shownGlove = glove;

            bool hasAbility = glove != null && glove.Ability != null;
            _gloveSlot.EnableInClassList("ability-slot--empty", !hasAbility);
            if (_gloveName != null)
                _gloveName.text = hasAbility ? glove.Ability.DisplayName : "No Glove";

            if (_gloveIcon != null)
            {
                foreach (Game.Core.Items.GloveSchool school in System.Enum.GetValues(typeof(Game.Core.Items.GloveSchool)))
                    _gloveIcon.EnableInClassList(GloveVisuals.SchoolClass(school), hasAbility && glove.School == school);
            }
        }

        public override void OnStopClient()
        {
            base.OnStopClient();
            if (_abilities != null)
                _abilities.OnHitConfirmed -= PlayHitMarker;

            if (_stats != null)
                _stats.OnStatsChanged -= RefreshStatModifiers;

            if (_health != null)
                _health.OnDamagedWithDirection -= PlayDamageIndicator;
        }

        private void Update()
        {
            if (!base.IsOwner) return;

            // Vida
            if (_health != null && _healthFill != null)
            {
                int hp = Mathf.RoundToInt(_health.Current);
                int hpMax = Mathf.RoundToInt(_health.Max);
                if (hp != _lastHp || hpMax != _lastHpMax)
                {
                    _lastHp = hp; _lastHpMax = hpMax;
                    float pct = _health.Max > 0 ? _health.Current / _health.Max : 0f;
                    _healthFill.style.width = Length.Percent(pct * 100f);
                    _healthText.text = $"{hp} / {hpMax}";
                }
            }

            // Maná
            if (_mana != null && _manaFill != null)
            {
                int mana = Mathf.RoundToInt(_mana.Current);
                int manaMax = Mathf.RoundToInt(_mana.Max);
                if (mana != _lastMana || manaMax != _lastManaMax)
                {
                    _lastMana = mana; _lastManaMax = manaMax;
                    float pct = _mana.Max > 0 ? _mana.Current / _mana.Max : 0f;
                    _manaFill.style.width = Length.Percent(pct * 100f);
                    _manaText.text = $"{mana} / {manaMax}";
                }
            }
            
            RefreshGloveSlot();
            if (_gloveSlot != null && _abilities != null)
                _gloveSlot.EnableInClassList("ability-slot--recast", _abilities.IsRecastReady(Game.Core.Abilities.AbilitySlots.Glove));

            // Cooldowns
            if (_abilities != null)
            {
                for (int i = 0; i < _cooldownOverlays.Length; i++)
                {
                    if (_cooldownOverlays[i] == null) continue;
                    float cd = _abilities.GetCooldownProgress(i);
                    _cooldownOverlays[i].style.height = Length.Percent(cd * 100f);

                    if (_cooldownTexts[i] == null) continue;
                    if (cd > 0f)
                    {
                        float remaining = _abilities.GetCooldownRemainingSeconds(i);
                        // Clave de lo que se muestra: segundos enteros (≥1 s) o décimas (<1 s).
                        int key = remaining >= 1f ? 1000 + Mathf.RoundToInt(remaining) : Mathf.RoundToInt(remaining * 10f);
                        if (key != _lastCooldownKey[i])
                        {
                            if (_lastCooldownKey[i] < 0) _cooldownTexts[i].style.display = DisplayStyle.Flex;
                            _lastCooldownKey[i] = key;
                            _cooldownTexts[i].text = remaining >= 1f ? $"{remaining:0}" : $"{remaining:0.0}";
                        }
                    }
                    else if (_lastCooldownKey[i] != -1)
                    {
                        _lastCooldownKey[i] = -1;
                        _cooldownTexts[i].style.display = DisplayStyle.None;
                    }
                }
            }

            // Extracción
            if (_extraction != null && _extractionContainer != null)
            {
                float progress = _extraction.ExtractionProgress;
                bool channeling = progress > 0f && !_extraction.IsExtracted;

                if (_extraction.IsExtracted)
                {
                    _extractionContainer.style.display = DisplayStyle.Flex;
                    _extractionFill.style.width = Length.Percent(100f);
                    _extractionLabel.text = "Extracted";
                }
                else if (channeling)
                {
                    _extractionContainer.style.display = DisplayStyle.Flex;
                    _extractionFill.style.width = Length.Percent(progress * 100f);
                    _extractionLabel.text = "Extracting...";
                }
                else
                {
                    _extractionContainer.style.display = DisplayStyle.None;
                }
            }

            // Info de run
            var run = Game.Presentation.Run.RunManager.Instance;
            if (run != null && _runTimer != null)
            {
                int total = Mathf.CeilToInt(run.TimeRemaining);
                if (total != _lastTimerSeconds)
                {
                    _lastTimerSeconds = total;
                    _runTimer.text = $"{total / 60:00}:{total % 60:00}";
                }

                bool danger = run.Phase == Game.Core.Run.RunPhase.DangerPhase;
                _runDanger.style.display = danger ? DisplayStyle.Flex : DisplayStyle.None;
                if (_dangerFrame != null)
                {
                    if (danger) _dangerFrame.AddToClassList("active");
                    else _dangerFrame.RemoveFromClassList("active");
                }

                if (run.AliveCount != _lastAlive || run.ExtractedCount != _lastExtracted || run.DeadCount != _lastDead)
                {
                    _lastAlive = run.AliveCount; _lastExtracted = run.ExtractedCount; _lastDead = run.DeadCount;
                    _runCounter.text = $"Alive {_lastAlive}  ·  Extracted {_lastExtracted}  ·  Dead {_lastDead}";
                }
            }

            //interact prompt
            if (_interaction != null && _interactPrompt != null)
            {
                string prompt = _interaction.CurrentPrompt;
                if (!string.IsNullOrEmpty(prompt))
                {
                    _interactPrompt.style.display = DisplayStyle.Flex;
                    _interactPrompt.text = prompt;
                }
                else
                {
                    _interactPrompt.style.display = DisplayStyle.None;
                }
            }

            if (_dashRing != null && _abilities != null)
            {
                float newProgress = 1f - _abilities.GetCooldownProgress(Game.Core.Abilities.AbilitySlots.Mobility);
                if (!Mathf.Approximately(newProgress, _dashRingProgress))
                {
                    _dashRingProgress = newProgress;
                    _dashRing.MarkDirtyRepaint(); // redibujar el aro
                }
            }

            // Hitmarker: animación por tiempo, se repinta mientras esté activo.
            if (_hitMarkerActive && _hitMarker != null)
            {
                float t = (Time.unscaledTime - _hitMarkerStartTime) / _hitMarkerDuration;
                if (t >= 1f)
                    _hitMarkerActive = false;
                _hitMarker.MarkDirtyRepaint();
            }

            UpdateDamageFlash();

            if (_damageDirectionActive && _damageIndicator != null)
                _damageIndicator.MarkDirtyRepaint();
        }

        private void DrawDashRing(MeshGenerationContext ctx)
        {
            var painter = ctx.painter2D;
            float size = _dashRing.resolvedStyle.width;
            if (size <= 0f) return;

            Vector2 center = new Vector2(size / 2f, size / 2f);
            float radius = size / 2f - 3f;

            // Fondo del aro (tenue).
            painter.strokeColor = new Color(1f, 1f, 1f, 0.15f);
            painter.lineWidth = 3f;
            painter.BeginPath();
            painter.Arc(center, radius, 0f, 360f);
            painter.Stroke();

            // Progreso: se llena de vacío a completo. Empieza arriba (-90°) en sentido horario.
            if (_dashRingProgress > 0f)
            {
                painter.strokeColor = _dashRingProgress >= 1f
                    ? new Color(0.4f, 0.9f, 1f, 0.95f)   // listo: cian brillante
                    : new Color(0.4f, 0.9f, 1f, 0.5f);   // cargando: cian tenue
                painter.lineWidth = 3f;
                painter.BeginPath();
                painter.Arc(center, radius, -90f, -90f + 360f * _dashRingProgress);
                painter.Stroke();
            }
        }

        /// <summary>Llamado (owner-only) cuando el servidor confirma que un ataque propio conectó.</summary>
        private void PlayHitMarker(bool isKill)
        {
            if (isKill) RunSummary.AddKill();
            _hitMarkerStartTime = Time.unscaledTime;
            _hitMarkerIsKill = isKill;
            _hitMarkerActive = true;

            if (_hitMarkerAudio != null)
            {
                AudioClip clip = isKill ? _killMarkerClip : _hitMarkerClip;
                if (clip != null) _hitMarkerAudio.PlayOneShot(clip, Game.Presentation.Audio.AudioVolumes.Effects);
            }
        }


        /// <summary>Owner-only. Se dispara al recibir daño propio, con la posición mundial de quien lo
        /// causó (si se pudo resolver — ver Health.NotifyDamageDirection). Arranca el flash de pantalla
        /// y el indicador direccional juntos: son dos lecturas del mismo evento.</summary>
        private void PlayDamageIndicator(Vector3 instigatorWorldPosition)
        {
            _damageFlashActive = true;
            _damageFlashStartTime = Time.unscaledTime;

            _damageDirectionActive = true;
            _damageDirectionStartTime = Time.unscaledTime;
            _damageDirectionWorldPos = instigatorWorldPosition;

            if (_damageAudio != null && _damageClip != null)
                _damageAudio.PlayOneShot(_damageClip, Game.Presentation.Audio.AudioVolumes.Effects);
        }

/// <summary>Anima el borde full-screen: blanco caliente al impacto (instante), sangra a rojo
        /// saturado y decae. Deliberadamente distinto en color/técnica de danger-frame (coral tenue,
        /// estático) para que no se confundan superpuestos.</summary>
        private void UpdateDamageFlash()
        {
            if (_damageFlash == null) return;
            if (!_damageFlashActive) return;

            float t = (Time.unscaledTime - _damageFlashStartTime) / DamageFlashDuration;
            if (t >= 1f)
            {
                _damageFlashActive = false;
                SetFlashBorder(0f, Color.clear);
                return;
            }

            float width = t < DamageFlashRiseFraction
                ? Mathf.Lerp(0f, DamageFlashPeakWidth, t / DamageFlashRiseFraction)
                : Mathf.Lerp(DamageFlashPeakWidth, 0f, (t - DamageFlashRiseFraction) / (1f - DamageFlashRiseFraction));

            Color color;
            if (t < DamageFlashPopPhase)
            {
                float popT = t / DamageFlashPopPhase;
                color = Color.Lerp(new Color(1f, 1f, 1f, 0.9f), new Color(1f, 0.08f, 0.05f, 0.8f), popT);
            }
            else
            {
                float fadeT = (t - DamageFlashPopPhase) / (1f - DamageFlashPopPhase);
                color = new Color(1f, 0.08f, 0.05f, Mathf.Lerp(0.8f, 0f, fadeT));
            }

            SetFlashBorder(width, color);
        }

        private void SetFlashBorder(float width, Color color)
        {
            _damageFlash.style.borderLeftWidth = width;
            _damageFlash.style.borderRightWidth = width;
            _damageFlash.style.borderTopWidth = width;
            _damageFlash.style.borderBottomWidth = width;
            _damageFlash.style.borderLeftColor = color;
            _damageFlash.style.borderRightColor = color;
            _damageFlash.style.borderTopColor = color;
            _damageFlash.style.borderBottomColor = color;
        }

        /// <summary>Wedge que apunta hacia de dónde vino el golpe. Pop de escala al aparecer, se
        /// sostiene a pleno la primera mitad de su duración y recién ahí decae; contorno oscuro para
        /// que se lea encima de cualquier fondo (flash de daño / danger-frame incluidos).</summary>
        private void DrawDamageIndicator(MeshGenerationContext ctx)
        {
            if (!_damageDirectionActive) return;

            float t = Mathf.Clamp01((Time.unscaledTime - _damageDirectionStartTime) / DamageDirectionDuration);
            if (t >= 1f)
            {
                _damageDirectionActive = false;
                return;
            }

            float alpha = 1f - Mathf.Clamp01((t - 0.5f) / 0.5f);
            float pop = Mathf.Clamp01(t * 6f);
            float scale = Mathf.Lerp(1.4f, 1f, pop);

            if (_cam == null) return;

            Vector3 toInstigator = _damageDirectionWorldPos - _cam.transform.position;
            toInstigator.y = 0f;
            if (toInstigator.sqrMagnitude < 0.0001f) return;
            toInstigator.Normalize();

            Vector3 fwd = _cam.transform.forward; fwd.y = 0f; fwd.Normalize();
            Vector3 right = _cam.transform.right; right.y = 0f; right.Normalize();

            float bearing = Mathf.Atan2(Vector3.Dot(toInstigator, right), Vector3.Dot(toInstigator, fwd)) * Mathf.Rad2Deg;

            float w = _damageIndicator.resolvedStyle.width;
            float h = _damageIndicator.resolvedStyle.height;
            if (w <= 0f || h <= 0f) return;

            Vector2 center = new Vector2(w / 2f, h / 2f);
            float radius = Mathf.Min(w, h) * 0.38f;

            float rad = bearing * Mathf.Deg2Rad;
            Vector2 dir = new Vector2(Mathf.Sin(rad), -Mathf.Cos(rad));
            Vector2 tip = center + dir * radius;
            Vector2 perp = new Vector2(-dir.y, dir.x) * 16f * scale;
            Vector2 baseA = tip - dir * 30f * scale + perp;
            Vector2 baseB = tip - dir * 30f * scale - perp;

            var painter = ctx.painter2D;

            painter.strokeColor = new Color(0f, 0f, 0f, alpha * 0.8f);
            painter.lineWidth = 4f;
            painter.BeginPath();
            painter.MoveTo(tip);
            painter.LineTo(baseA);
            painter.LineTo(baseB);
            painter.ClosePath();
            painter.Stroke();

            painter.fillColor = new Color(1f, 0.25f, 0.15f, alpha);
            painter.BeginPath();
            painter.MoveTo(tip);
            painter.LineTo(baseA);
            painter.LineTo(baseB);
            painter.ClosePath();
            painter.Fill();
        }

        /// <summary>Redibuja la lista de bonus/penalizaciones activas del equipo. Solo corre cuando cambian (evento), no por frame.</summary>
        private void RefreshStatModifiers()
        {
            if (_statModifiers == null || _stats == null) return;

            _statModifiers.Clear();
            foreach (var (label, isPositive) in _stats.GetActiveModifierSummaries())
            {
                var entry = new Label(label);
                entry.AddToClassList("stat-modifier-entry");
                entry.AddToClassList(isPositive ? "stat-modifier-positive" : "stat-modifier-negative");
                _statModifiers.Add(entry);
            }
        }

        private static readonly float[] HitMarkerAngles = { 45f, 135f, 225f, 315f };

        // Cuatro ticks diagonales (estilo shooter competitivo) que hacen pop y se desvanecen.
        // Kill marker: mismo dibujo, más grueso y en rojo.
        private void DrawHitMarker(MeshGenerationContext ctx)
        {
            if (!_hitMarkerActive) return;

            float t = Mathf.Clamp01((Time.unscaledTime - _hitMarkerStartTime) / _hitMarkerDuration);
            float alpha = 1f - t;
            float pop = Mathf.Clamp01(t * 4f); // asienta rápido en el primer cuarto de la duración
            float scale = Mathf.Lerp(1.3f, 1f, pop);

            float size = _hitMarker.resolvedStyle.width;
            if (size <= 0f) return;
            Vector2 center = new Vector2(size / 2f, size / 2f);

            float r1 = 6f * scale;
            float r2 = 14f * scale;

            var painter = ctx.painter2D;
            painter.strokeColor = _hitMarkerIsKill
                ? new Color(1f, 0.2f, 0.15f, alpha)
                : new Color(1f, 1f, 1f, alpha);
            painter.lineWidth = _hitMarkerIsKill ? 3.5f : 2.5f;

            foreach (float deg in HitMarkerAngles)
            {
                float rad = deg * Mathf.Deg2Rad;
                Vector2 dir = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
                painter.BeginPath();
                painter.MoveTo(center + dir * r1);
                painter.LineTo(center + dir * r2);
                painter.Stroke();
            }
        }
    }
}