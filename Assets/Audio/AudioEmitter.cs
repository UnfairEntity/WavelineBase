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
            _source.pitch = pitch;
            _source.PlayOneShot(clip, volumeScale);
            ScheduleRelease(clip.length / Mathf.Max(pitch, 0.01f));
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

            if (_releaseRoutine != null) { StopCoroutine(_releaseRoutine); _releaseRoutine = null; }
            if (!loop) ScheduleRelease(clip.length / Mathf.Max(pitch, 0.01f));
        }

        public void Stop()
        {
            if (_releaseRoutine != null) { StopCoroutine(_releaseRoutine); _releaseRoutine = null; }
            _source.Stop();
            Release();
        }

        private void ConfigureSource(bool spatial, AudioMixerGroup group)
        {
            _source.spatialBlend = spatial ? 1f : 0f;
            _source.outputAudioMixerGroup = group != null ? group : _defaultGroup;
        }

        private void ScheduleRelease(float delay)
        {
            if (_releaseRoutine != null) StopCoroutine(_releaseRoutine);
            _releaseRoutine = StartCoroutine(ReleaseAfter(delay));
        }

        private IEnumerator ReleaseAfter(float delay)
        {
            yield return new WaitForSeconds(delay);
            Release();
        }

        private void Release()
        {
            _releaseRoutine = null;
            transform.SetParent(_owner != null ? _owner.transform : null);
            gameObject.SetActive(false);
        }
    }
}
