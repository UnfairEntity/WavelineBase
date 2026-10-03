using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Player
{
    /// <summary>
    /// On a networked player, only the owning client's copy may read input and drive movement.
    /// Without this, every copy of every player has a live PlayerInput that reads the local
    /// keyboard/gamepad. Pair it with a NetworkTransform set to Owner authority so the owner's
    /// movement is replicated. The CharacterController stays enabled on every copy so other
    /// players (and SpawnManager's overlap checks) still collide with it.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class NetworkPlayerOwnership : NetworkBehaviour
    {
        [Tooltip("Extra components to enable only on the owner's copy. PlayerInput and PlayerController are handled automatically.")]
        [SerializeField] private Behaviour[] ownerOnly = Array.Empty<Behaviour>();

        public override void OnNetworkSpawn() => ApplyOwnership();
        public override void OnGainedOwnership() => ApplyOwnership();
        public override void OnLostOwnership() => ApplyOwnership();

        private void ApplyOwnership()
        {
            var isOwner = IsOwner;
            SetEnabled(GetComponent<PlayerInput>(), isOwner);
            SetEnabled(GetComponent<PlayerController>(), isOwner);
            foreach (var behaviour in ownerOnly) SetEnabled(behaviour, isOwner);
        }

        private static void SetEnabled(Behaviour behaviour, bool value)
        {
            if (behaviour != null) behaviour.enabled = value;
        }
    }
}
