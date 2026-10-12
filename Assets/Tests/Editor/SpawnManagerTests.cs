using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Network.Spawning;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace WavelineBase.Tests
{
    /// <summary>
    /// SpawnManager's choice of spawn pose: modes, reservations, blocked points, ground snap and
    /// the fallback search. Runs without Netcode - TryGetSpawnPose only needs points and physics.
    /// The manager uses its manual clearance: an upright capsule, radius 0.5, height 2, pivot at center.
    /// </summary>
    public class SpawnManagerTests
    {
        private const float Skin = 0.05f;
        private readonly List<GameObject> _created = new();
        private SpawnManager _manager;

        [SetUp]
        public void SetUp()
        {
            ResetPointRegistry();

            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane); // 10 x 10, collider at y = 0
            ground.name = "Ground";
            ground.transform.localScale = new Vector3(3f, 1f, 3f);       // 30 x 30
            _created.Add(ground);

            var managerObject = new GameObject("SpawnManager");
            _created.Add(managerObject);
            _manager = managerObject.AddComponent<SpawnManager>();
            Configure(_manager, so =>
            {
                so.FindProperty("autoClearanceFromPlayerPrefab").boolValue = false;
                so.FindProperty("clearanceRadius").floatValue = 0.5f;
                so.FindProperty("clearanceHeight").floatValue = 2f;
                so.FindProperty("clearanceCenterOffset").vector3Value = Vector3.zero;
                so.FindProperty("floorSkin").floatValue = Skin;
                so.FindProperty("useFixedSeed").boolValue = true;
                so.FindProperty("snapToGround").boolValue = true;
            });
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _created) if (go != null) Object.DestroyImmediate(go);
            _created.Clear();
            ResetPointRegistry();
            Physics.SyncTransforms();
        }

        // ---------------- Helpers ----------------

        private static void Configure(Object target, System.Action<SerializedObject> edit)
        {
            var so = new SerializedObject(target);
            edit(so);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>SpawnPoints register in OnEnable, which Unity doesn't call for plain scripts in
        /// edit mode, so tests invoke it (and the registry reset) directly.</summary>
        private static void Invoke(object target, string method, BindingFlags flags) =>
            typeof(SpawnPoint).GetMethod(method, flags)!.Invoke(target, null);

        private static void ResetPointRegistry() =>
            Invoke(null, "ResetRegistry", BindingFlags.NonPublic | BindingFlags.Static);

        private SpawnPoint Point(Vector3 position, int order = 0, SpawnPoint.GroundSnapMode snap = SpawnPoint.GroundSnapMode.UseSpawnManager)
        {
            var go = new GameObject($"SpawnPoint {order}");
            go.transform.position = position;
            _created.Add(go);
            var point = go.AddComponent<SpawnPoint>();
            Configure(point, so =>
            {
                so.FindProperty("order").intValue = order;
                so.FindProperty("groundSnap").enumValueIndex = (int)snap;
            });
            Invoke(point, "OnEnable", BindingFlags.NonPublic | BindingFlags.Instance);
            return point;
        }

        private GameObject Blocker(Vector3 position, float size = 1.5f)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "Blocker";
            cube.transform.position = position;
            cube.transform.localScale = Vector3.one * size;
            _created.Add(cube);
            return cube;
        }

        private Pose Pick(ulong clientId)
        {
            Physics.SyncTransforms();
            _manager.TryGetSpawnPose(clientId, out var pose);
            return pose;
        }

        private static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));

        // ---------------- Tests ----------------

        [Test]
        public void GroundSnap_RestsTheShapeOnTheFloor()
        {
            Point(new Vector3(0f, 3f, 0f)); // floating 3 m up
            var pose = Pick(0);
            // Capsule height 2 with pivot at center: lowest point is 1 below the pivot.
            Assert.AreEqual(1f + Skin, pose.position.y, 0.01f);
        }

        [Test]
        public void DontSnap_UsesThePointExactly()
        {
            var point = Point(new Vector3(0f, 3f, 0f), snap: SpawnPoint.GroundSnapMode.DontSnap);
            var pose = Pick(0);
            Assert.AreEqual(point.transform.position, pose.position);
        }

        [Test]
        public void NoGroundBelow_FallsBackToThePoint()
        {
            var point = Point(new Vector3(100f, 3f, 0f)); // off the edge of the ground plane
            var pose = Pick(0);
            Assert.AreEqual(point.transform.position, pose.position);
        }

        [Test]
        public void RandomUnique_GivesFourPlayersFourDifferentPoints()
        {
            _manager.Mode = SpawnMode.RandomUnique;
            for (var i = 0; i < 4; i++) Point(new Vector3(i * 4f, 1f, 0f), i);

            var picks = Enumerable.Range(0, 4).Select(i => Pick((ulong)i).position).ToList();
            for (var a = 0; a < picks.Count; a++)
                for (var b = a + 1; b < picks.Count; b++)
                    Assert.Greater(Flat(picks[a], picks[b]), 1f, $"players {a} and {b} share a spot");
        }

        [Test]
        public void Random_SameMomentSpawns_NeverShareAPoint()
        {
            _manager.Mode = SpawnMode.Random;
            for (var i = 0; i < 3; i++) Point(new Vector3(i * 4f, 1f, 0f), i);

            // Reservations must keep these apart even though no player objects exist yet.
            var picks = Enumerable.Range(0, 3).Select(i => Pick((ulong)i).position).ToList();
            Assert.AreEqual(3, picks.Select(p => Mathf.RoundToInt(p.x)).Distinct().Count());
        }

        [Test]
        public void Fixed_AssignsPointsByJoinOrder()
        {
            _manager.Mode = SpawnMode.Fixed;
            var first = Point(new Vector3(0f, 1f, 0f), 0);
            var second = Point(new Vector3(5f, 1f, 0f), 1);

            Assert.Less(Flat(Pick(10).position, first.transform.position), 0.01f);
            Assert.Less(Flat(Pick(11).position, second.transform.position), 0.01f);
        }

        [Test]
        public void BlockedPoint_IsSkipped()
        {
            _manager.Mode = SpawnMode.Fixed;
            var blocked = Point(new Vector3(0f, 1f, 0f), 0);
            var free = Point(new Vector3(6f, 1f, 0f), 1);
            Blocker(blocked.transform.position); // the ground probe starts inside it, so still finds the floor

            var pose = Pick(0); // slot 0 would be the blocked point
            Assert.Less(Flat(pose.position, free.transform.position), 0.01f);
        }

        [Test]
        public void EveryPointBlocked_FindsAFreeSpotNearby()
        {
            var only = Point(new Vector3(0f, 1f, 0f));
            Blocker(only.transform.position);

            var pose = Pick(0);
            Assert.Greater(Flat(pose.position, only.transform.position), 0.5f, "should have moved off the blocked point");
            var shape = SpawnClearance.UprightCapsule(Vector3.zero, 0.5f, 2f, "test");
            Assert.IsFalse(shape.Overlaps(pose.position, pose.rotation, ~0, Skin), "the fallback spot overlaps something");
        }

        [Test]
        public void SpawnFacesThePointsDirection()
        {
            var point = Point(new Vector3(0f, 1f, 0f));
            point.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
            var pose = Pick(0);
            Assert.Less(Quaternion.Angle(pose.rotation, point.transform.rotation), 0.1f);
        }
    }
}
