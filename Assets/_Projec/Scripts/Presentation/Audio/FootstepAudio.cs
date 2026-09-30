using UnityEngine;

namespace Game.Presentation.Audio
{
    /// <summary>
    /// Pasos y aterrizajes de un jugador (propio y ajenos), en 3D. Solo mira cómo se mueve el
    /// objeto (no toca el movimiento predicho): cada tantos metros recorridos en el piso suena un
    /// paso, y al tocar el piso después de un rato en el aire, el aterrizaje. Lo agrega en runtime
    /// PlayerAvatarState en los clientes.
    /// </summary>
    public class FootstepAudio : MonoBehaviour
    {
        private const float StrideDistance = 2.1f;   // metros entre pasos
        private const float MinSpeed = 1.2f;          // m/s: por debajo, quieto (sin pasos)
        private const float MinAirTimeForLand = 0.35f;
        private const float GroundProbe = 0.35f;

        private LayerMask _groundMask;
        private Vector3 _lastPosition;
        private float _distanceSinceStep;
        private float _airTime;
        private bool _wasGrounded = true;

        private void Awake()
        {
            _groundMask = LayerMask.GetMask("Ground");
            _lastPosition = transform.position;
        }

        private void LateUpdate()
        {
            Vector3 pos = transform.position;
            Vector3 delta = pos - _lastPosition;
            _lastPosition = pos;

            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            bool grounded = Physics.Raycast(pos + Vector3.up * 0.1f, Vector3.down, 0.1f + GroundProbe, _groundMask, QueryTriggerInteraction.Ignore);
            var lib = GameAudio.Library;

            if (!grounded)
            {
                _airTime += dt;
                _wasGrounded = false;
                return;
            }

            if (!_wasGrounded && _airTime >= MinAirTimeForLand && lib != null)
                GameAudio.Play3D(lib.Land, pos, lib.FootstepVolume * 1.3f, 0.05f);
            _wasGrounded = true;
            _airTime = 0f;

            Vector3 horizontal = new Vector3(delta.x, 0f, delta.z);
            float speed = horizontal.magnitude / dt;
            if (speed < MinSpeed || speed > 30f) { _distanceSinceStep = Mathf.Min(_distanceSinceStep, StrideDistance * 0.5f); return; }

            _distanceSinceStep += horizontal.magnitude;
            if (_distanceSinceStep >= StrideDistance && lib != null)
            {
                _distanceSinceStep = 0f;
                GameAudio.Play3D(GameAudio.Pick(lib.Footsteps), pos, lib.FootstepVolume, 0.08f);
            }
        }
    }
}
