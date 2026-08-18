using UnityEngine;

namespace Core
{
    /// <summary>
    /// Base class for scene-persistent single-instance managers, wired up via
    /// Bootstrapper (see Core/Bootstrap). Subclasses that override Awake() must call
    /// base.Awake() first, then check IsDuplicate and return early if true - otherwise
    /// setup code will run once more on the doomed duplicate before Unity actually
    /// destroys it at the end of the frame.
    /// </summary>
    public class Singleton<T> : MonoBehaviour where T : MonoBehaviour
    {
        private static T _instance;

        public static T Instance
        {
            get
            {
                if (_instance == null)
                    Debug.LogError($"[Singleton] {typeof(T)} is null! Is it created before something tries to use it?");
                return _instance;
            }
        }

        /// <summary>True for the remainder of this frame if this object was destroyed in Awake() because another instance already existed.</summary>
        protected bool IsDuplicate { get; private set; }

        protected virtual void Awake()
        {
            if (_instance != null && _instance != this)
            {
                IsDuplicate = true;
                Destroy(gameObject);
                return;
            }

            _instance = this as T;
            DontDestroyOnLoad(gameObject);
        }

        protected virtual void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }
    }
}
