using System.Collections.Generic;
using UnityEngine;

namespace Network.Spawning
{
    /// <summary>
    /// A place players can spawn. Drop one on an empty GameObject per spawn location in a
    /// level; rotation = the direction they face. With ground snap (the default), the point
    /// marks a spot on the floor and the player is placed standing on the ground below it;
    /// without it, position = exactly where the player's pivot goes.
    /// SpawnManager (server only) picks between all enabled SpawnPoints in loaded scenes.
    /// The green gizmo is the exact volume SpawnManager checks for overlap here (the player
    /// prefab's collider plus padding); grey means no SpawnManager was found.
    /// </summary>
    [DisallowMultipleComponent]
    public class SpawnPoint : MonoBehaviour
    {
        public enum GroundSnapMode
        {
            /// <summary>Follow SpawnManager's "Snap To Ground" setting.</summary>
            UseSpawnManager,
            /// <summary>Always rest the player on the ground below this point.</summary>
            Snap,
            /// <summary>Put the player's pivot exactly at this point (e.g. a spawn in mid-air or on a moving platform).</summary>
            DontSnap
        }

        private static readonly List<SpawnPoint> ActivePoints = new();

        /// <summary>All enabled spawn points in loaded scenes.</summary>
        public static IReadOnlyList<SpawnPoint> All => ActivePoints;

        [Tooltip("Order used by Fixed spawn mode (lowest first). Ties are broken by hierarchy order, then name.")]
        [SerializeField] private int order;

        [Tooltip("Optional group/team name. SpawnManager only uses points in its active group (empty = every point).")]
        [SerializeField] private string group = "";

        [Tooltip("Rest the player on the ground below this point. 'Use Spawn Manager' follows SpawnManager's Snap To Ground setting.")]
        [SerializeField] private GroundSnapMode groundSnap = GroundSnapMode.UseSpawnManager;

        [Header("Gizmo fallback")]
        [Tooltip("The gizmo normally draws SpawnManager's real clearance shape (green). These sizes are only " +
                 "used (grey gizmo) when no SpawnManager can be found in the project.")]
        [SerializeField] private float gizmoRadius = 0.5f;
        [SerializeField] private float gizmoHeight = 1f;

        public int Order => order;
        public string Group => group;
        public GroundSnapMode GroundSnap => groundSnap;
        public Pose Pose => new(transform.position, transform.rotation);

        private void OnEnable()
        {
            if (!ActivePoints.Contains(this)) ActivePoints.Add(this);
        }

        private void OnDisable() => ActivePoints.Remove(this);

        // Keeps the registry clean if Domain Reload is disabled in Enter Play Mode settings.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetRegistry() => ActivePoints.Clear();

        private void OnDrawGizmos()
        {
            var position = transform.position;
            var rotation = transform.rotation;

            // Draw exactly what SpawnManager will test here: the player prefab's collider (or its
            // manual clearance), at the pose the player will actually get (snapped to the ground
            // when enabled). Green = ok, yellow = snapping but no ground found, grey = no SpawnManager.
            SpawnClearance clearance;
            var spawnPosition = position;
            var manager = FindSpawnManagerForGizmos();
            if (manager != null)
            {
                clearance = manager.GetClearance();
                var snaps = manager.ShouldSnap(this);
                var grounded = manager.TryResolveSpawnPosition(position, rotation, snaps, out spawnPosition);
                Gizmos.color = grounded ? new Color(0.2f, 0.9f, 0.4f, 0.9f) : new Color(1f, 0.85f, 0.1f, 0.9f);

                if (snaps && grounded)
                {
                    // Link the marker to where the shape's base rests.
                    var basePoint = spawnPosition + Vector3.up * clearance.BottomOffset(rotation);
                    Gizmos.DrawLine(position, basePoint);
                    Gizmos.DrawWireSphere(position, 0.08f);
                }
            }
            else
            {
                clearance = SpawnClearance.UprightCapsule(Vector3.zero, gizmoRadius, gizmoHeight, "gizmo fallback");
                Gizmos.color = new Color(0.6f, 0.6f, 0.6f, 0.9f);
            }

            clearance.DrawGizmo(spawnPosition, rotation);
            Gizmos.DrawLine(spawnPosition, spawnPosition + transform.forward * (clearance.FootprintRadius + 0.5f));
        }

        private static SpawnManager FindSpawnManagerForGizmos()
        {
#if UNITY_EDITOR
            return SpawnManager.FindForGizmos();
#else
            return SpawnManager.Active;
#endif
        }
    }
}
