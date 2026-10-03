using System.Collections;
using UnityEngine;
using UnityEngine.Audio;

namespace Audio
{
    /// <summary>
    /// A single pooled, positionable audio voice. Owned and recycled by AudioManager -
    /// don't Instantiate/Destroy these directly; use AudioManager.PlayAtPoint,
    /// PlayAttached, or PlayUI instead.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class AudioEmitter : MonoBehaviour
    {
        private AudioSource _source;
        private AudioManager _owner;
        private AudioMixerGroup _defaultGroup;
        private Coroutine _releaseRoutine;
        private float _releaseAt = -1f; // realtime at which a non-looping sound should return to the pool; -1 = none

        public bool IsPlaying => _source != null && _source.isPlaying;

        public void Initialize(AudioManager owner, AudioMixerGroup defaultGroup)
        {
            _owner = owner;
            _defaultGroup = defaultGroup;
            _source = GetComponent<AudioSource>();
            _source.playOnAwake = false;
        }

        /// <summary>Fire-and-forget one-shot. Auto-releases back to the pool once the clip finishes.</summary>
        public void PlayOneShot(AudioClip clip, float volumeScale = 1f, float pitch = 1f, bool spatial = true, AudioMixerGroup group = null)
        {
            ConfigureSource(spatial, group);
            gameObject.SetActive(true);

            // Reset anything a previous Play() on this pooled voice may have left behind.
            _source.Stop();
            _source.clip = null;
            _source.loop = false;
            _source.volume = 1f;

            _source.pitch = pitch;
            _source.PlayOneShot(clip, volumeScale);
            ScheduleRelease(ClipDuration(clip, pitch));
        }

        /// <summary>Looping or long-running playback. Call Stop() to release it early.</summary>
        public void Play(AudioClip clip, float volumeScale = 1f, float pitch = 1f, bool loop = false, bool spatial = true, AudioMixerGroup group = null)
        {
            ConfigureSource(spatial, group);
            gameObject.SetActive(true);
            _source.clip = clip;
            _source.volume = volumeScale;
            _source.pitch = pitch;
            _source.loop = loop;
            _source.Play();

            CancelRelease();
            if (!loop) ScheduleRelease(ClipDuration(clip, pitch));
        }

        public void Stop()
        {
            CancelRelease();
            _source.Stop();
            Release();
        }

        /// <summary>
        /// A non-looping sound attached to a target that got deactivated loses its release
        /// coroutine and would sit in the pool as "busy" forever. Returns it to the pool.
        /// Looping sounds are left alone - their caller owns them via Stop().
        /// </summary>
        internal bool TryReclaimOrphan()
        {
            if (_source == null || _source.loop) return false;
            if (!gameObject.activeSelf || gameObject.activeInHierarchy) return false;
            Stop();
            return true;
        }

        private void OnEnable()
        {
            // Re-activated after its parent was disabled mid-sound: resume the pending release.
            if (_releaseAt >= 0f && _releaseRoutine == null)
                _releaseRoutine = StartCoroutine(ReleaseAfter(Mathf.Max(0f, _releaseAt - Time.realtimeSinceStartup)));
        }

        private void OnDisable()
        {
            // Unity stops coroutines on disable; forget the dead handle so OnEnable can restart it.
            _releaseRoutine = null;
        }

        private static float ClipDuration(AudioClip clip, float pitch) => clip.length / Mathf.Max(Mathf.Abs(pitch), 0.01f);

        private void ConfigureSource(bool spatial, AudioMixerGroup group)
        {
            _source.spatialBlend = spatial ? 1f : 0f;
            _source.outputAudioMixerGroup = group != null ? group : _defaultGroup;
        }

        private void ScheduleRelease(float delay)
        {
            if (_releaseRoutine != null) StopCoroutine(_releaseRoutine);
            _releaseAt = Time.realtimeSinceStartup + delay;
            _releaseRoutine = StartCoroutine(ReleaseAfter(delay));
        }

        private void CancelRelease()
        {
            if (_releaseRoutine != null) StopCoroutine(_releaseRoutine);
            _releaseRoutine = null;
            _releaseAt = -1f;
        }

        // Real time, so sounds still return to the pool while the game is paused (timeScale = 0).
        private IEnumerator ReleaseAfter(float delay)
        {
            yield return new WaitForSecondsRealtime(delay);
            Release();
        }

        private void Release()
        {
            _releaseRoutine = null;
            _releaseAt = -1f;
            transform.SetParent(_owner != null ? _owner.transform : null);
            gameObject.SetActive(false);
        }
    }
}
