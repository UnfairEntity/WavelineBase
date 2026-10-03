using UnityEngine;
using UnityEngine.Events;

namespace Core.Events
{
    public class GameEventListener : MonoBehaviour
    {
        public GameEvent @event;
        public UnityEvent response;

        private void OnEnable()
        {
            if (@event == null)
            {
                Debug.LogWarning($"[GameEventListener] No event assigned on '{name}'.", this);
                return;
            }
            @event.RegisterListener(this);
        }

        private void OnDisable()
        {
            if (@event != null) @event.UnregisterListener(this);
        }

        public void OnEventRaised() => response?.Invoke();
    }
}
