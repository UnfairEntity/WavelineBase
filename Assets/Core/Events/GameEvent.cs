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
            for (int i = _listeners.Count - 1; i >= 0; i--)
                _listeners[i].OnEventRaised();
        }
 
        public void RegisterListener(GameEventListener l)   => _listeners.Add(l);
        public void UnregisterListener(GameEventListener l) => _listeners.Remove(l);
    }
}
