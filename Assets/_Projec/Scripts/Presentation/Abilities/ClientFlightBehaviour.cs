using FishNet.Object;
using UnityEngine;

namespace Game.Presentation.Abilities
{
    /// <summary>
    /// Base de los proyectiles de vuelo determinista (recto o balístico). El servidor simula el vuelo y
    /// decide el impacto; los clientes reciben UN RPC (origen, velocidad, gravedad, tick) y simulan el
    /// vuelo localmente, en vez de recibir la posición por NetworkTransform en cada tick.
    /// Uso: el servidor llama BroadcastFlight() al final de su Initialize (el objeto ya está spawneado).
    /// </summary>
    public abstract class ClientFlightBehaviour : NetworkBehaviour
    {
        // Techo del adelanto por latencia / entrada tardía. Cubre la vida máxima de los orbes (8 s).
        private const float MaxCatchUpSeconds = 10f;

        private static int _stopMask;

        private bool _clientFlying;
        private bool _clientStopped;
        private Vector3 _flightOrigin;
        private Vector3 _flightVelocity;
        private float _flightGravity;
        private float _flightElapsed;

        public override void OnStartClient()
        {
            base.OnStartClient();
            _clientFlying = false; // instancia reutilizada del pool
        }

        public override void OnStopClient()
        {
            base.OnStopClient();
            _clientFlying = false;
        }

        /// <summary>Server. Avisa a los clientes cómo vuela este proyectil. gravity = aceleración en Y (negativa = cae).</summary>
        [Server]
        protected void BroadcastFlight(Vector3 origin, Vector3 velocity, float gravity)
        {
            FlightObserversRpc(origin, velocity, gravity, base.TimeManager.Tick);
        }

        [ObserversRpc(BufferLast = true, ExcludeServer = true)]
        private void FlightObserversRpc(Vector3 origin, Vector3 velocity, float gravity, uint spawnTick)
        {
            _flightOrigin = origin;
            _flightVelocity = velocity;
            _flightGravity = gravity;

            uint now = base.TimeManager.Tick;
            float since = now > spawnTick ? (float)base.TimeManager.TicksToTime(now - spawnTick) : 0f;
            _flightElapsed = Mathf.Clamp(since, 0f, MaxCatchUpSeconds);

            _clientStopped = false;
            _clientFlying = true;

            Vector3 pos = FlightPosition(_flightElapsed);
            if (Physics.Linecast(origin, pos, out RaycastHit hit, StopMask, QueryTriggerInteraction.Ignore))
            {
                pos = hit.point;
                _clientStopped = true;
            }
            transform.position = pos;
            ApplyRotation(_flightElapsed);
        }

        private void LateUpdate()
        {
            if (!_clientFlying || _clientStopped || base.IsServerStarted) return;

            Vector3 from = transform.position;
            _flightElapsed += Time.deltaTime;
            Vector3 to = FlightPosition(_flightElapsed);

            // Frena visualmente en paredes/piso; la detonación real la manda el servidor.
            if (Physics.Linecast(from, to, out RaycastHit hit, StopMask, QueryTriggerInteraction.Ignore))
            {
                transform.position = hit.point;
                _clientStopped = true;
                return;
            }

            transform.position = to;
            ApplyRotation(_flightElapsed);
        }

        private Vector3 FlightPosition(float t) =>
            _flightOrigin + _flightVelocity * t + Vector3.up * (0.5f * _flightGravity * t * t);

        private void ApplyRotation(float t)
        {
            Vector3 v = _flightVelocity + Vector3.up * (_flightGravity * t);
            if (v.sqrMagnitude > 0.0001f)
                transform.rotation = Quaternion.LookRotation(v.normalized);
        }

        private static int StopMask
        {
            get
            {
                if (_stopMask == 0) _stopMask = LayerMask.GetMask("Ground");
                return _stopMask;
            }
        }
    }
}