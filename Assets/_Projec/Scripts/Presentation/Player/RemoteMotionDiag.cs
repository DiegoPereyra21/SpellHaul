using FishNet.Object;
using UnityEngine;

namespace Game.Presentation.Player
{
    /// <summary>
    /// Diagnóstico (solo editor / development build): detecta saltos visuales del jugador REMOTO.
    /// Loguea cuando la velocidad aparente del root o del objeto gráfico supera un umbral.
    /// </summary>
    public class RemoteMotionDiag : NetworkBehaviour
    {
        [SerializeField] private Transform _visual;      // vacío = hijo "Graphics"
        [SerializeField] private float _speedThreshold = 25f; // m/s aparentes; dash máx ≈ 22

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private Vector3 _lastRoot, _lastVisual;
        private bool _has;

        private void Awake()
        {
            if (_visual == null) _visual = transform.Find("Graphics");
        }

        private void LateUpdate()
        {
            if (!base.IsClientStarted || base.IsOwner) { _has = false; return; }

            float dt = Mathf.Max(Time.unscaledDeltaTime, 0.0001f);
            Vector3 root = transform.position;
            Vector3 vis = _visual != null ? _visual.position : root;

            if (_has)
            {
                float rootSpeed = (root - _lastRoot).magnitude / dt;
                float visSpeed = (vis - _lastVisual).magnitude / dt;
                if (rootSpeed > _speedThreshold || visSpeed > _speedThreshold)
                    Debug.Log($"[RemoteDiag] rootSpeed={rootSpeed:F1} visualSpeed={visSpeed:F1} m/s " +
                              $"rootDy={(root.y - _lastRoot.y):F2} visDy={(vis.y - _lastVisual.y):F2} dt={dt * 1000f:F0}ms fps={1f / dt:F0}");
            }

            _lastRoot = root;
            _lastVisual = vis;
            _has = true;
        }
#endif
    }
}