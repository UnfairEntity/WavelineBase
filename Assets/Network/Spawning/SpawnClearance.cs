using UnityEngine;

namespace Network.Spawning
{
    /// <summary>
    /// The volume a spawned player will occupy, relative to its pivot (unrotated), used for
    /// overlap checks and gizmos. Built from the player prefab's collider (CharacterController,
    /// CapsuleCollider, SphereCollider or BoxCollider) or from SpawnManager's manual values.
    /// </summary>
    public readonly struct SpawnClearance
    {
        public enum ShapeKind { Capsule, Sphere, Box }

        public ShapeKind Kind { get; }
        /// <summary>Capsule: end-sphere centers. Sphere/Box: both equal the center.</summary>
        public Vector3 PointA { get; }
        public Vector3 PointB { get; }
        /// <summary>Capsule/Sphere radius.</summary>
        public float Radius { get; }
        /// <summary>Box half size and local rotation.</summary>
        public Vector3 HalfExtents { get; }
        public Quaternion BoxRotation { get; }
        /// <summary>Where this shape came from, for logs and tooltips (e.g. "NetworkPlayer: CharacterController").</summary>
        public string Source { get; }

        private SpawnClearance(ShapeKind kind, Vector3 a, Vector3 b, float radius, Vector3 halfExtents,
                               Quaternion boxRotation, string source)
        {
            Kind = kind;
            PointA = a;
            PointB = b;
            Radius = radius;
            HalfExtents = halfExtents;
            BoxRotation = boxRotation;
            Source = source;
        }

        public static SpawnClearance Capsule(Vector3 a, Vector3 b, float radius, string source) =>
            new(ShapeKind.Capsule, a, b, Mathf.Max(0.01f, radius), Vector3.zero, Quaternion.identity, source);

        public static SpawnClearance Sphere(Vector3 center, float radius, string source) =>
            new(ShapeKind.Sphere, center, center, Mathf.Max(0.01f, radius), Vector3.zero, Quaternion.identity, source);

        public static SpawnClearance Box(Vector3 center, Vector3 halfExtents, Quaternion rotation, string source) =>
            new(ShapeKind.Box, center, center, 0f, Vector3.Max(halfExtents, Vector3.one * 0.01f), rotation, source);

        /// <summary>Upright capsule from radius/height/center (the manual settings).</summary>
        public static SpawnClearance UprightCapsule(Vector3 center, float radius, float height, string source)
        {
            var half = Mathf.Max(height * 0.5f - radius, 0f);
            return Capsule(center - Vector3.up * half, center + Vector3.up * half, radius, source);
        }

        /// <summary>Horizontal half-width, used to keep reservations and fallback searches apart.</summary>
        public float FootprintRadius
        {
            get
            {
                switch (Kind)
                {
                    case ShapeKind.Box:
                    {
                        var e = BoxRotation * HalfExtents;
                        return Mathf.Max(Mathf.Abs(e.x), Mathf.Abs(e.z), HalfExtents.x, HalfExtents.z);
                    }
                    case ShapeKind.Capsule:
                    {
                        var spread = Mathf.Max(Mathf.Abs(PointA.x - PointB.x), Mathf.Abs(PointA.z - PointB.z)) * 0.5f;
                        return Radius + spread;
                    }
                    default:
                        return Radius;
                }
            }
        }

        /// <summary>Same shape grown by <paramref name="padding"/> on every side.</summary>
        public SpawnClearance Padded(float padding)
        {
            if (padding <= 0f) return this;
            return Kind == ShapeKind.Box
                ? new SpawnClearance(Kind, PointA, PointB, Radius, HalfExtents + Vector3.one * padding, BoxRotation, Source)
                : new SpawnClearance(Kind, PointA, PointB, Radius + padding, HalfExtents, BoxRotation, Source);
        }

        /// <summary>True if this shape, placed at a pivot pose, overlaps any non-trigger collider on <paramref name="layers"/>.
        /// <paramref name="lift"/> raises the test volume so resting on the floor doesn't count.</summary>
        public bool Overlaps(Vector3 position, Quaternion rotation, LayerMask layers, float lift)
        {
            var offset = position + Vector3.up * lift;
            switch (Kind)
            {
                case ShapeKind.Sphere:
                    return Physics.CheckSphere(offset + rotation * PointA, Radius, layers, QueryTriggerInteraction.Ignore);
                case ShapeKind.Box:
                    return Physics.CheckBox(offset + rotation * PointA, HalfExtents, rotation * BoxRotation, layers,
                                            QueryTriggerInteraction.Ignore);
                default:
                    return Physics.CheckCapsule(offset + rotation * PointA, offset + rotation * PointB, Radius, layers,
                                                QueryTriggerInteraction.Ignore);
            }
        }

        /// <summary>Draws the shape at a pivot pose with the current Gizmos color.</summary>
        public void DrawGizmo(Vector3 position, Quaternion rotation)
        {
            switch (Kind)
            {
                case ShapeKind.Sphere:
                    Gizmos.DrawWireSphere(position + rotation * PointA, Radius);
                    break;

                case ShapeKind.Box:
                {
                    var previous = Gizmos.matrix;
                    Gizmos.matrix = Matrix4x4.TRS(position + rotation * PointA, rotation * BoxRotation, Vector3.one);
                    Gizmos.DrawWireCube(Vector3.zero, HalfExtents * 2f);
                    Gizmos.matrix = previous;
                    break;
                }

                default:
                {
                    var a = position + rotation * PointA;
                    var b = position + rotation * PointB;
                    Gizmos.DrawWireSphere(a, Radius);
                    Gizmos.DrawWireSphere(b, Radius);

                    // Four side lines joining the end spheres.
                    var axis = b - a;
                    var up = axis.sqrMagnitude > 1e-6f ? axis.normalized : rotation * Vector3.up;
                    var side = Vector3.Cross(up, Mathf.Abs(Vector3.Dot(up, Vector3.forward)) > 0.9f ? Vector3.right : Vector3.forward).normalized;
                    var side2 = Vector3.Cross(up, side);
                    foreach (var dir in new[] { side, -side, side2, -side2 })
                        Gizmos.DrawLine(a + dir * Radius, b + dir * Radius);
                    break;
                }
            }
        }

        // ---------------- Building from a prefab ----------------

        /// <summary>
        /// Reads the clearance from a prefab's collider, relative to the prefab root's pivot.
        /// Prefers a CharacterController, then Capsule, Sphere and Box colliders (non-trigger),
        /// on the root first, then children. Returns false if none is found.
        /// </summary>
        public static bool TryFromPrefab(GameObject prefab, out SpawnClearance clearance)
        {
            clearance = default;
            if (prefab == null) return false;

            var collider = FindCollider(prefab);
            if (collider == null) return false;

            // Collider space -> prefab-root space (handles child offsets, rotation and scale).
            var toRoot = prefab.transform.worldToLocalMatrix * collider.transform.localToWorldMatrix;
            var scale = Abs(toRoot.lossyScale);
            var source = $"{prefab.name}: {collider.GetType().Name}";

            switch (collider)
            {
                case CharacterController cc:
                {
                    // The skin width is part of the space the controller keeps clear.
                    var radius = (cc.radius + cc.skinWidth) * Mathf.Max(scale.x, scale.z);
                    var height = (cc.height + cc.skinWidth * 2f) * scale.y;
                    var center = toRoot.MultiplyPoint3x4(cc.center);
                    var up = toRoot.MultiplyVector(Vector3.up).normalized;
                    var half = Mathf.Max(height * 0.5f - radius, 0f);
                    clearance = Capsule(center - up * half, center + up * half, radius, source);
                    return true;
                }
                case CapsuleCollider capsule:
                {
                    var axis = capsule.direction == 0 ? Vector3.right : capsule.direction == 2 ? Vector3.forward : Vector3.up;
                    var axisScale = capsule.direction == 0 ? scale.x : capsule.direction == 2 ? scale.z : scale.y;
                    var radiusScale = capsule.direction == 0 ? Mathf.Max(scale.y, scale.z)
                                    : capsule.direction == 2 ? Mathf.Max(scale.x, scale.y)
                                    : Mathf.Max(scale.x, scale.z);
                    var radius = capsule.radius * radiusScale;
                    var height = capsule.height * axisScale;
                    var center = toRoot.MultiplyPoint3x4(capsule.center);
                    var dir = toRoot.MultiplyVector(axis).normalized;
                    var half = Mathf.Max(height * 0.5f - radius, 0f);
                    clearance = Capsule(center - dir * half, center + dir * half, radius, source);
                    return true;
                }
                case SphereCollider sphere:
                    clearance = Sphere(toRoot.MultiplyPoint3x4(sphere.center),
                                       sphere.radius * Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z)), source);
                    return true;
                case BoxCollider box:
                    clearance = Box(toRoot.MultiplyPoint3x4(box.center), Vector3.Scale(box.size * 0.5f, scale),
                                    toRoot.rotation, source);
                    return true;
                default:
                    return false;
            }
        }

        private static Collider FindCollider(GameObject prefab)
        {
            var controller = prefab.GetComponent<CharacterController>();
            if (controller != null) return controller;

            Collider best = null;
            foreach (var candidate in prefab.GetComponentsInChildren<Collider>(true))
            {
                if (candidate.isTrigger || !IsSupported(candidate)) continue;
                if (candidate is CharacterController) return candidate;
                if (candidate.gameObject == prefab) return candidate; // root collider wins
                best ??= candidate;
            }
            return best;
        }

        private static bool IsSupported(Collider c) =>
            c is CharacterController or CapsuleCollider or SphereCollider or BoxCollider;

        private static Vector3 Abs(Vector3 v) => new(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
    }
}
