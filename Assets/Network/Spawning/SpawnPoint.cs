using System.Collections.Generic;
using UnityEngine;

namespace Network.Spawning
{
    /// <summary>
    /// A place players can spawn. Drop one on an empty GameObject per spawn location in a
    /// level; position = where the player's pivot goes, rotation = the direction they face.
    /// SpawnManager (server only) picks between all enabled SpawnPoints in loaded scenes.
    /// The green gizmo is the exact volume SpawnManager checks for overlap here (the player
    /// prefab's collider plus padding); grey means no SpawnManager was found.
    /// </summary>
    [DisallowMultipleComponent]
    public class SpawnPoint : MonoBehaviour
    {
        private static readonly List<SpawnPoint> ActivePoints = new();

        /// <summary>All enabled spawn points in loaded scenes.</summary>
        public static IReadOnlyList<SpawnPoint> All => ActivePoints;

        [Tooltip("Order used by Fixed spawn mode (lowest first). Ties are broken by hierarchy order, then name.")]
        [SerializeField] private int order;

        [Tooltip("Optional group/team name. SpawnManager only uses points in its active group (empty = every point).")]
        [SerializeField] private string group = "";

        [Header("Gizmo fallback")]
        [Tooltip("The gizmo normally draws SpawnManager's real clearance shape (green). These sizes are only " +
                 "used (grey gizmo) when no SpawnManager can be found in the project.")]
        [SerializeField] private float gizmoRadius = 0.5f;
        [SerializeField] private float gizmoHeight = 1f;

        public int Order => order;
        public string Group => group;
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
            // manual clearance), placed at this point's pose.
            SpawnClearance clearance;
            var manager = FindSpawnManagerForGizmos();
            if (manager != null)
            {
                clearance = manager.GetClearance();
                Gizmos.color = new Color(0.2f, 0.9f, 0.4f, 0.9f);
            }
            else
            {
                clearance = SpawnClearance.UprightCapsule(Vector3.zero, gizmoRadius, gizmoHeight, "gizmo fallback");
                Gizmos.color = new Color(0.6f, 0.6f, 0.6f, 0.9f);
            }

            clearance.DrawGizmo(position, rotation);
            Gizmos.DrawLine(position, position + transform.forward * (clearance.FootprintRadius + 0.5f));
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
