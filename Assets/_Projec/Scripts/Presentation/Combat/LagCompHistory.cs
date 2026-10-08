using System.Collections.Generic;
using FishNet.Managing.Timing;
using FishNet.Object;
using Game.Presentation.Player;
using UnityEngine;

namespace Game.Presentation.Combat
{
    /// <summary>
    /// Server-only. Lag compensation propia: guarda la posición de cada entidad golpeable por tick
    /// y permite "rebobinar" sus hitboxes a un tick pasado para resolver un impacto contra lo que
    /// el tirador veía en su pantalla.
    ///
    /// Reemplaza al RollbackManager / ColliderRollback de Fish-Net: en la versión gratuita esos
    /// métodos están vacíos (Rollback y Return no hacen nada), así que la compensación nunca corría.
    ///
    /// Se agrega sola desde Health.OnStartServer (no hace falta ponerla en los prefabs). Mueve solo
    /// los hijos en capa Hitbox (nunca el root con el CharacterController) y los devuelve al presente
    /// en Restore(), dentro del mismo tick.
    /// </summary>
    public class LagCompHistory : MonoBehaviour
    {
        private const int HistorySize = 128; // ~2 s a 60 Hz (el techo real de rewind lo pone Projectile)

        private static readonly List<LagCompHistory> _all = new List<LagCompHistory>();
        private static readonly List<Transform> _movedTransforms = new List<Transform>();
        private static readonly List<Vector3> _movedOriginals = new List<Vector3>();
        private static bool _rewound;
        private static int _hitboxLayer = -1;

        private readonly uint[] _ticks = new uint[HistorySize];
        private readonly Vector3[] _positions = new Vector3[HistorySize];
        private Transform[] _hitboxes = System.Array.Empty<Transform>();
        private TimeManager _timeManager;

        public bool IsPlayer { get; private set; }

        /// <summary>Server. Asegura el historial en la entidad y empieza a grabar.</summary>
        public static void EnsureOn(NetworkBehaviour owner)
        {
            if (owner == null || owner.TimeManager == null) return;
            if (!owner.TryGetComponent(out LagCompHistory history))
                history = owner.gameObject.AddComponent<LagCompHistory>();
            history.Begin(owner.TimeManager);
        }

        /// <summary>Server. Deja de grabar (despawn / vuelta al pool).</summary>
        public static void StopOn(GameObject go)
        {
            if (go != null && go.TryGetComponent(out LagCompHistory history))
                history.End();
        }

        private void Begin(TimeManager timeManager)
        {
            if (_timeManager != null) return; // ya grabando

            if (_hitboxLayer < 0) _hitboxLayer = LayerMask.NameToLayer("Hitbox");

            var hitboxes = new List<Transform>();
            foreach (var col in GetComponentsInChildren<Collider>(true))
            {
                if (col.gameObject.layer != _hitboxLayer) continue;
                if (col.transform == transform) continue; // nunca mover el root (CharacterController)
                if (!hitboxes.Contains(col.transform)) hitboxes.Add(col.transform);
            }
            _hitboxes = hitboxes.ToArray();
            IsPlayer = GetComponent<PlayerAvatarState>() != null;

            System.Array.Clear(_ticks, 0, HistorySize); // instancia reutilizada del pool: historial viejo fuera

            _timeManager = timeManager;
            _timeManager.OnPostTick += Record;
            _all.Add(this);
        }

        private void End()
        {
            if (_timeManager == null) return;
            _timeManager.OnPostTick -= Record;
            _timeManager = null;
            _all.Remove(this);
        }

        private void OnDestroy() => End();

        private void Record()
        {
            uint tick = _timeManager.Tick;
            int i = (int)(tick % HistorySize);
            _ticks[i] = tick;
            _positions[i] = transform.position;
        }

        private bool TryGetPosition(uint tick, out Vector3 position)
        {
            int i = (int)(tick % HistorySize);
            if (_ticks[i] == tick)
            {
                position = _positions[i];
                return true;
            }
            position = default;
            return false;
        }

        /// <summary>
        /// Server. Mueve las hitboxes de las entidades elegidas a donde estaban en ese tick.
        /// Llamar SIEMPRE a Restore() después de los casts.
        /// </summary>
        public static void Rewind(uint tick, bool players, bool nonPlayers)
        {
            if (_rewound) Restore();
            _rewound = true;

            for (int e = 0; e < _all.Count; e++)
            {
                LagCompHistory h = _all[e];
                if (h.IsPlayer ? !players : !nonPlayers) continue;
                if (!h.TryGetPosition(tick, out Vector3 past)) continue; // sin dato de ese tick: queda en el presente

                Vector3 delta = past - h.transform.position;
                if (delta.sqrMagnitude < 0.0001f) continue;

                for (int i = 0; i < h._hitboxes.Length; i++)
                {
                    Transform t = h._hitboxes[i];
                    if (t == null) continue;
                    _movedTransforms.Add(t);
                    _movedOriginals.Add(t.position);
                    t.position += delta;
                }
            }

            if (_movedTransforms.Count > 0)
                Physics.SyncTransforms();
        }

        /// <summary>Server. Devuelve al presente todo lo que movió Rewind().</summary>
        public static void Restore()
        {
            if (!_rewound) return;
            _rewound = false;

            bool any = _movedTransforms.Count > 0;
            for (int i = _movedTransforms.Count - 1; i >= 0; i--)
            {
                if (_movedTransforms[i] != null)
                    _movedTransforms[i].position = _movedOriginals[i];
            }
            _movedTransforms.Clear();
            _movedOriginals.Clear();

            if (any)
                Physics.SyncTransforms();
        }
    }
}