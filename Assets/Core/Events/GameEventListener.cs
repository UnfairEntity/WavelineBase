using UnityEngine;
using UnityEngine.Events;

namespace Core.Events
{
    public class GameEventListener : MonoBehaviour
    {
        public GameEvent @event;
        public UnityEvent response;

        private void OnEnable() => @event.RegisterListener(this);
        private void OnDisable() => @event.UnregisterListener(this);

        public void OnEventRaised() => response.Invoke();
    }
}