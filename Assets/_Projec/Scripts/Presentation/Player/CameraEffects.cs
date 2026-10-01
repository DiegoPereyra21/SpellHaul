using System.Collections;
using UnityEngine;

namespace Game.Presentation.Player
{
    /// <summary>
    /// Local (owner-only) camera juice: FOV kicks, etc. Purely visual, never networked.
    /// Lives on the same GameObject as the player Camera.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class CameraEffects : MonoBehaviour
    {
        private Camera _camera;
        private float _baseFov;
        private Coroutine _fovRoutine;

        // FOV elegido en opciones. Se aplica a la Camera y a la CinemachineCamera (la que maneje
        // el lente). Sin elección (0) se dejan los valores originales del prefab, sin tocar.
        private Unity.Cinemachine.CinemachineCamera _cinemachine;
        private float _originalCameraFov;
        private float _originalLensFov;

        private void Awake()
        {
            _camera = GetComponent<Camera>();
            _cinemachine = GetComponent<Unity.Cinemachine.CinemachineCamera>();
            _originalCameraFov = _camera.fieldOfView;
            _originalLensFov = _cinemachine != null ? _cinemachine.Lens.FieldOfView : _originalCameraFov;
            _baseFov = _camera.fieldOfView;

            ApplyFieldOfView();
            Game.Presentation.Settings.DisplaySettings.Changed += ApplyFieldOfView;
        }

        private void OnDestroy()
        {
            Game.Presentation.Settings.DisplaySettings.Changed -= ApplyFieldOfView;
        }

        private void ApplyFieldOfView()
        {
            if (_camera == null) return;

            float horizontal = Game.Presentation.Settings.DisplaySettings.FieldOfView;
            float cameraFov, lensFov;
            if (horizontal > 0f)
            {
                // Horizontal → vertical (lo que usa Unity) según el aspecto de la pantalla.
                float aspect = Mathf.Max(0.1f, _camera.aspect);
                float vertical = 2f * Mathf.Atan(Mathf.Tan(horizontal * 0.5f * Mathf.Deg2Rad) / aspect) * Mathf.Rad2Deg;
                cameraFov = lensFov = vertical;
            }
            else
            {
                cameraFov = _originalCameraFov;
                lensFov = _originalLensFov;
            }

            _baseFov = cameraFov;
            if (_fovRoutine == null) _camera.fieldOfView = cameraFov;
            if (_cinemachine != null)
            {
                var lens = _cinemachine.Lens;
                lens.FieldOfView = lensFov;
                _cinemachine.Lens = lens;
            }
        }

        /// <summary>
        /// Kicks the FOV outward then eases it back to the base value.
        /// </summary>
        /// <param name="amount">Degrees added to the base FOV at the peak.</param>
        /// <param name="inDuration">Seconds to reach the peak.</param>
        /// <param name="outDuration">Seconds to return to base.</param>
        public void FovKick(float amount = 12f, float inDuration = 0.08f, float outDuration = 0.25f)
        {
            if (_fovRoutine != null) StopCoroutine(_fovRoutine);
            _fovRoutine = StartCoroutine(FovKickRoutine(amount, inDuration, outDuration));
        }

        private IEnumerator FovKickRoutine(float amount, float inDuration, float outDuration)
        {
            float targetFov = _baseFov + amount;

            // Ease out to peak.
            float t = 0f;
            float start = _camera.fieldOfView;
            while (t < inDuration)
            {
                t += Time.deltaTime;
                _camera.fieldOfView = Mathf.Lerp(start, targetFov, Mathf.SmoothStep(0f, 1f, t / inDuration));
                yield return null;
            }
            _camera.fieldOfView = targetFov;

            // Ease back to base.
            t = 0f;
            while (t < outDuration)
            {
                t += Time.deltaTime;
                _camera.fieldOfView = Mathf.Lerp(targetFov, _baseFov, Mathf.SmoothStep(0f, 1f, t / outDuration));
                yield return null;
            }
            _camera.fieldOfView = _baseFov;
            _fovRoutine = null;
        }
    }
}