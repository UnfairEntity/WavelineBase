using System;
using System.Collections.Generic;
using UnityEngine;

namespace Core.Events
{
    [CreateAssetMenu(menuName = "Events/GameEvent")]
    public class GameEvent : ScriptableObject
    {
        private readonly List<GameEventListener> _listeners = new();

        public void Raise()
        {
            // Iterate backwards: listeners may remove themselves on response
            for (var i = _listeners.Count - 1; i >= 0; i--)
            {
                // A response may have removed more than one listener; skip indices that no longer exist.
                if (i >= _listeners.Count) continue;

                // One faulty listener must not prevent the rest from reacting.
                try
                {
                    _listeners[i].OnEventRaised();
                }
                catch (Exception e)
                {
                    Debug.LogException(e, this);
                }
            }
        }

        public void RegisterListener(GameEventListener l)
        {
            if (l != null && !_listeners.Contains(l)) _listeners.Add(l);
        }

        public void UnregisterListener(GameEventListener l) => _listeners.Remove(l);
    }
}
