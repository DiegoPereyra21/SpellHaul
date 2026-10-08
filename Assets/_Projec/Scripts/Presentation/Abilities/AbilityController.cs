using System;
using FishNet.Object;
using Game.Core.Abilities;
using Game.Presentation.Bootstrap;
using Game.Presentation.Combat;
using UnityEngine;
using UnityEngine.InputSystem;
using VContainer;
using FishNet.Managing.Timing;
using Game.Presentation.Player;

namespace Game.Presentation.Abilities
{
    /// <summary>
    /// Lee input de casteo, valida cooldown localmente (feedback inmediato de UI/anim) y
    /// pide ejecución real al servidor vía ServerRpc. El servidor es quien valida cooldown
    /// "de verdad" y ejecuta el efecto — el cliente nunca es fuente de verdad de daño/loot.
    ///
    /// Slots (ver AbilitySlots): Primary (clic izq.) y Mobility (Shift) son fijos del personaje;
    /// Glove (clic der.) es la habilidad del guante equipado en RunInventory, con la potencia y
    /// el cooldown de su rareza. Sin guante, el clic derecho no hace nada. El cooldown es del
    /// slot, no del guante: cambiar de guante no lo reinicia.
    /// </summary>
    public class AbilityController : NetworkBehaviour
    {
        [Header("Habilidades fijas")]
        [Tooltip("Clic izquierdo: ataque básico, siempre disponible.")]
        [SerializeField] private AbilitySO _primaryAbility;
        [Tooltip("Shift: dash.")]
        [SerializeField] private AbilitySO _mobilityAbility;

        [Header("Puntería")]
        [SerializeField] private Transform _aimOrigin;
        [SerializeField] private Transform _spellOrigin; // punto de salida del hechizo (báculo, mano, etc.)
        [SerializeField] private LayerMask _aimMask;
        [SerializeField] private float _maxAimDistance = 50f;
        [SerializeField] private TrajectoryPreviewController _trajectoryPreview;
        [SerializeField] private ChargeVFXController _chargeVfx;

        // Cooldowns en ticks de red (TimeManager.Tick), no Time.time: evita el degrade de
        // precisión de un float acumulando desde el boot del proceso, y usa el mismo reloj
        // que ya gobierna prediction/lag-comp en vez de uno paralelo.
        private readonly uint[] _localCooldownEndTick = new uint[AbilitySlots.Count];
        private readonly uint[] _serverCooldownEndTick = new uint[AbilitySlots.Count];
        // Aim más reciente recibido durante un windup en curso (re-apuntado en tiempo real).
        private readonly Vector3[] _pendingAimDirection = new Vector3[AbilitySlots.Count];
        private readonly Vector3[] _pendingAimPoint = new Vector3[AbilitySlots.Count];
        private readonly bool[] _hasPendingAim = new bool[AbilitySlots.Count];

        // Carga sostenida (habilidades chargeable). El tiempo real lo mide el SERVIDOR:
        // el cliente solo avisa "empecé" y "solté"; nunca manda cuánto cargó.
        private readonly uint[] _serverChargeStartTick = new uint[AbilitySlots.Count];
        private readonly bool[] _serverCharging = new bool[AbilitySlots.Count];
        private readonly bool[] _localCharging = new bool[AbilitySlots.Count];
        // Reloj LOCAL, solo para el preview de trayectoria (aproximado, no autoritativo).
        private readonly uint[] _localChargeStartTick = new uint[AbilitySlots.Count];

        // Habilidad con la que empezó cada carga: si al soltar el slot tiene otra (cambió el
        // guante), la carga se descarta en vez de disparar la habilidad nueva.
        private readonly AbilitySO[] _serverChargeAbility = new AbilitySO[AbilitySlots.Count];
        private readonly AbilitySO[] _localChargeAbility = new AbilitySO[AbilitySlots.Count];

        // Owner. Hasta cuándo (Time.unscaledTime) lo lanzado por el slot se puede re-activar
        // (habilidades IsRecastable). 0 = nada activo.
        private readonly float[] _localRecastUntil = new float[AbilitySlots.Count];

        private Mana _mana;
        private RunInventory _inventory;
        private PlayerMovementController _movement;
        private PlayerAvatarState _avatar;
        [SerializeField] private Game.Presentation.Combat.PlayerStats _stats;
        private AbilityExecutor _executor;

        private InputAction[] _castActions;
        private PlayerControls _controls;

        private bool _inputBlocked;

        // VContainer no puede inyectar automáticamente en prefabs instanciados por el
        // NetworkManager de Fish-Net (spawn fuera del LifetimeScope normal). Por eso se
        // resuelve manualmente acá en vez de usar [Inject] en el spawn.
        [Inject]
        public void Construct(AbilityExecutor executor)
        {
            _executor = executor;
        }

        public override void OnStartNetwork()
        {
            // Este objeto fue instanciado por el NetworkManager de Fish-Net, por lo que
            // VContainer no lo inyectó automáticamente. Se resuelve manualmente acá.
            if (_executor == null)
            {
                var scope = FindFirstObjectByType<GameLifetimeScope>();
                scope?.InjectSpawnedObject(base.NetworkObject);
            }
        }

        /// <summary>Server-only. False si el jugador ya murió o extrajo: desactivar el componente no
        /// bloquea los ServerRpc, así que cada uno lo chequea explícitamente.</summary>
        private bool CanActServer => _avatar == null || !_avatar.IsControlDisabled;

        /// <summary>
        /// Corta todo cast en curso: windups pendientes (server y owner), cargas sostenidas y su
        /// telegrafía. Lo llama PlayerAvatarState al morir o extraer, en todas las instancias.
        /// </summary>
        public void CancelActiveCasts()
        {
            StopAllCoroutines();
            for (int i = 0; i < _castActions.Length; i++)
            {
                _localCharging[i] = false;
                _serverCharging[i] = false;
                _localChargeAbility[i] = null;
                _serverChargeAbility[i] = null;
                _hasPendingAim[i] = false;
                _localRecastUntil[i] = 0f;
            }
            _trajectoryPreview?.Hide();
            _chargeVfx?.EndCharge();
        }

        // ---------- Helpers de tiempo (ticks) ----------

        private bool IsOnCooldown(uint endTick) => base.TimeManager.Tick < endTick;

        // Techo de antigüedad del tick de disparo que manda el cliente para medir cooldowns. A
        // 60 Hz, 20 ≈ 333 ms (latencia + jitter). Más viejo que eso se toma como "ahora - techo".
        private const uint MaxFireTickAgeTicks = 20;

        /// <summary>
        /// Server-only. Tick de disparo del cliente acotado a [ahora - techo, ahora]. El cooldown se
        /// mide entre ticks de DISPARO, no de llegada del RPC: con la llegada, el jitter de la red
        /// juntaba dos casts seguidos y el servidor rechazaba el segundo aunque el cliente respetó
        /// el cooldown (el cliente ya había mostrado el proyectil: "pegaba" sin hacer daño). El
        /// ritmo sostenido sigue acotado: cada disparo tiene que estar un cooldown después del
        /// anterior y nunca en el futuro.
        /// </summary>
        private uint ClampFireTick(PreciseTick fireTick)
        {
            uint now = base.TimeManager.Tick;
            uint oldest = now > MaxFireTickAgeTicks ? now - MaxFireTickAgeTicks : 0u;
            uint tick = fireTick.Tick;
            if (tick < oldest) tick = oldest;
            if (tick > now) tick = now;
            return tick;
        }

        // ---------- Habilidades por slot ----------

        /// <summary>Guante equipado (null si no hay). Vale en servidor y en todos los clientes.</summary>
        public Game.Core.Items.GloveItemSO CurrentGlove => _inventory != null ? _inventory.EquippedGlove : null;

        /// <summary>Habilidad del slot ahora mismo. Glove: la del guante equipado (null sin guante).</summary>
        public AbilitySO GetAbility(int slot) => slot switch
        {
            AbilitySlots.Primary => _primaryAbility,
            AbilitySlots.Mobility => _mobilityAbility,
            AbilitySlots.Glove => CurrentGlove != null ? CurrentGlove.Ability : null,
            _ => null
        };

        /// <summary>Potencia que aporta el guante al slot Glove (rareza). 1 en los demás slots.</summary>
        private float AbilityPowerFor(int slot)
        {
            var glove = slot == AbilitySlots.Glove ? CurrentGlove : null;
            return glove != null ? glove.AbilityPower : 1f;
        }

        /// <summary>Cooldown efectivo en segundos: base de la habilidad x rareza del guante / velocidad de casteo.</summary>
        private float EffectiveCooldown(int slot, AbilitySO ability)
        {
            if (ability == null) return 0f;
            var glove = slot == AbilitySlots.Glove ? CurrentGlove : null;
            float gloveMul = glove != null ? glove.CooldownMultiplier : 1f;
            float castSpeed = _stats != null ? _stats.CastSpeedMultiplier : 1f;
            return ability.Cooldown * gloveMul / Mathf.Max(0.1f, castSpeed);
        }

        private uint CooldownTicks(int slot, AbilitySO ability) => base.TimeManager.TimeToTicks(EffectiveCooldown(slot, ability));

        // ---------- Maná predicho (cliente) ----------

        // Gastos locales que el SyncVar de maná todavía puede no reflejar (llega un RTT después).
        // Sin esto, spameando con poco maná el cliente veía maná de sobra, mostraba el proyectil y
        // el servidor rechazaba el cast.
        private readonly System.Collections.Generic.List<(float time, float cost)> _localManaSpends = new();

        private float PredictedLocalMana()
        {
            if (_mana == null) return float.MaxValue;
            float window = (float)(base.TimeManager.RoundTripTime / 1000.0) + 0.15f;
            float now = Time.unscaledTime;
            float pending = 0f;
            for (int i = _localManaSpends.Count - 1; i >= 0; i--)
            {
                if (now - _localManaSpends[i].time > window) _localManaSpends.RemoveAt(i);
                else pending += _localManaSpends[i].cost;
            }
            return _mana.Current - pending;
        }

        // "Fizzle" al intentar castear sin maná (o si el servidor rechaza el cast). No suena por
        // apretar en cooldown: spameando sería constante. Con un mínimo entre sonidos.
        private float _lastRejectSoundTime = -10f;

        private void PlayCastRejectedSound()
        {
            if (Time.unscaledTime - _lastRejectSoundTime < 0.4f) return;
            _lastRejectSoundTime = Time.unscaledTime;
            var lib = Game.Presentation.Audio.GameAudio.Library;
            if (lib != null) Game.Presentation.Audio.GameAudio.Play2D(lib.CastRejected, lib.UiVolume * 0.8f);
        }

        private void RecordLocalManaSpend(float cost)
        {
            if (cost > 0f) _localManaSpends.Add((Time.unscaledTime, cost));
        }

        /// <summary>Segundos restantes hasta endTick, en el reloj local de este lado (server o cliente). 0 si ya pasó.</summary>
        private float RemainingSeconds(uint endTick)
        {
            uint now = base.TimeManager.Tick;
            if (endTick <= now) return 0f;
            return (float)base.TimeManager.TicksToTime(endTick - now);
        }

        private uint TicksFromNow(float seconds) => base.TimeManager.Tick + base.TimeManager.TimeToTicks(seconds);

        private float GetCooldownRemainingNormalized(int slot)
        {
            if (slot < 0 || slot >= _localCooldownEndTick.Length) return 0f;

            float cooldown = EffectiveCooldown(slot, GetAbility(slot));
            if (cooldown <= 0f) return 0f;

            float remaining = RemainingSeconds(_localCooldownEndTick[slot]);
            if (remaining <= 0f) return 0f;

            return Mathf.Clamp01(remaining / cooldown);
        }

        /// <summary>Progreso de cooldown del slot (0 = listo, 1 = recién usado). Usa la predicción local del owner.</summary>
        public float GetCooldownProgress(int slot) => GetCooldownRemainingNormalized(slot);

        /// <summary>Segundos de cooldown que le quedan al slot (predicción local del owner).</summary>
        public float GetCooldownRemainingSeconds(int slot)
        {
            if (slot < 0 || slot >= _localCooldownEndTick.Length) return 0f;
            return RemainingSeconds(_localCooldownEndTick[slot]);
        }

        private void Awake()
        {
            _mana = GetComponent<Mana>();
            _movement = GetComponent<PlayerMovementController>();
            _avatar = GetComponent<PlayerAvatarState>();
            _stats = GetComponent<Game.Presentation.Combat.PlayerStats>();
            _inventory = GetComponent<RunInventory>();

            _controls = new PlayerControls();
            // Indexado por AbilitySlots: CastSlot0 = clic izq., CastSlot1 = Shift, CastSlot2 = clic der.
            _castActions = new InputAction[AbilitySlots.Count]
            {
                _controls.Player.CastSlot0,
                _controls.Player.CastSlot1,
                _controls.Player.CastSlot2,
            };
        }

        private void OnDestroy()
        {
            _controls?.Dispose();
        }

        private void OnEnable()
        {
            foreach (var action in _castActions) action.Enable();
        }

        private void OnDisable()
        {
            foreach (var action in _castActions) action.Disable();
        }

        private void Update()
        {
            if (!base.IsOwner) return;

            // Con el input bloqueado (inventario abierto), soltar cualquier carga en curso
            // para no dejar al servidor cargando indefinidamente.
            if (_inputBlocked)
            {
                for (int i = 0; i < _castActions.Length; i++)
                    if (_localCharging[i]) ReleaseCharge(i);
                return;
            }

            for (int i = 0; i < _castActions.Length; i++)
            {
                AbilitySO ability = GetAbility(i);
                // La habilidad del slot cambió (o desapareció) en medio de una carga: se descarta.
                if (_localCharging[i] && ability != _localChargeAbility[i]) AbortLocalCharge(i);
                if (ability == null) continue;

                if (ability.IsChargeable)
                {
                    if (_castActions[i].WasPressedThisFrame()) BeginCharge(i);
                    else if (_castActions[i].WasReleasedThisFrame() && _localCharging[i]) ReleaseCharge(i);
                }
                else if (_castActions[i].WasPressedThisFrame())
                {
                    TryCast(i);
                }
            }
        }

        /// <summary>
        /// Preview de trayectoria mientras se sostiene una carga. En LateUpdate: la cámara y el
        /// SpellOrigin (hijos de Graphics) ya tienen la posición suavizada de este frame; leerlos
        /// antes dibujaba la línea con un frame de atraso y "saltaba" al moverse.
        /// </summary>
        private void LateUpdate()
        {
            if (!base.IsOwner || _trajectoryPreview == null || _inputBlocked) return;

            for (int i = 0; i < _castActions.Length; i++)
            {
                AbilitySO ability = _localChargeAbility[i];
                if (ability == null || !_localCharging[i] || !ability.ShowTrajectoryPreview) continue;

                float held = (float)base.TimeManager.TicksToTime(base.TimeManager.Tick - _localChargeStartTick[i]);
                float t = ability.MaxChargeDuration > 0f
                    ? Mathf.Clamp01(held / ability.MaxChargeDuration)
                    : 1f;
                ability.GetLaunchForCharge(t, out float launchSpeed, out float gravity);
                ResolveAim(out _, out Vector3 aimPoint);
                Vector3 origin = _spellOrigin != null ? _spellOrigin.position : _aimOrigin.position;
                _trajectoryPreview.Show(origin, aimPoint, launchSpeed, gravity);
            }
        }

        // ---------- Casteo instantáneo / con windup ----------

        private void TryCast(int slot)
        {
            AbilitySO ability = GetAbility(slot);
            if (ability == null) return;
            RaiseAbilityUsed();

            // Lo lanzado sigue activo: este clic lo re-activa (ej. detonar el orbe en el aire).
            if (ability.IsRecastable && IsRecastReady(slot))
            {
                _localRecastUntil[slot] = 0f;
                RecastServerRpc(slot);
                return;
            }

            // Chequeos locales (feedback inmediato, no autoritativos).
            if (IsOnCooldown(_localCooldownEndTick[slot])) return;
            if (PredictedLocalMana() < ability.ResourceCost) { PlayCastRejectedSound(); return; }

            // Predicción local de cooldown y maná. El maná real lo descuenta y sincroniza el servidor.
            PredictCooldownLocally(slot, ability);
            RecordLocalManaSpend(ability.ResourceCost);
            if (ability.IsRecastable)
                _localRecastUntil[slot] = Time.unscaledTime + ability.WindupDuration + ability.RecastWindow;

            ResolveAim(out Vector3 aimDirection, out Vector3 aimPoint);

            // El dash no va por CastServerRpc: viaja como input predicho del movimiento, así
            // cliente y servidor lo aplican en el mismo tick (el servidor valida cooldown y maná ahí).
            if (_movement != null && ability.TryGetOwnerDash(aimDirection, out _, out _, out _))
            {
                _movement.QueueDashInput(slot, _movement.GetDashDirection());
                PlayLocalFireFeedback(ability, aimDirection, aimPoint);
                return;
            }

            // Tick de disparo (catch-up del proyectil) + ticks en que este cliente VEÍA a los demás
            // (lag compensation: el servidor rebobina las hitboxes a esos momentos).
            PreciseTick fireTick = base.TimeManager.GetPreciseTick(TickType.Tick);
            GetViewTicks(out uint playerViewTick, out uint aiViewTick);
            CastServerRpc(slot, aimDirection, aimPoint, fireTick, playerViewTick, aiViewTick);

            if (base.IsOwner)
            {
                if (ability.WindupDuration > 0f)
                {
                    _chargeVfx?.BeginCharge(ability.WindupDuration); // telegrafía local instantánea
                    StartCoroutine(PlayLocalFireFeedbackDelayed(ability, ability.WindupDuration));
                    StartCoroutine(SendAimUpdatesDuringWindup(slot, ability.WindupDuration));
                }
                else
                {
                    PlayLocalFireFeedback(ability, aimDirection, aimPoint);
                }
            }
        }

        // ---------- Carga sostenida (mantener presionado) ----------

        private void BeginCharge(int slot)
        {
            AbilitySO ability = GetAbility(slot);
            if (ability == null) return;
            RaiseAbilityUsed();

            // Chequeos locales (feedback inmediato, no autoritativos). El cooldown de esta
            // habilidad recién se predice al SOLTAR (arranca cuando se dispara, no al cargar).
            if (IsOnCooldown(_localCooldownEndTick[slot])) return;
            if (PredictedLocalMana() < ability.ResourceCost) { PlayCastRejectedSound(); return; }

            RecordLocalManaSpend(ability.ResourceCost);
            _localCharging[slot] = true;
            _localChargeAbility[slot] = ability;
            _localChargeStartTick[slot] = base.TimeManager.Tick;
            _chargeVfx?.BeginCharge(ability.MaxChargeDuration); // telegrafía local instantánea
            BeginChargeServerRpc(slot);
        }

        private void ReleaseCharge(int slot)
        {
            if (!_localCharging[slot]) return;
            _localCharging[slot] = false;
            _trajectoryPreview?.Hide();
            _chargeVfx?.EndCharge();

            AbilitySO ability = _localChargeAbility[slot];
            _localChargeAbility[slot] = null;
            if (ability == null) return;

            // Recién ahora arranca el cooldown local predicho (coincide con el servidor, que
            // también lo arranca al soltar).
            PredictCooldownLocally(slot, ability);

            ResolveAim(out Vector3 aimDirection, out Vector3 aimPoint);
            PreciseTick fireTick = base.TimeManager.GetPreciseTick(TickType.Tick);
            GetViewTicks(out uint playerViewTick, out uint aiViewTick);
            ReleaseChargeServerRpc(slot, aimDirection, aimPoint, fireTick, playerViewTick, aiViewTick);

            if (base.IsOwner)
                PlayLocalFireFeedback(ability, aimDirection, aimPoint);
        }

        /// <summary>Owner. Corta una carga local sin disparar (la habilidad del slot cambió) y avisa al servidor.</summary>
        private void AbortLocalCharge(int slot)
        {
            _localCharging[slot] = false;
            _localChargeAbility[slot] = null;
            _trajectoryPreview?.Hide();
            _chargeVfx?.EndCharge();
            AbortChargeServerRpc(slot);
        }

        [ServerRpc]
        private void AbortChargeServerRpc(int slot)
        {
            if (slot < 0 || slot >= AbilitySlots.Count || !_serverCharging[slot]) return;
            _serverCharging[slot] = false;
            _serverChargeAbility[slot] = null;
            StopChargeVfxObserversRpc();
        }

        [ServerRpc]
        private void BeginChargeServerRpc(int slot)
        {
            if (!CanActServer) return;
            if (slot < 0 || slot >= AbilitySlots.Count) return;
            AbilitySO ability = GetAbility(slot);
            if (ability == null || !ability.IsChargeable) return;
            if (_serverCharging[slot]) return; // ya estaba cargando

            // Validar cooldown autoritativo (de un cast anterior).
            if (IsOnCooldown(_serverCooldownEndTick[slot]))
            {
                RejectCastTargetRpc(base.Owner, slot, RemainingSeconds(_serverCooldownEndTick[slot]));
                return;
            }

            // Validar y descontar maná autoritativo (se cobra al EMPEZAR a cargar).
            if (_mana != null && !_mana.TrySpend(ability.ResourceCost))
            {
                RejectCastTargetRpc(base.Owner, slot, RemainingSeconds(_serverCooldownEndTick[slot]));
                return;
            }

            // El cooldown NO arranca acá: arranca al soltar (ver ReleaseChargeServerRpc),
            // para que cargar más tiempo no "regale" cooldown gratis.
            _serverCharging[slot] = true;
            _serverChargeAbility[slot] = ability;
            RaiseAbilityUsed();
            _serverChargeStartTick[slot] = base.TimeManager.Tick; // reloj del SERVIDOR: el cliente no decide la carga

            PlayChargeVfxObserversRpc(ability.MaxChargeDuration); // telegrafía para los demás
        }

        [ServerRpc]
        private void ReleaseChargeServerRpc(int slot, Vector3 aimDirection, Vector3 aimPoint, PreciseTick fireTick, uint playerViewTick, uint aiViewTick)
        {
            if (!CanActServer) return;
            if (slot < 0 || slot >= AbilitySlots.Count) return;
            if (!_serverCharging[slot]) return; // soltó sin haber empezado (o el begin fue rechazado)

            AbilitySO ability = _serverChargeAbility[slot];
            _serverCharging[slot] = false;
            _serverChargeAbility[slot] = null;
            StopChargeVfxObserversRpc(); // cortar la telegrafía para los demás, coincidiendo con el disparo
            // Si el guante cambió durante la carga, la carga se pierde (el maná ya se cobró).
            if (ability == null || ability != GetAbility(slot)) return;

            // Cooldown arranca al disparar (tick de disparo del cliente), no cuando empezó a cargar.
            _serverCooldownEndTick[slot] = ClampFireTick(fireTick) + CooldownTicks(slot, ability);

            // Carga medida contra el reloj del servidor y acotada a [0..1].
            float held = (float)base.TimeManager.TicksToTime(base.TimeManager.Tick - _serverChargeStartTick[slot]);
            float maxCharge = Mathf.Max(0.01f, ability.MaxChargeDuration);
            float charge = Mathf.Clamp01(held / maxCharge);

            ExecuteCast(ability, slot, aimDirection, aimPoint, fireTick, playerViewTick, aiViewTick, charge);
        }

        // ---------- Helpers de cliente ----------

        private void PredictCooldownLocally(int slot, AbilitySO ability)
        {
            _localCooldownEndTick[slot] = TicksFromNow(EffectiveCooldown(slot, ability));
        }

        private System.Collections.IEnumerator PlayLocalFireFeedbackDelayed(AbilitySO ability, float delay)
        {
            yield return new WaitForSeconds(delay);
            _chargeVfx?.EndCharge();
            ResolveAim(out Vector3 aimDirection, out Vector3 aimPoint); // apunta al instante real de disparar
            PlayLocalFireFeedback(ability, aimDirection, aimPoint);
        }

        /// <summary>
        /// Mientras dura el windup, el cliente le manda al servidor su aim actualizado una vez por
        /// tick (no por frame: a 300 fps eran 300 RPC por segundo). No valida nada, solo datos de
        /// puntería — el servidor sigue siendo quien decide CUÁNDO se ejecuta, esto define HACIA DÓNDE.
        /// </summary>
        private System.Collections.IEnumerator SendAimUpdatesDuringWindup(int slot, float duration)
        {
            float elapsed = 0f;
            uint lastSentTick = 0;
            while (elapsed < duration)
            {
                uint tick = base.TimeManager.Tick;
                if (tick != lastSentTick)
                {
                    lastSentTick = tick;
                    ResolveAim(out Vector3 aimDirection, out Vector3 aimPoint);
                    UpdateAimServerRpc(slot, aimDirection, aimPoint);
                }
                yield return null;
                elapsed += Time.deltaTime;
            }
        }

        [ServerRpc]
        private void UpdateAimServerRpc(int slot, Vector3 aimDirection, Vector3 aimPoint)
        {
            if (!CanActServer) return;
            if (slot < 0 || slot >= _pendingAimDirection.Length) return;
            _pendingAimDirection[slot] = aimDirection;
            _pendingAimPoint[slot] = aimPoint;
            _hasPendingAim[slot] = true;
        }

        private void PlayLocalFireFeedback(AbilitySO ability, Vector3 aimDirection, Vector3 aimPoint)
        {
            Vector3 muzzlePos = _spellOrigin != null ? _spellOrigin.position : _aimOrigin.position;

            if (ability.MuzzlePrefab != null)
                VFXManager.PlayProjectileMuzzle(muzzlePos, Quaternion.LookRotation(aimDirection));

            if (ability.CastClip != null)
                VFXManager.PlaySfx(ability.CastClip, muzzlePos);

            if (ability.TryGetCosmeticProjectile(out GameObject cosmeticPrefab, out float cosmeticSpeed))
            {
                Vector3 toAim = aimPoint - muzzlePos;
                Vector3 dir = toAim.sqrMagnitude > 0.0001f ? toAim.normalized : aimDirection;
                CosmeticProjectileManager.Spawn(cosmeticPrefab, muzzlePos, dir, cosmeticSpeed, transform);
            }
        }

        // NetworkTransform de la IA: interpolación 2 + 1 tick de envío.
        private const uint AiViewDelayTicks = 3;

        /// <summary>
        /// Owner. Ticks del SERVIDOR en que este cliente está viendo a los otros jugadores (los
        /// remotos se dibujan atrasados, ver PlayerMovementController.RemoteSnap) y a la IA
        /// (NetworkTransform interpolado). 0 = sin dato (el servidor usa el tick de disparo).
        /// </summary>
        private void GetViewTicks(out uint playerViewTick, out uint aiViewTick)
        {
            uint serverNow = base.TimeManager.LastPacketTick.Value(base.TimeManager);
            aiViewTick = serverNow > AiViewDelayTicks ? serverNow - AiViewDelayTicks : 0u;
            playerViewTick = PlayerMovementController.TryGetRemoteViewTick(out uint remoteTick) ? remoteTick : aiViewTick;
        }

        private void ResolveAim(out Vector3 aimDirection, out Vector3 aimPoint)
        {
            Vector3 cameraOrigin = _aimOrigin.position;
            Vector3 cameraForward = _aimOrigin.forward;

            aimPoint = cameraOrigin + cameraForward * _maxAimDistance;
            if (Physics.Raycast(cameraOrigin, cameraForward, out RaycastHit hit, _maxAimDistance, _aimMask))
                aimPoint = hit.point;

            aimDirection = cameraForward;
        }

        [ServerRpc]
        private void CastServerRpc(int slot, Vector3 aimDirection, Vector3 aimPoint, PreciseTick fireTick, uint playerViewTick, uint aiViewTick)
        {
            if (!CanActServer) return;
            if (slot < 0 || slot >= AbilitySlots.Count) return;
            AbilitySO ability = GetAbility(slot);
            if (ability == null) return; // sin guante: el clic derecho no hace nada
            if (ability.TryGetOwnerDash(Vector3.forward, out _, out _, out _)) return; // el dash va por el input de movimiento

            uint fire = ClampFireTick(fireTick);
            if (fire < _serverCooldownEndTick[slot])
            {
                RejectCastTargetRpc(base.Owner, slot, RemainingSeconds(_serverCooldownEndTick[slot]));
                return;
            }

            if (_mana != null && !_mana.TrySpend(ability.ResourceCost))
            {
                RejectCastTargetRpc(base.Owner, slot, RemainingSeconds(_serverCooldownEndTick[slot]));
                return;
            }

            _serverCooldownEndTick[slot] = fire + CooldownTicks(slot, ability);
            RaiseAbilityUsed();

            if (ability.WindupDuration > 0f)
            {
                PlayChargeVfxObserversRpc(ability.WindupDuration); // telegrafía para los demás
                StartCoroutine(ExecuteAfterWindup(ability, slot, aimDirection, aimPoint, fireTick, playerViewTick, aiViewTick, ability.WindupDuration));
            }
            else
            {
                ExecuteCast(ability, slot, aimDirection, aimPoint, fireTick, playerViewTick, aiViewTick);
            }
        }

        [Server]
        private System.Collections.IEnumerator ExecuteAfterWindup(AbilitySO ability, int slot, Vector3 aimDirection, Vector3 aimPoint, PreciseTick fireTick, uint playerViewTick, uint aiViewTick, float delay)
        {
            _hasPendingAim[slot] = false;
            yield return new WaitForSeconds(delay);

            if (_hasPendingAim[slot])
            {
                aimDirection = _pendingAimDirection[slot];
                aimPoint = _pendingAimPoint[slot];
            }

            StopChargeVfxObserversRpc(); // cortar la telegrafía para los demás, coincidiendo con el disparo
            // Si el guante cambió durante el windup, no se ejecuta la habilidad de otro guante.
            if (ability != GetAbility(slot)) yield break;
            ExecuteCast(ability, slot, aimDirection, aimPoint, fireTick, playerViewTick, aiViewTick);
        }

        [Server]
        private void ExecuteCast(AbilitySO ability, int slot, Vector3 aimDirection, Vector3 aimPoint, PreciseTick fireTick, uint playerViewTick, uint aiViewTick, float charge = 0f)
        {
            if (!CanActServer) return; // red de seguridad: murió/extrajo durante un windup
            float dmgMul = _stats != null ? _stats.DamageMultiplier : 1f;

            Vector3 head = _aimOrigin != null ? _aimOrigin.position : transform.position;
            Vector3 origin = _spellOrigin != null ? _spellOrigin.position : head;

            Vector3 toAim = aimPoint - head;
            if (toAim.magnitude > _maxAimDistance)
                aimPoint = head + toAim.normalized * _maxAimDistance;

            double tickDelta = base.TimeManager.TickDelta;
            uint windupTicks = tickDelta > 0 ? (uint)Mathf.RoundToInt(ability.WindupDuration / (float)tickDelta) : 0;
            uint adjustedFireTick = fireTick.Tick + windupTicks;
            // Con windup el disparo sale más tarde: lo que el tirador ve también avanza esos ticks.
            // (El Projectile acota estos valores contra el reloj del servidor: el cliente no decide cuánto se rebobina.)
            uint adjustedPlayerViewTick = playerViewTick != 0 ? playerViewTick + windupTicks : 0u;
            uint adjustedAiViewTick = aiViewTick != 0 ? aiViewTick + windupTicks : 0u;

            var context = new AbilityCastContext(
                casterNetworkId: base.ObjectId,
                origin: origin,
                aimDirection: aimDirection.normalized,
                aimPoint: aimPoint,
                tick: adjustedFireTick,
                damageMultiplier: dmgMul,
                slot: slot,
                chargeNormalized: charge,
                abilityPower: AbilityPowerFor(slot),
                playerViewTick: adjustedPlayerViewTick,
                aiViewTick: adjustedAiViewTick
            );

            ability.Execute(_executor, in context);

            if (ability.MuzzlePrefab != null)
                PlayMuzzleObserversRpc(origin, aimDirection);
            if (ability.CastClip != null)
                PlayCastSfxObserversRpc(origin, slot);
        }

        // Margen para el cooldown del dash en el servidor: el input llega con la latencia de ese
        // momento, que varía entre dashes. Sin margen, dashear justo al terminar el cooldown local
        // a veces se rechazaba y el jugador volvía atrás.
        private const uint DashCooldownToleranceTicks = 3;

        /// <summary>Datos del dash de un slot (velocidad/duración, del SO). Lo usan los clientes para predecir.</summary>
        public bool TryGetDashParams(int slot, out float speed, out float duration)
        {
            speed = 0f; duration = 0f;
            AbilitySO ability = GetAbility(slot);
            return ability != null && ability.TryGetOwnerDash(Vector3.forward, out _, out speed, out duration);
        }

        /// <summary>
        /// Server-only. Valida y cobra un dash pedido por input (PlayerMovementController): estado del
        /// jugador, cooldown y maná, igual que CastServerRpc. Devuelve velocidad y duración del SO
        /// (nunca las del cliente). Si se rechaza, corrige la predicción de cooldown del cliente.
        /// </summary>
        public bool ServerTryConsumeDash(int slot, Vector3 direction, out float speed, out float duration)
        {
            speed = 0f; duration = 0f;
            if (!CanActServer) return false;
            if (!TryGetDashParams(slot, out speed, out duration)) return false;

            AbilitySO ability = GetAbility(slot);
            uint endTick = _serverCooldownEndTick[slot];
            bool onCooldown = endTick > DashCooldownToleranceTicks && base.TimeManager.Tick + DashCooldownToleranceTicks < endTick;
            if (onCooldown || (_mana != null && !_mana.TrySpend(ability.ResourceCost)))
            {
                RejectCastTargetRpc(base.Owner, slot, RemainingSeconds(endTick));
                return false;
            }

            _serverCooldownEndTick[slot] = TicksFromNow(EffectiveCooldown(slot, ability));
            RaiseAbilityUsed();

            Vector3 origin = _spellOrigin != null ? _spellOrigin.position : transform.position;
            if (ability.MuzzlePrefab != null) PlayMuzzleObserversRpc(origin, direction);
            if (ability.CastClip != null) PlayCastSfxObserversRpc(origin, slot);
            return true;
        }

        // ---------- Re-activación (IsRecastable) ----------

        /// <summary>Owner. True si lo lanzado por el slot sigue activo y el próximo clic lo re-activa.</summary>
        public bool IsRecastReady(int slot)
            => slot >= 0 && slot < _localRecastUntil.Length && Time.unscaledTime < _localRecastUntil[slot];

        [ServerRpc]
        private void RecastServerRpc(int slot)
        {
            if (!CanActServer) return;
            if (slot < 0 || slot >= AbilitySlots.Count) return;
            RaiseAbilityUsed();
            RecastRegistry.TryRecast(base.ObjectId, slot); // si ya detonó solo, no pasa nada
        }

        /// <summary>Server. Lo lanzado por el slot ya no está (detonó/chocó): el dueño deja de ofrecer la re-activación.</summary>
        public void NotifyRecastEnded(int slot)
        {
            if (base.Owner.IsActive) RecastEndedTargetRpc(base.Owner, slot);
        }

        [TargetRpc]
        private void RecastEndedTargetRpc(FishNet.Connection.NetworkConnection conn, int slot)
        {
            if (slot >= 0 && slot < _localRecastUntil.Length) _localRecastUntil[slot] = 0f;
        }

        [TargetRpc]
        private void RejectCastTargetRpc(FishNet.Connection.NetworkConnection conn, int slot, float cooldownRemaining)
        {
            PlayCastRejectedSound();
            _localRecastUntil[slot] = 0f; // el cast no salió: no hay nada que re-activar
            // Llega el tiempo RESTANTE, no un tick absoluto del servidor: el tick del servidor no
            // significa nada en el reloj local del cliente. Se convierte a ticks LOCALES acá.
            _localCooldownEndTick[slot] = cooldownRemaining > 0f ? TicksFromNow(cooldownRemaining) : 0u;
            if (_localCharging[slot])
            {
                _localCharging[slot] = false;
                _localChargeAbility[slot] = null;
                _trajectoryPreview?.Hide();
                _chargeVfx?.EndCharge();
            }
        }


        public void NotifyProjectileImpact(Vector3 point, Vector3 normal, bool hitConfirmed, bool isKill, float damageDealt = 0f)
        {
            PlayImpactObserversRpc(point, normal);

            if (hitConfirmed && base.Owner.IsActive) // el tirador pudo haberse desconectado
                PlayHitMarkerTargetRpc(base.Owner, isKill, damageDealt, point);
        }

        public event Action<bool> OnHitConfirmed;

        /// <summary>Client-only (dueño). Daño confirmado por el servidor y dónde (números de daño).</summary>
        public event Action<Vector3, float, bool> OnDamageDealt;

        [TargetRpc]
        private void PlayHitMarkerTargetRpc(FishNet.Connection.NetworkConnection conn, bool isKill, float damageDealt, Vector3 point)
        {
            OnHitConfirmed?.Invoke(isKill);
            if (damageDealt > 0f) OnDamageDealt?.Invoke(point, damageDealt, isKill);
        }

        [ObserversRpc(ExcludeOwner = true)]
        private void PlayImpactObserversRpc(Vector3 point, Vector3 normal)
        {
            VFXManager.PlayProjectileHit(point, Quaternion.LookRotation(normal));
        }

        [ObserversRpc(ExcludeOwner = true)]
        private void PlayMuzzleObserversRpc(Vector3 point, Vector3 direction)
        {
            VFXManager.PlayProjectileMuzzle(point, Quaternion.LookRotation(direction));
        }

        [ObserversRpc(ExcludeOwner = true)]
        private void PlayCastSfxObserversRpc(Vector3 point, int slot)
        {
            AbilitySO ability = GetAbility(slot);
            if (ability != null && ability.CastClip != null)
                VFXManager.PlaySfx(ability.CastClip, point);
        }

        [ObserversRpc(ExcludeOwner = true)]
        private void PlayChargeVfxObserversRpc(float maxDuration)
        {
            _chargeVfx?.BeginCharge(maxDuration);
        }

        [ObserversRpc(ExcludeOwner = true)]
        private void StopChargeVfxObserversRpc()
        {
            _chargeVfx?.EndCharge();
        }

        public void NotifyAbilityImpactSfx(Vector3 point, int slot, bool wallHit = false)
        {
            PlayImpactSfxObserversRpc(point, slot, wallHit);
        }

        [ObserversRpc]
        private void PlayImpactSfxObserversRpc(Vector3 point, int slot, bool wallHit)
        {
            AbilitySO ability = GetAbility(slot);
            if (ability == null) return;

            AudioClip clip = wallHit ? ability.SurfaceImpactClip : ability.ImpactClip;
            if (clip != null)
                VFXManager.PlaySfx(clip, point);
        }


        public void SetInputBlocked(bool blocked) => _inputBlocked = blocked;

        /// <summary>True con el inventario o la pausa abiertos (UsableController tampoco lee input).</summary>
        public bool IsInputBlocked => _inputBlocked;

        /// <summary>Se dispara (en el dueño y en el servidor) al castear algo: el uso de un consumible
        /// en curso se cancela.</summary>
        public event Action OnAbilityUsed;
        private void RaiseAbilityUsed() => OnAbilityUsed?.Invoke();

        public int AbilitySlotCount => AbilitySlots.Count;

        public float GetCooldownNormalized(int slot) => GetCooldownRemainingNormalized(slot);
    }
}