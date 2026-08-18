using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace Audio
{
    /// <summary>
    /// Metadata around a music clip: base volume, loop behavior, and a timeline of
    /// "keyframe" events fired as playback crosses each timestamp - beat drops, lyric
    /// cues, boss-phase triggers, anything gameplay/UI wants synced to the music
    /// instead of polling AudioSource.time by hand.
    /// </summary>
    [CreateAssetMenu(menuName = "Audio/Audio Track", fileName = "New Audio Track")]
    public class AudioTrackData : ScriptableObject
    {
        public AudioClip clip;
        [Range(0f, 1f)] public float baseVolume = 1f;
        public bool loop = true;

        [Tooltip("Fired in ascending 'time' order as the track plays. Keep them sorted for reliable loop-reset behavior.")]
        public List<AudioKeyframe> keyframes = new();
    }

    [Serializable]
    public class AudioKeyframe
    {
        public string label;
        [Min(0f)] public float time; // seconds from the start of the clip
        public UnityEvent onReached;
    }
}
