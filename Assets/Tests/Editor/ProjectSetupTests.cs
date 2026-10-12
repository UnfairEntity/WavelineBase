using System.Collections.Generic;
using System.Linq;
using Network.Spawning;
using NUnit.Framework;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using NetcodeManager = Unity.Netcode.NetworkManager;

namespace WavelineBase.Tests
{
    /// <summary>
    /// Checks the project's real assets - build settings, the Network prefab, the networked player,
    /// the gameplay scene's spawn points and the menu layout - so a broken reference, a renamed UI
    /// element or a spawn point buried in a wall fails here instead of at runtime.
    /// </summary>
    public class ProjectSetupTests
    {
        // ---------------- Helpers ----------------

        private static IEnumerable<T> PrefabsWith<T>() where T : Component
        {
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" }))
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                if (prefab != null && prefab.TryGetComponent(out T component)) yield return component;
            }
        }

        private static Network.NetworkManager NetworkPrefab()
        {
            var all = PrefabsWith<Network.NetworkManager>().ToList();
            Assert.AreEqual(1, all.Count, "Expected exactly one prefab with Network.NetworkManager");
            return all[0];
        }

        private static SerializedProperty Field(Object target, string name)
        {
            var property = new SerializedObject(target).FindProperty(name);
            Assert.IsNotNull(property, $"{target.GetType().Name} has no serialized field '{name}'");
            return property;
        }

        private static string[] EnabledBuildScenes() =>
            EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();

        /// <summary>Opens a scene additively for inspection and closes it again afterwards
        /// (left alone if it was already open in the editor).</summary>
        private static void WithScene(string path, System.Action<Scene> inspect)
        {
            var existing = SceneManager.GetSceneByPath(path);
            var wasOpen = existing.IsValid() && existing.isLoaded;
            var scene = wasOpen ? existing : EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            try
            {
                Physics.SyncTransforms();
                inspect(scene);
            }
            finally
            {
                if (!wasOpen) EditorSceneManager.CloseScene(scene, true);
            }
        }

        // ---------------- Build settings ----------------

        [Test]
        public void BuildSettings_StartWithBootstrap_AndEverySceneExists()
        {
            var scenes = EnabledBuildScenes();
            Assert.IsNotEmpty(scenes, "No enabled scenes in Build Settings");
            StringAssert.EndsWith("Bootstrap.unity", scenes[0], "Bootstrap must be the first enabled scene");
            foreach (var path in scenes)
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<SceneAsset>(path), $"Build Settings lists a missing scene: {path}");
        }

        [Test]
        public void Bootstrapper_HasNoEmptyManagerSlots()
        {
            WithScene(EnabledBuildScenes()[0], scene =>
            {
                var bootstrapper = scene.GetRootGameObjects()
                    .Select(g => g.GetComponentInChildren<Core.Bootstrap.Bootstrapper>(true))
                    .FirstOrDefault(b => b != null);
                Assert.IsNotNull(bootstrapper, "No Bootstrapper in the bootstrap scene");

                var prefabs = Field(bootstrapper, "managerPrefabs");
                Assert.Greater(prefabs.arraySize, 0, "Bootstrapper has no manager prefabs");
                for (var i = 0; i < prefabs.arraySize; i++)
                    Assert.IsNotNull(prefabs.GetArrayElementAtIndex(i).objectReferenceValue, $"Manager prefab slot {i} is empty");

                var first = Field(bootstrapper, "firstSceneAfterBoot").stringValue;
                Assert.IsTrue(EnabledBuildScenes().Any(p => System.IO.Path.GetFileNameWithoutExtension(p) == first),
                              $"First Scene After Boot '{first}' isn't an enabled scene in Build Settings");
            });
        }

        // ---------------- Networking setup ----------------

        [Test]
        public void NetworkPrefab_HasNetcodeConfiguredForSessions()
        {
            var network = NetworkPrefab();
            var netcode = network.GetComponent<NetcodeManager>();
            Assert.IsNotNull(netcode, "Network prefab needs Unity.Netcode.NetworkManager");
            Assert.IsNotNull(netcode.NetworkConfig.NetworkTransport, "Netcode's Network Transport isn't assigned");
            Assert.IsTrue(netcode.NetworkConfig.EnableSceneManagement, "Enable Scene Management must be on (level transitions)");
            Assert.IsNotNull(network.GetComponent<SpawnManager>(), "Network prefab needs a SpawnManager");

            var gameplayScene = Field(network, "gameplaySceneName").stringValue;
            Assert.IsTrue(EnabledBuildScenes().Any(p => System.IO.Path.GetFileNameWithoutExtension(p) == gameplayScene),
                          $"Gameplay Scene Name '{gameplayScene}' isn't an enabled scene in Build Settings");
        }

        [Test]
        public void NetworkPlayer_IsSpawnableAndOwnerDriven()
        {
            var spawner = NetworkPrefab().GetComponent<SpawnManager>();
            var player = spawner.ResolvePlayerPrefab();
            Assert.IsNotNull(player, "No player prefab (SpawnManager's or Netcode's Player Prefab)");
            Assert.IsNotNull(player.GetComponent<NetworkObject>(), $"{player.name} needs a NetworkObject");

            var transform = player.GetComponent<NetworkTransform>();
            Assert.IsNotNull(transform, $"{player.name} needs a NetworkTransform so movement is replicated");
            Assert.AreEqual(NetworkTransform.AuthorityModes.Owner, transform.AuthorityMode,
                            "NetworkTransform should use Owner authority (each player moves itself)");
            Assert.IsNotNull(player.GetComponent<Player.NetworkPlayerOwnership>(),
                             $"{player.name} needs NetworkPlayerOwnership so only the owner reads input");
            Assert.IsTrue(SpawnClearance.TryFromPrefab(player, out _),
                          $"{player.name} has no CharacterController/Capsule/Sphere/Box collider for spawn clearance");
        }

        [Test]
        public void GameplayScene_HasEnoughClearGroundedSpawnPoints()
        {
            var network = NetworkPrefab();
            var spawner = network.GetComponent<SpawnManager>();
            var maxPlayers = Field(network, "maxPlayers").intValue;
            var sceneName = Field(network, "gameplaySceneName").stringValue;
            var path = EnabledBuildScenes().First(p => System.IO.Path.GetFileNameWithoutExtension(p) == sceneName);
            var blocking = Field(spawner, "blockingLayers").intValue;
            var skin = Field(spawner, "floorSkin").floatValue;

            WithScene(path, scene =>
            {
                var points = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<SpawnPoint>(true)).ToList();
                Assert.GreaterOrEqual(points.Count, maxPlayers,
                                      $"'{sceneName}' has {points.Count} SpawnPoints for up to {maxPlayers} players");

                var clearance = spawner.GetClearance();
                var positions = new List<Vector3>();
                foreach (var point in points)
                {
                    var grounded = spawner.TryResolveSpawnPosition(point, out var position);
                    Assert.IsTrue(grounded, $"{point.name}: snapping is on but there's no ground below it");
                    Assert.IsFalse(clearance.Overlaps(position, point.transform.rotation, blocking, skin),
                                   $"{point.name}: a player spawned here would overlap something");
                    positions.Add(position);
                }

                var minGap = clearance.FootprintRadius * 2f;
                for (var a = 0; a < positions.Count; a++)
                    for (var b = a + 1; b < positions.Count; b++)
                        Assert.GreaterOrEqual(Vector3.Distance(positions[a], positions[b]), minGap,
                                              $"{points[a].name} and {points[b].name} are too close to use at the same time");
            });
        }

        // ---------------- Menu layout ----------------

        [Test]
        public void MainMenu_HasEveryElementMenuManagerLooksFor()
        {
            var guid = AssetDatabase.FindAssets("MainMenu t:VisualTreeAsset", new[] { "Assets" }).FirstOrDefault();
            Assert.IsNotNull(guid, "MainMenu.uxml not found");
            var root = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(AssetDatabase.GUIDToAssetPath(guid)).CloneTree();

            void Expect<T>(string name) where T : VisualElement =>
                Assert.IsNotNull(root.Q<T>(name), $"MainMenu.uxml has no {typeof(T).Name} named '{name}'");

            foreach (var name in new[] { "MainMenuPanel", "SettingsPanel", "PlayPanel", "LobbyPanel",
                                         "AudioSubpanel", "GraphicsSubpanel", "LobbiesSubpanel", "NewLobbySubpanel" })
                Expect<VisualElement>(name);

            foreach (var name in new[] { "PlayButton", "SettingsButton", "QuitButton", "SoloButton", "LobbiesButton",
                                         "NewLobbyButton", "CreateLobbyButton", "StartGameButton", "AudioButton",
                                         "GraphicsButton", "RefreshSessionsButton" })
                Expect<Button>(name);

            Expect<ScrollView>("SessionList");
            Expect<TextField>("LobbyName");
            Expect<TextField>("LobbyPassword");
            Expect<Label>("LobbyNameText");
            Expect<Label>("StatusText");
            Expect<Toggle>("LockOnStartToggle");
            foreach (var name in new[] { "MasterVolume", "MusicVolume", "SFXVolume", "UIVolume", "VSyncFrames", "AntiAliasingQuality" })
                Expect<SliderInt>(name);
            Expect<DropdownField>("Resolution");
            Expect<DropdownField>("DisplayMode");

            Assert.IsNotNull(root.Q<VisualElement>("LobbyPanel").Q<VisualElement>("Players"), "LobbyPanel has no 'Players' container");
            foreach (var panel in new[] { "SettingsPanel", "PlayPanel", "LobbyPanel" })
                Assert.IsNotNull(root.Q<VisualElement>(panel).Q<Button>("BackButton"), $"{panel} has no BackButton");
        }

        // ---------------- Small rules ----------------

        [TestCase(null, true)]
        [TestCase("", true)]
        [TestCase("short", false)]
        [TestCase("12345678", true)]
        public void SessionPasswords_FollowTheServiceRules(string password, bool valid)
        {
            Assert.AreEqual(valid, Network.NetworkManager.IsValidPassword(password, out _));
        }

        [TestCase("Player2", "Player2")]
        [TestCase("host client!", "hostclient")]
        [TestCase("a_b-c", "a_b-c")]
        [TestCase("!!!", null)]
        public void AuthProfiles_KeepOnlyAllowedCharacters(string raw, string expected)
        {
            Assert.AreEqual(expected, Core.Diagnostics.InstanceIdentity.Sanitize(raw));
        }

        [Test]
        public void AuthProfiles_AreCappedAt30Characters()
        {
            Assert.AreEqual(30, Core.Diagnostics.InstanceIdentity.Sanitize(new string('p', 40)).Length);
        }

        [Test]
        public void SessionPasswords_LongerThan64_AreRejected()
        {
            Assert.IsFalse(Network.NetworkManager.IsValidPassword(new string('x', 65), out var error));
            Assert.IsNotEmpty(error);
        }
    }
}
