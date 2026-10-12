using System.Collections.Generic;
using Network.Spawning;
using NUnit.Framework;
using UnityEngine;

namespace WavelineBase.Tests
{
    /// <summary>The clearance shape read from a player's collider, and its geometry helpers.</summary>
    public class SpawnClearanceTests
    {
        private readonly List<GameObject> _created = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _created) if (go != null) Object.DestroyImmediate(go);
            _created.Clear();
        }

        private GameObject Make(string name)
        {
            var go = new GameObject(name);
            _created.Add(go);
            return go;
        }

        [Test]
        public void CharacterController_UsesRadiusHeightAndSkinWidth()
        {
            var go = Make("Player");
            var cc = go.AddComponent<CharacterController>();
            cc.radius = 0.5f;
            cc.height = 2f;
            cc.skinWidth = 0.08f;
            cc.center = Vector3.zero;

            Assert.IsTrue(SpawnClearance.TryFromPrefab(go, out var c));
            Assert.AreEqual(SpawnClearance.ShapeKind.Capsule, c.Kind);
            Assert.AreEqual(0.58f, c.Radius, 1e-4f);
            // Height with skin = 2.16, so the lowest point is 1.08 below the pivot.
            Assert.AreEqual(-1.08f, c.BottomOffset(Quaternion.identity), 1e-4f);
        }

        [Test]
        public void CharacterController_IsPreferredOverOtherColliders()
        {
            var go = Make("Player");
            var box = go.AddComponent<BoxCollider>();
            box.size = Vector3.one * 5f;
            go.AddComponent<CharacterController>();

            Assert.IsTrue(SpawnClearance.TryFromPrefab(go, out var c));
            Assert.AreEqual(SpawnClearance.ShapeKind.Capsule, c.Kind);
        }

        [Test]
        public void CapsuleCollider_AlongX_IsScaledAndLaidFlat()
        {
            var go = Make("Log");
            go.transform.localScale = new Vector3(2f, 1f, 1f);
            var capsule = go.AddComponent<CapsuleCollider>();
            capsule.direction = 0; // X
            capsule.radius = 0.25f;
            capsule.height = 2f;

            Assert.IsTrue(SpawnClearance.TryFromPrefab(go, out var c));
            Assert.AreEqual(0.25f, c.Radius, 1e-4f);
            // Height 2 * scale 2 = 4: end-sphere centers at x = +-(2 - 0.25).
            Assert.AreEqual(1.75f, Mathf.Max(Mathf.Abs(c.PointA.x), Mathf.Abs(c.PointB.x)), 1e-4f);
            Assert.AreEqual(-0.25f, c.BottomOffset(Quaternion.identity), 1e-4f);
        }

        [Test]
        public void ChildBoxCollider_UsesChildOffset()
        {
            var root = Make("Crate");
            var child = new GameObject("Body");
            child.transform.SetParent(root.transform, false);
            child.transform.localPosition = new Vector3(0f, 1f, 0f);
            var box = child.AddComponent<BoxCollider>();
            box.size = new Vector3(1f, 2f, 1f);

            Assert.IsTrue(SpawnClearance.TryFromPrefab(root, out var c));
            Assert.AreEqual(SpawnClearance.ShapeKind.Box, c.Kind);
            Assert.AreEqual(0f, c.BottomOffset(Quaternion.identity), 1e-4f, "Box bottom should sit at the root pivot");
        }

        [Test]
        public void TriggersAndMissingColliders_AreNotUsed()
        {
            var go = Make("Ghost");
            go.AddComponent<SphereCollider>().isTrigger = true;
            Assert.IsFalse(SpawnClearance.TryFromPrefab(go, out _));
            Assert.IsFalse(SpawnClearance.TryFromPrefab(null, out _));
        }

        [Test]
        public void Padding_GrowsTheShape()
        {
            var c = SpawnClearance.UprightCapsule(Vector3.zero, 0.5f, 2f, "test").Padded(0.25f);
            Assert.AreEqual(0.75f, c.Radius, 1e-4f);
            Assert.AreEqual(-1.25f, c.BottomOffset(Quaternion.identity), 1e-4f);
        }

        [Test]
        public void Overlaps_DetectsAColliderInTheWay()
        {
            var wall = Make("Wall");
            wall.AddComponent<BoxCollider>();
            wall.transform.position = new Vector3(0f, 1f, 0f);
            Physics.SyncTransforms();

            var shape = SpawnClearance.UprightCapsule(Vector3.zero, 0.5f, 2f, "test");
            Assert.IsTrue(shape.Overlaps(new Vector3(0f, 1f, 0f), Quaternion.identity, ~0, 0.05f), "inside the wall");
            Assert.IsFalse(shape.Overlaps(new Vector3(5f, 1f, 0f), Quaternion.identity, ~0, 0.05f), "well clear of it");
        }
    }
}
