using Game.Core.Pooling;
using UnityEngine;

namespace Game.Presentation.Combat
{
    public class VFXManager : MonoBehaviour
    {
        private static VFXManager _instance;

        [Header("Proyectil básico")]
        [SerializeField] private ParticleSystem _projectileHitPrefab;
        [SerializeField] private int _projectileHitPoolSize = 8;

        [Header("Orbe explosivo")]
        [SerializeField] private ParticleSystem _orbExplosionPrefab;
        [SerializeField] private int _orbExplosionPoolSize = 4;

        [Header("Muzzle proyectil básico")]
        [SerializeField] private ParticleSystem _projectileMuzzlePrefab;
        [SerializeField] private int _projectileMuzzlePoolSize = 8;

        [Header("Parry")]
        [SerializeField] private ParticleSystem _parryActivePrefab;
        [SerializeField] private int _parryActivePoolSize = 4;
        [SerializeField] private ParticleSystem _parrySuccessPrefab;
        [SerializeField] private int _parrySuccessPoolSize = 4;

        [Header("Audio (SFX 3D genérico, pooleado)")]
        [Tooltip("Prefab con un AudioSource preconfigurado: Spatial Blend = 1 (3D), Play On Awake = false, Loop = false.")]
        [SerializeField] private AudioSource _sfxPrefab;
        [SerializeField] private int _sfxPoolSize = 12;

        private ObjectPool<ParticleSystem> _projectileHitPool;
        private ObjectPool<ParticleSystem> _orbExplosionPool;
        private ObjectPool<ParticleSystem> _projectileMuzzlePool;
        private ObjectPool<ParticleSystem> _parryActivePool;
        private ObjectPool<ParticleSystem> _parrySuccessPool;
        private ObjectPool<AudioSource> _sfxPool;

        private void Awake()
        {
            if (_instance != null) { Destroy(gameObject); return; }
            _instance = this;
            DontDestroyOnLoad(gameObject);

            if (_projectileHitPrefab != null)
                _projectileHitPool = new ObjectPool<ParticleSystem>(_projectileHitPrefab, _projectileHitPoolSize, transform);
            if (_orbExplosionPrefab != null)
                _orbExplosionPool = new ObjectPool<ParticleSystem>(_orbExplosionPrefab, _orbExplosionPoolSize, transform);
            if (_projectileMuzzlePrefab != null)
                _projectileMuzzlePool = new ObjectPool<ParticleSystem>(_projectileMuzzlePrefab, _projectileMuzzlePoolSize, transform);
            if (_parryActivePrefab != null)
                _parryActivePool = new ObjectPool<ParticleSystem>(_parryActivePrefab, _parryActivePoolSize, transform);
            if (_parrySuccessPrefab != null)
                _parrySuccessPool = new ObjectPool<ParticleSystem>(_parrySuccessPrefab, _parrySuccessPoolSize, transform);
            if (_sfxPrefab != null)
                _sfxPool = new ObjectPool<AudioSource>(_sfxPrefab, _sfxPoolSize, transform);
        }

        public static void PlayProjectileHit(Vector3 point, Quaternion rotation)
            => _instance?.PlayVFX(_instance._projectileHitPool, point, rotation);

        public static void PlayOrbExplosion(Vector3 point)
            => _instance?.PlayVFX(_instance._orbExplosionPool, point, Quaternion.identity);

        public static void PlayProjectileMuzzle(Vector3 point, Quaternion rotation)
            => _instance?.PlayVFX(_instance._projectileMuzzlePool, point, rotation);

        public static void PlayParryActive(Vector3 point, float scale = 1f)
            => _instance?.PlayVFXScaled(_instance._parryActivePool, point, Quaternion.identity, scale);

        public static void PlayParrySuccess(Vector3 point, float scale = 1f)
            => _instance?.PlayVFXScaled(_instance._parrySuccessPool, point, Quaternion.identity, scale);

        /// <summary>Reproduce un clip 3D pooleado en un punto del mundo. clip null = no-op (habilidad sin audio configurado).</summary>
        public static void PlaySfx(AudioClip clip, Vector3 point, float volume = 1f)
            => _instance?.PlaySfxInternal(clip, point, volume);

        private void PlaySfxInternal(AudioClip clip, Vector3 point, float volume)
        {
            if (clip == null || _sfxPool == null) return;
            AudioSource src = _sfxPool.Get(point, Quaternion.identity);
            src.clip = clip;
            src.volume = volume * Game.Presentation.Audio.AudioVolumes.Effects;
            src.Play();
            StartCoroutine(ReleaseAudioWhenDone(src));
        }

        private System.Collections.IEnumerator ReleaseAudioWhenDone(AudioSource src)
        {
            yield return new WaitWhile(() => src.isPlaying);
            src.clip = null; // liberar la referencia para el próximo uso del pool
            _sfxPool.Release(src);
        }

        private void PlayVFXScaled(ObjectPool<ParticleSystem> pool, Vector3 point, Quaternion rotation, float scale)
        {
            if (pool == null) return;
            ParticleSystem ps = pool.Get(point, rotation);

            // Forzar que el ParticleSystem (y sus hijos) respeten la escala del transform.
            ApplyHierarchyScaling(ps);
            ps.transform.localScale = Vector3.one * scale;

            ps.Play();
            StartCoroutine(ReleaseWhenDone(ps, pool));
        }

        private void ApplyHierarchyScaling(ParticleSystem root)
        {
            foreach (ParticleSystem ps in root.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = ps.main;
                main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            }
        }

        private void PlayVFX(ObjectPool<ParticleSystem> pool, Vector3 point, Quaternion rotation)
        {
            if (pool == null) return;
            ParticleSystem ps = pool.Get(point, rotation);
            ps.Play();
            StartCoroutine(ReleaseWhenDone(ps, pool));
        }

        private System.Collections.IEnumerator ReleaseWhenDone(ParticleSystem ps, ObjectPool<ParticleSystem> pool)
        {
            yield return new WaitUntil(() => !ps.isPlaying);
            ps.transform.localScale = Vector3.one; // resetear para el próximo uso del pool
            pool.Release(ps);
        }
    }
}