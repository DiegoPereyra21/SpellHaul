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
    /// </summary>
    public class AbilityController : NetworkBehaviour
    {
        [SerializeField] private AbilitySO[] _equippedAbilities = new AbilitySO[5];
        [SerializeField] private Transform _aimOrigin;
        [SerializeField] private Transform _spellOrigin; // punto de salida del hechizo (báculo, mano, etc.)
        [SerializeField] private LayerMask _aimMask;
        [SerializeField] private float _maxAimDistance = 50f;
        [SerializeField] private TrajectoryPreviewController _trajectoryPreview;
        [SerializeField] private ChargeVFXController _chargeVfx;

        // Cooldowns en ticks de red (TimeManager.Tick), no Time.time: evita el degrade de
        // precisión de un float acumulando desde el boot del proceso, y usa el mismo reloj
        // que ya gobierna prediction/lag-comp en vez de uno paralelo.
        private readonly uint[] _localCooldownEndTick = new uint[5];
        private readonly uint[] _serverCooldownEndTick = new uint[5];
        // Aim más reciente recibido durante un windup en curso (re-apuntado en tiempo real).
        private readonly Vector3[] _pendingAimDirection = new Vector3[5];
        private readonly Vector3[] _pendingAimPoint = new Vector3[5];
        private readonly bool[] _hasPendingAim = new bool[5];

        // Carga sostenida (habilidades chargeable). El tiempo real lo mide el SERVIDOR:
        // el cliente solo avisa "empecé" y "solté"; nunca manda cuánto cargó.
        private readonly uint[] _serverChargeStartTick = new uint[5];
        private readonly bool[] _serverCharging = new bool[5];
        private readonly bool[] _localCharging = new bool[5];
        // Reloj LOCAL, solo para el preview de trayectoria (aproximado, no autoritativo).
        private readonly uint[] _localChargeStartTick = new uint[5];

        private Mana _mana;
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
                _hasPendingAim[i] = false;
            }
            _trajectoryPreview?.Hide();
            _chargeVfx?.EndCharge();
        }

        // ---------- Helpers de tiempo (ticks) ----------

        private bool IsOnCooldown(uint endTick) => base.TimeManager.Tick < endTick;

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

            AbilitySO ability = _equippedAbilities[slot];
            if (ability == null || ability.Cooldown <= 0f) return 0f;

            float remaining = RemainingSeconds(_localCooldownEndTick[slot]);
            if (remaining <= 0f) return 0f;

            return Mathf.Clamp01(remaining / ability.Cooldown);
        }

        /// <summary>Progreso de cooldown del slot (0 = listo, 1 = recién usado). Usa la predicción local del owner.</summary>
        public float GetCooldownProgress(int slot) => GetCooldownRemainingNormalized(slot);

        private void Awake()
        {
            _mana = GetComponent<Mana>();
            _movement = GetComponent<PlayerMovementController>();
            _avatar = GetComponent<PlayerAvatarState>();
            _stats = GetComponent<Game.Presentation.Combat.PlayerStats>();

            _controls = new PlayerControls();
            _castActions = new InputAction[5]
            {
                _controls.Player.CastSlot0,
                _controls.Player.CastSlot1,
                _controls.Player.CastSlot2,
                _controls.Player.CastSlot3,
                _controls.Player.CastSlot4,
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
                AbilitySO ability = _equippedAbilities[i];
                if (ability == null) continue;

                if (ability.IsChargeable)
                {
                    if (_castActions[i].WasPressedThisFrame()) BeginCharge(i);
                    else if (_castActions[i].WasReleasedThisFrame() && _localCharging[i]) ReleaseCharge(i);

                    // Preview de trayectoria: se actualiza cada frame mientras se sostiene.
                    if (_localCharging[i] && ability.ShowTrajectoryPreview && _trajectoryPreview != null)
                    {
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
                else if (_castActions[i].WasPressedThisFrame())
                {
                    TryCast(i);
                }
            }
        }

        // ---------- Casteo instantáneo / con windup ----------

        private void TryCast(int slot)
        {
            AbilitySO ability = _equippedAbilities[slot];
            if (ability == null) return;

            // Chequeos locales (feedback inmediato, no autoritativos).
            if (IsOnCooldown(_localCooldownEndTick[slot])) return;
            if (_mana != null && _mana.Current < ability.ResourceCost) return;

            // Predicción local de cooldown solamente. El maná lo descuenta y sincroniza el servidor.
            PredictCooldownLocally(slot, ability);

            ResolveAim(out Vector3 aimDirection, out Vector3 aimPoint);

            // Tick de disparo del cliente para lag compensation (el server rebobina a este tick).
            PreciseTick fireTick = base.TimeManager.GetPreciseTick(TickType.Tick);
            CastServerRpc(slot, aimDirection, aimPoint, fireTick);

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
            AbilitySO ability = _equippedAbilities[slot];
            if (ability == null) return;

            // Chequeos locales (feedback inmediato, no autoritativos). El cooldown de esta
            // habilidad recién se predice al SOLTAR (arranca cuando se dispara, no al cargar).
            if (IsOnCooldown(_localCooldownEndTick[slot])) return;
            if (_mana != null && _mana.Current < ability.ResourceCost) return;

            _localCharging[slot] = true;
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

            AbilitySO ability = _equippedAbilities[slot];
            if (ability == null) return;

            // Recién ahora arranca el cooldown local predicho (coincide con el servidor, que
            // también lo arranca al soltar).
            PredictCooldownLocally(slot, ability);

            ResolveAim(out Vector3 aimDirection, out Vector3 aimPoint);
            PreciseTick fireTick = base.TimeManager.GetPreciseTick(TickType.Tick);
            ReleaseChargeServerRpc(slot, aimDirection, aimPoint, fireTick);

            if (base.IsOwner)
                PlayLocalFireFeedback(ability, aimDirection, aimPoint);
        }

        [ServerRpc]
        private void BeginChargeServerRpc(int slot)
        {
            if (!CanActServer) return;
            if (slot < 0 || slot >= _equippedAbilities.Length) return;
            AbilitySO ability = _equippedAbilities[slot];
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
            _serverChargeStartTick[slot] = base.TimeManager.Tick; // reloj del SERVIDOR: el cliente no decide la carga

            PlayChargeVfxObserversRpc(ability.MaxChargeDuration); // telegrafía para los demás
        }

        [ServerRpc]
        private void ReleaseChargeServerRpc(int slot, Vector3 aimDirection, Vector3 aimPoint, PreciseTick fireTick)
        {
            if (!CanActServer) return;
            if (slot < 0 || slot >= _equippedAbilities.Length) return;
            if (!_serverCharging[slot]) return; // soltó sin haber empezado (o el begin fue rechazado)

            AbilitySO ability = _equippedAbilities[slot];
            _serverCharging[slot] = false;
            StopChargeVfxObserversRpc(); // cortar la telegrafía para los demás, coincidiendo con el disparo
            if (ability == null) return;

            // Cooldown arranca AHORA (al disparar), no cuando empezó a cargar.
            float castSpeed = _stats != null ? _stats.CastSpeedMultiplier : 1f;
            _serverCooldownEndTick[slot] = TicksFromNow(ability.Cooldown / Mathf.Max(0.1f, castSpeed));

            // Carga medida contra el reloj del servidor y acotada a [0..1].
            float held = (float)base.TimeManager.TicksToTime(base.TimeManager.Tick - _serverChargeStartTick[slot]);
            float maxCharge = Mathf.Max(0.01f, ability.MaxChargeDuration);
            float charge = Mathf.Clamp01(held / maxCharge);

            ExecuteCast(ability, slot, aimDirection, aimPoint, fireTick, charge);
        }

        // ---------- Helpers de cliente ----------

        private void PredictCooldownLocally(int slot, AbilitySO ability)
        {
            float castSpeed = _stats != null ? _stats.CastSpeedMultiplier : 1f;
            _localCooldownEndTick[slot] = TicksFromNow(ability.Cooldown / Mathf.Max(0.1f, castSpeed));
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

            if (ability.TryGetOwnerDash(aimDirection, out Vector3 dashDir, out float dashSpeed, out float dashDur))
                _movement?.StartDash(dashDir, dashSpeed, dashDur);
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
        private void CastServerRpc(int slot, Vector3 aimDirection, Vector3 aimPoint, PreciseTick fireTick)
        {
            if (!CanActServer) return;
            if (slot < 0 || slot >= _equippedAbilities.Length) return;
            AbilitySO ability = _equippedAbilities[slot];
            if (ability == null) return;

            if (IsOnCooldown(_serverCooldownEndTick[slot]))
            {
                RejectCastTargetRpc(base.Owner, slot, RemainingSeconds(_serverCooldownEndTick[slot]));
                return;
            }

            if (_mana != null && !_mana.TrySpend(ability.ResourceCost))
            {
                RejectCastTargetRpc(base.Owner, slot, RemainingSeconds(_serverCooldownEndTick[slot]));
                return;
            }

            float castSpeed = _stats != null ? _stats.CastSpeedMultiplier : 1f;
            _serverCooldownEndTick[slot] = TicksFromNow(ability.Cooldown / Mathf.Max(0.1f, castSpeed));

            if (ability.WindupDuration > 0f)
            {
                PlayChargeVfxObserversRpc(ability.WindupDuration); // telegrafía para los demás
                StartCoroutine(ExecuteAfterWindup(ability, slot, aimDirection, aimPoint, fireTick, ability.WindupDuration));
            }
            else
            {
                ExecuteCast(ability, slot, aimDirection, aimPoint, fireTick);
            }
        }

        [Server]
        private System.Collections.IEnumerator ExecuteAfterWindup(AbilitySO ability, int slot, Vector3 aimDirection, Vector3 aimPoint, PreciseTick fireTick, float delay)
        {
            _hasPendingAim[slot] = false;
            yield return new WaitForSeconds(delay);

            if (_hasPendingAim[slot])
            {
                aimDirection = _pendingAimDirection[slot];
                aimPoint = _pendingAimPoint[slot];
            }

            ExecuteCast(ability, slot, aimDirection, aimPoint, fireTick);
            StopChargeVfxObserversRpc(); // cortar la telegrafía para los demás, coincidiendo con el disparo
        }

        [Server]
        private void ExecuteCast(AbilitySO ability, int slot, Vector3 aimDirection, Vector3 aimPoint, PreciseTick fireTick, float charge = 0f)
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

            var context = new AbilityCastContext(
                casterNetworkId: base.ObjectId,
                origin: origin,
                aimDirection: aimDirection.normalized,
                aimPoint: aimPoint,
                tick: adjustedFireTick,
                damageMultiplier: dmgMul,
                slot: slot,
                chargeNormalized: charge
            );

            ability.Execute(_executor, in context);

            if (ability.MuzzlePrefab != null)
                PlayMuzzleObserversRpc(origin, aimDirection);
            if (ability.CastClip != null)
                PlayCastSfxObserversRpc(origin, slot);
        }

        [TargetRpc]
        private void RejectCastTargetRpc(FishNet.Connection.NetworkConnection conn, int slot, float cooldownRemaining)
        {
            // Llega el tiempo RESTANTE, no un tick absoluto del servidor: el tick del servidor no
            // significa nada en el reloj local del cliente. Se convierte a ticks LOCALES acá.
            _localCooldownEndTick[slot] = cooldownRemaining > 0f ? TicksFromNow(cooldownRemaining) : 0u;
            if (_localCharging[slot])
            {
                _localCharging[slot] = false;
                _trajectoryPreview?.Hide();
                _chargeVfx?.EndCharge();
            }
        }


        public void NotifyProjectileImpact(Vector3 point, Vector3 normal, bool hitConfirmed, bool isKill)
        {
            PlayImpactObserversRpc(point, normal);

            if (hitConfirmed)
                PlayHitMarkerTargetRpc(base.Owner, isKill);
        }

        public event Action<bool> OnHitConfirmed;

        [TargetRpc]
        private void PlayHitMarkerTargetRpc(FishNet.Connection.NetworkConnection conn, bool isKill)
        {
            OnHitConfirmed?.Invoke(isKill);
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
            AbilitySO ability = (slot >= 0 && slot < _equippedAbilities.Length) ? _equippedAbilities[slot] : null;
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
            AbilitySO ability = (slot >= 0 && slot < _equippedAbilities.Length) ? _equippedAbilities[slot] : null;
            if (ability == null) return;

            AudioClip clip = wallHit ? ability.SurfaceImpactClip : ability.ImpactClip;
            if (clip != null)
                VFXManager.PlaySfx(clip, point);
        }


        public void SetInputBlocked(bool blocked) => _inputBlocked = blocked;

        public int AbilitySlotCount => _equippedAbilities.Length;

        public AbilitySO GetAbility(int slot)
        {
            if (slot < 0 || slot >= _equippedAbilities.Length) return null;
            return _equippedAbilities[slot];
        }

        public float GetCooldownNormalized(int slot) => GetCooldownRemainingNormalized(slot);
    }
}