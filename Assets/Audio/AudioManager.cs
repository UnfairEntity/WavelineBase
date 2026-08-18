using System;
using System.Collections;
using System.Collections.Generic;
using Core;
using UnityEngine;
using UnityEngine.Audio;

namespace Audio
{
    public enum AudioCategory
    {
        Master,
        Music,
        SFX, // world sounds - stationary one-shots and moving/attached emitters
        UI   // non-spatial UI / global sounds
    }

    /// <summary>
    /// PURPOSE: Central audio playback and mixing.
    ///   - World SFX: pooled, spatialized AudioSources for stationary (PlayAtPoint) and
    ///     moving/attached (PlayAttached) one-shots and loops.
    ///   - UI/global SFX: non-spatial one-shots via PlayUI.
    ///   - Music: one active track at a time with crossfade, a FIFO queue for what plays
    ///     next, and timestamped "keyframe" events read off the AudioTrackData asset.
    ///   - Per-category volume, routed through an AudioMixer and persisted via SaveManager.
    /// DEPENDENCIES: An AudioMixer asset with exposed float parameters named
    ///               "MasterVolume", "MusicVolume", "SFXVolume", "UIVolume".
    /// EVENTS PUBLISHED: OnVolumeChanged(AudioCategory, float), OnTrackChanged(AudioTrackData)
    /// PUBLIC API: PlayAtPoint, PlayAttached, PlayUI, SetVolume, GetVolume,
    ///             PlayMusic, QueueMusic, SkipMusic, StopMusic
    /// </summary>
    [RequireComponent(typeof(AudioSource))] // reserved as music crossfade source #1
    public class AudioManager : Singleton<AudioManager>
    {
        [Header("Mixer")]
        [SerializeField] private AudioMixer mixer;
        [SerializeField] private AudioMixerGroup sfxGroup;
        [SerializeField] private AudioMixerGroup uiGroup;
        [SerializeField] private AudioMixerGroup musicGroup;

        [Header("World SFX Pool")]
        [SerializeField] private int poolSize = 16;
        [Tooltip("Optional prefab for pooled world-sound emitters (e.g. pre-configured 3D rolloff). Leave empty to use plain default AudioSources.")]
        [SerializeField] private GameObject worldEmitterPrefab;

        [Header("Music")]
        [SerializeField] private float musicCrossfadeTime = 1.5f;

        public event Action<AudioCategory, float> OnVolumeChanged;
        public event Action<AudioTrackData> OnTrackChanged;

        private readonly Dictionary<AudioCategory, float> _volumes = new();
        private readonly Queue<AudioTrackData> _musicQueue = new();
        private readonly List<AudioEmitter> _pool = new();

        private AudioSource _musicSourceA;
        private AudioSource _musicSourceB;
        private AudioSource _activeMusicSource;
        private AudioTrackData _currentTrack;
        private Coroutine _musicRoutine;

        private static readonly AudioCategory[] AllCategories = (AudioCategory[])Enum.GetValues(typeof(AudioCategory));

        protected override void Awake()
        {
            base.Awake();
            if (IsDuplicate) return;

            BuildPool();
            SetupMusicSources();
            LoadVolumes();
        }

        // ---------------- Volume ----------------

        public float GetVolume(AudioCategory category) => _volumes.TryGetValue(category, out float v) ? v : 1f;

        /// <param name="linear01">0-1 linear volume, as you'd get straight from a UI slider.</param>
        public void SetVolume(AudioCategory category, float linear01)
        {
            linear01 = Mathf.Clamp01(linear01);
            _volumes[category] = linear01;

            if (mixer != null)
                mixer.SetFloat($"{category}Volume", LinearToDecibel(linear01));

            SaveManager.Instance.SaveFloat(VolumeKey(category), linear01);
            OnVolumeChanged?.Invoke(category, linear01);
        }

        private void LoadVolumes()
        {
            foreach (var category in AllCategories)
                SetVolume(category, SaveManager.Instance.LoadFloat(VolumeKey(category), 1f));
        }

        private static string VolumeKey(AudioCategory category) => $"Audio_{category}Volume";

        // AudioMixer parameters are in decibels; -80dB is effectively silent.
        private static float LinearToDecibel(float linear) => linear <= 0.0001f ? -80f : Mathf.Log10(linear) * 20f;

        // ---------------- World SFX (stationary + attached) ----------------

        private void BuildPool()
        {
            for (int i = 0; i < poolSize; i++)
                _pool.Add(CreateEmitter());
        }

        private AudioEmitter CreateEmitter()
        {
            GameObject go = worldEmitterPrefab != null
                ? Instantiate(worldEmitterPrefab, transform)
                : new GameObject("PooledAudioEmitter");

            go.transform.SetParent(transform);
            go.SetActive(false);

            AudioEmitter emitter = go.GetComponent<AudioEmitter>();
            if (emitter == null) emitter = go.AddComponent<AudioEmitter>();
            emitter.Initialize(this, sfxGroup);
            return emitter;
        }

        private AudioEmitter GetFromPool()
        {
            for (int i = _pool.Count - 1; i >= 0; i--)
            {
                var emitter = _pool[i];
                if (emitter == null) { _pool.RemoveAt(i); continue; } // destroyed alongside a target it was attached to
                if (!emitter.gameObject.activeSelf) return emitter;
            }

            var extra = CreateEmitter(); // pool exhausted - grow rather than drop the sound
            _pool.Add(extra);
            return extra;
        }

        /// <summary>Stationary world sound at a fixed point (impacts, explosions, environmental one-shots).</summary>
        public void PlayAtPoint(AudioClip clip, Vector3 position, float volumeScale = 1f, float pitch = 1f)
        {
            if (clip == null) return;
            var emitter = GetFromPool();
            emitter.transform.SetParent(transform);
            emitter.transform.position = position;
            emitter.PlayOneShot(clip, volumeScale, pitch, spatial: true, group: sfxGroup);
        }

        /// <summary>World sound that follows a moving object (footsteps, engine loops, held-item rattling).
        /// Returns the emitter so a looping sound can be stopped early - call Stop() on it before
        /// destroying the target, since a destroyed parent takes an attached child with it.</summary>
        public AudioEmitter PlayAttached(AudioClip clip, Transform target, float volumeScale = 1f, bool loop = false, float pitch = 1f)
        {
            if (clip == null || target == null) return null;
            var emitter = GetFromPool();
            emitter.transform.SetParent(target, worldPositionStays: false);
            emitter.transform.localPosition = Vector3.zero;
            emitter.Play(clip, volumeScale, pitch, loop, spatial: true, group: sfxGroup);
            return emitter;
        }

        /// <summary>Non-spatial UI/global sound - same presence regardless of listener position.</summary>
        public void PlayUI(AudioClip clip, float volumeScale = 1f)
        {
            if (clip == null) return;
            var emitter = GetFromPool();
            emitter.transform.SetParent(transform);
            emitter.transform.localPosition = Vector3.zero;
            emitter.PlayOneShot(clip, volumeScale, 1f, spatial: false, group: uiGroup);
        }

        // ---------------- Music: queue + crossfade + keyframed events ----------------

        private void SetupMusicSources()
        {
            _musicSourceA = GetComponent<AudioSource>();
            _musicSourceB = gameObject.AddComponent<AudioSource>();

            foreach (var source in new[] { _musicSourceA, _musicSourceB })
            {
                source.playOnAwake = false;
                source.loop = false;
                source.spatialBlend = 0f;
                source.outputAudioMixerGroup = musicGroup;
            }
            _activeMusicSource = _musicSourceA;
        }

        /// <summary>Plays immediately, clearing any queued tracks.</summary>
        public void PlayMusic(AudioTrackData track, bool crossfade = true)
        {
            if (track == null) return;
            _musicQueue.Clear();
            if (_musicRoutine != null) StopCoroutine(_musicRoutine);
            _musicRoutine = StartCoroutine(PlayTrackRoutine(track, crossfade));
        }

        /// <summary>Adds a track to the end of the playback queue. Starts playing immediately if nothing is currently playing.</summary>
        public void QueueMusic(AudioTrackData track)
        {
            if (track == null) return;
            _musicQueue.Enqueue(track);
            if (_currentTrack == null && _musicRoutine == null)
                SkipMusic();
        }

        public void SkipMusic()
        {
            if (_musicQueue.Count == 0) return;
            var next = _musicQueue.Dequeue();
            if (_musicRoutine != null) StopCoroutine(_musicRoutine);
            _musicRoutine = StartCoroutine(PlayTrackRoutine(next, crossfade: true));
        }

        public void StopMusic()
        {
            _musicQueue.Clear();
            if (_musicRoutine != null) StopCoroutine(_musicRoutine);
            _musicRoutine = null;
            _musicSourceA.Stop();
            _musicSourceB.Stop();
            _currentTrack = null;
        }

        private IEnumerator PlayTrackRoutine(AudioTrackData track, bool crossfade)
        {
            AudioSource incoming = _activeMusicSource == _musicSourceA ? _musicSourceB : _musicSourceA;
            AudioSource outgoing = _activeMusicSource;

            incoming.clip = track.clip;
            incoming.loop = track.loop;
            incoming.volume = crossfade ? 0f : track.baseVolume;
            incoming.Play();

            _currentTrack = track;
            _activeMusicSource = incoming;
            OnTrackChanged?.Invoke(track);

            float fadeTime = crossfade ? musicCrossfadeTime : 0f;
            float startOutgoingVolume = outgoing.volume;
            float t = 0f;
            while (t < fadeTime)
            {
                t += Time.unscaledDeltaTime; // matches MenuBase's fades: keeps working while paused
                float p = t / fadeTime;
                incoming.volume = Mathf.Lerp(0f, track.baseVolume, p);
                outgoing.volume = Mathf.Lerp(startOutgoingVolume, 0f, p);
                yield return null;
            }
            incoming.volume = track.baseVolume;
            outgoing.Stop();

            int nextKeyframe = 0;
            var keyframes = track.keyframes;
            while (incoming.isPlaying)
            {
                float time = incoming.time;
                if (nextKeyframe > 0 && keyframes.Count > 0 && time < keyframes[nextKeyframe - 1].time)
                    nextKeyframe = 0; // looped back to the start

                while (nextKeyframe < keyframes.Count && time >= keyframes[nextKeyframe].time)
                {
                    keyframes[nextKeyframe].onReached?.Invoke();
                    nextKeyframe++;
                }
                yield return null;
            }

            _musicRoutine = null;
            if (_musicQueue.Count > 0) SkipMusic();
        }
    }
}
