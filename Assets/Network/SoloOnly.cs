using UnityEngine;
using NetcodeManager = Unity.Netcode.NetworkManager;

namespace Network
{
    /// <summary>
    /// Put this on objects that should exist only in single-player, such as the local Player
    /// placed in a level for Solo play. When the level is loaded as part of a networked game
    /// (Netcode is running), the object removes itself, so it doesn't sit alongside the
    /// networked players spawned by SpawnManager.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public class SoloOnly : MonoBehaviour
    {
        private void Awake()
        {
            var netcode = NetcodeManager.Singleton;
            if (netcode == null || !netcode.IsListening) return;

            gameObject.SetActive(false);
            Destroy(gameObject);
        }
    }
}
