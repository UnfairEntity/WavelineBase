using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;
using NetcodeManager = Unity.Netcode.NetworkManager;
using Random = System.Random;

namespace Network.Spawning
{
    public enum SpawnMode
    {
        /// <summary>Each player gets a slot (by join order) and always uses that slot's point.</summary>
        Fixed,
        /// <summary>Any free point, picked at random each time.</summary>
        Random,
        /// <summary>Random, but no point is reused until every point has been used once.</summary>
        RandomUnique
    }

    /// <summary>
    /// PURPOSE: Server-authoritative player spawning. The server picks a SpawnPoint, makes sure
    ///          it's free (reservations + a physics capsule check), instantiates the player there
    ///          and spawns it as that client's player object, so every peer receives the same
    ///          starting position in the spawn message (no teleport afterwards).
    /// Lives on the Network prefab next to NetworkManager, which calls it once a client has
    /// finished loading the level (see NetworkManager.GameFlow.cs). Call it yourself for respawns.
    /// The clearance checked at each point is read from the player prefab's collider by default
    /// (see SpawnClearance), and SpawnPoint gizmos draw that same shape.
    /// PUBLIC API (server only): SpawnPlayer, SpawnPlayers, RespawnPlayer, TryGetSpawnPose,
    ///                           ForgetClient, ResetState; Mode / SpawnGroup can be changed at runtime.
    ///            (any time):    GetClearance, ResolvePlayerPrefab
    /// </summary>
    public class SpawnManager : MonoBehaviour
    {
        [Header("Player")]
        [Tooltip("Networked player prefab (needs a NetworkObject). Empty = the Player Prefab set on Netcode's NetworkManager.")]
        [SerializeField] private GameObject playerPrefab;

        [Header("Choosing points")]
        [SerializeField] private SpawnMode mode = SpawnMode.RandomUnique;
        [Tooltip("Only use SpawnPoints in this group (empty = all points).")]
        [SerializeField] private string spawnGroup = "";

        [Header("Overlap prevention")]
        [Tooltip("Skip points where the player's collider would overlap something.")]
        [SerializeField] private bool preventOverlap = true;
        [Tooltip("Layers that block a spawn point (triggers are ignored).")]
        [SerializeField] private LayerMask blockingLayers = ~0;
        [Tooltip("Use the player prefab's own collider (CharacterController, Capsule, Sphere or Box) as the clearance " +
                 "shape. Falls back to the manual values below if the prefab has none of those.")]
        [SerializeField] private bool autoClearanceFromPlayerPrefab = true;
        [Tooltip("Extra room added on every side of the clearance shape.")]
        [SerializeField] private float clearancePadding;
        [Tooltip("Raises the test volume so resting on the floor doesn't count as blocked.")]
        [SerializeField] private float floorSkin = 0.05f;

        [Header("Manual clearance (auto off, or no supported collider on the prefab)")]
        [Tooltip("Capsule radius checked around a point.")]
        [SerializeField] private float clearanceRadius = 0.5f;
        [Tooltip("Capsule height checked around a point.")]
        [SerializeField] private float clearanceHeight = 1f;
        [Tooltip("Capsule center relative to the point. (0,0,0) suits a pivot at the collider's center; " +
                 "use (0, height/2, 0) for a pivot at the feet.")]
        [SerializeField] private Vector3 clearanceCenterOffset = Vector3.zero;
        [Tooltip("A chosen position stays reserved this long, so players spawned in the same moment never share it.")]
        [SerializeField] private float reservationSeconds = 1.5f;
        [Tooltip("If every point is blocked, search this many rings of positions around them for a free spot.")]
        [SerializeField] private int fallbackRings = 3;

        [Header("Randomness")]
        [Tooltip("Use a fixed seed so Random/RandomUnique picks repeat between runs (handy for testing).")]
        [SerializeField] private bool useFixedSeed;
        [SerializeField] private int seed = 12345;

        private readonly Dictionary<ulong, int> _fixedSlots = new();
        private readonly List<SpawnPoint> _uniqueBag = new();
        private readonly List<(Vector3 position, float until)> _reservations = new();
        private Random _random;
        private NetcodeManager _netcode;
        private SpawnClearance? _cachedClearance; // play mode only; edit mode recomputes so prefab edits show at once
        private GameObject _cachedClearancePrefab;

        /// <summary>The SpawnManager in use while playing (for SpawnPoint gizmos and tools).</summary>
        public static SpawnManager Active { get; private set; }

        public SpawnMode Mode { get => mode; set => mode = value; }
        public string SpawnGroup { get => spawnGroup; set { spawnGroup = value ?? ""; _uniqueBag.Clear(); } }

        private void OnEnable()
        {
            if (Active == null) Active = this;
        }

        private void OnDisable()
        {
            if (Active == this) Active = null;
        }

        private NetcodeManager Netcode
        {
            get
            {
                // Explicit checks: '??' skips Unity's null handling for destroyed/missing components.
                if (_netcode != null) return _netcode;
                _netcode = GetComponent<NetcodeManager>();
                if (_netcode == null) _netcode = NetcodeManager.Singleton;
                return _netcode;
            }
        }
        private Random Rng => _random ??= useFixedSeed ? new Random(seed) : new Random();

        // ---------------- Spawning (server only) ----------------

        /// <summary>Spawns the player object for a client at a free spawn point. Returns the existing
        /// player object instead if the client already has one.</summary>
        public NetworkObject SpawnPlayer(ulong clientId)
        {
            var netcode = Netcode;
            if (netcode == null || !netcode.IsServer)
            {
                Debug.LogError("[SpawnManager] Players can only be spawned by the server/host.", this);
                return null;
            }

            if (!netcode.ConnectedClients.TryGetValue(clientId, out var client))
            {
                Debug.LogWarning($"[SpawnManager] Client {clientId} isn't connected; not spawning.", this);
                return null;
            }
            if (client.PlayerObject != null) return client.PlayerObject;

            var prefab = ResolvePlayerPrefab();
            if (prefab == null || prefab.GetComponent<NetworkObject>() == null)
            {
                Debug.LogError("[SpawnManager] No player prefab with a NetworkObject. Set one on SpawnManager or on Netcode's NetworkManager.", this);
                return null;
            }

            TryGetSpawnPose(clientId, out var pose);

            // Instantiate at the final pose *before* spawning: the spawn message carries this
            // transform, so every peer creates the player in the same place.
            var instance = Instantiate(prefab, pose.position, pose.rotation);
            var networkObject = instance.GetComponent<NetworkObject>();
            networkObject.SpawnAsPlayerObject(clientId, destroyWithScene: true);
            Debug.Log($"[SpawnManager] Spawned player for client {clientId} at {pose.position}.", this);
            return networkObject;
        }

        /// <summary>Spawns several clients in one go. Reservations keep them on separate points.</summary>
        public void SpawnPlayers(IEnumerable<ulong> clientIds)
        {
            foreach (var clientId in clientIds) SpawnPlayer(clientId);
        }

        /// <summary>Despawns the client's current player object (if any) and spawns a fresh one at a
        /// newly chosen point. Works whatever the player's NetworkTransform authority is.</summary>
        public NetworkObject RespawnPlayer(ulong clientId)
        {
            var netcode = Netcode;
            if (netcode == null || !netcode.IsServer)
            {
                Debug.LogError("[SpawnManager] Players can only be respawned by the server/host.", this);
                return null;
            }

            if (netcode.ConnectedClients.TryGetValue(clientId, out var client) && client.PlayerObject != null)
                client.PlayerObject.Despawn(destroy: true);

            return SpawnPlayer(clientId);
        }

        /// <summary>Forget per-client data (Fixed slot) when a client leaves.</summary>
        public void ForgetClient(ulong clientId) => _fixedSlots.Remove(clientId);

        /// <summary>Clears slots, the RandomUnique bag and reservations (e.g. when a session ends).</summary>
        public void ResetState()
        {
            _fixedSlots.Clear();
            _uniqueBag.Clear();
            _reservations.Clear();
            _random = null;
            _cachedClearance = null;
        }

        // ---------------- Clearance shape ----------------

        /// <summary>The prefab that will be spawned: SpawnManager's own, else Netcode's Player Prefab.</summary>
        public GameObject ResolvePlayerPrefab()
        {
            if (playerPrefab != null) return playerPrefab;
            var netcode = Netcode;
            return netcode != null && netcode.NetworkConfig != null ? netcode.NetworkConfig.PlayerPrefab : null;
        }

        /// <summary>
        /// The volume checked at each spawn point (padding included): the player prefab's collider when
        /// "Auto Clearance From Player Prefab" is on and one is found, otherwise the manual capsule.
        /// SpawnPoint gizmos draw this same shape.
        /// </summary>
        public SpawnClearance GetClearance()
        {
            var prefab = autoClearanceFromPlayerPrefab ? ResolvePlayerPrefab() : null;
            if (Application.isPlaying && _cachedClearance.HasValue && _cachedClearancePrefab == prefab)
                return _cachedClearance.Value;

            SpawnClearance clearance;
            if (autoClearanceFromPlayerPrefab && SpawnClearance.TryFromPrefab(prefab, out var fromPrefab))
            {
                clearance = fromPrefab;
            }
            else
            {
                clearance = SpawnClearance.UprightCapsule(clearanceCenterOffset, clearanceRadius, clearanceHeight, "manual settings");
                if (autoClearanceFromPlayerPrefab && Application.isPlaying)
                {
                    Debug.LogWarning($"[SpawnManager] Player prefab '{(prefab != null ? prefab.name : "none")}' has no " +
                                     "CharacterController/Capsule/Sphere/Box collider; using the manual clearance values.", this);
                }
            }

            clearance = clearance.Padded(clearancePadding);
            if (Application.isPlaying)
            {
                _cachedClearance = clearance;
                _cachedClearancePrefab = prefab;
                Debug.Log($"[SpawnManager] Spawn clearance: {clearance.Kind} from {clearance.Source}" +
                          $"{(clearancePadding > 0f ? $" + {clearancePadding} padding" : "")}.", this);
            }
            return clearance;
        }

#if UNITY_EDITOR
        private static SpawnManager _editorPrefabInstance;
        private static bool _editorSearchDone;

        /// <summary>
        /// For SpawnPoint gizmos: the active SpawnManager while playing; in edit mode, the SpawnManager
        /// on a prefab in the project (normally the Network prefab), since it isn't in level scenes.
        /// </summary>
        public static SpawnManager FindForGizmos()
        {
            if (Active != null) return Active;
            if (_editorSearchDone) return _editorPrefabInstance;

            _editorSearchDone = true;
            foreach (var guid in UnityEditor.AssetDatabase.FindAssets("t:Prefab"))
            {
                var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (asset == null || !asset.TryGetComponent(out SpawnManager manager)) continue;
                _editorPrefabInstance = manager;
                break;
            }
            return _editorPrefabInstance;
        }

        [UnityEditor.InitializeOnLoadMethod]
        private static void ResetEditorSearchOnProjectChange()
        {
            UnityEditor.EditorApplication.projectChanged += () =>
            {
                _editorSearchDone = false;
                _editorPrefabInstance = null;
            };
        }
#endif

        // ---------------- Choosing a point ----------------

        /// <summary>Picks (and reserves) a spawn pose for a client. Returns false if it had to fall back
        /// to a blocked point or to the world origin.</summary>
        public bool TryGetSpawnPose(ulong clientId, out Pose pose)
        {
            var points = GetCandidatePoints();
            if (points.Count == 0)
            {
                Debug.LogWarning($"[SpawnManager] No enabled SpawnPoints{(string.IsNullOrEmpty(spawnGroup) ? "" : $" in group '{spawnGroup}'")}; " +
                                 "spawning at the world origin.", this);
                pose = new Pose(Vector3.zero, Quaternion.identity);
                return false;
            }

            var ordered = OrderForMode(clientId, points);

            // 1. The first free point in mode order.
            var clearance = GetClearance();
            foreach (var point in ordered)
            {
                if (!IsClear(point.transform.position, point.transform.rotation, clearance)) continue;
                Choose(point, point.transform.position);
                pose = point.Pose;
                return true;
            }

            // 2. Every point is blocked/reserved: look for a free spot around them.
            foreach (var point in ordered)
            {
                if (!TryFindClearNear(point.transform.position, point.transform.rotation, clearance, out var position)) continue;
                Choose(point, position);
                pose = new Pose(position, point.transform.rotation);
                Debug.LogWarning($"[SpawnManager] All spawn points blocked; using a free spot near '{point.name}'.", this);
                return true;
            }

            // 3. Nothing free at all: overlap rather than not spawning.
            var fallback = ordered[0];
            Choose(fallback, fallback.transform.position);
            pose = fallback.Pose;
            Debug.LogWarning($"[SpawnManager] No free spawn position found; spawning at blocked point '{fallback.name}'. " +
                             "Add more SpawnPoints or check Blocking Layers.", this);
            return false;
        }

        private List<SpawnPoint> GetCandidatePoints()
        {
            return SpawnPoint.All
                .Where(p => p != null && p.isActiveAndEnabled &&
                            (string.IsNullOrEmpty(spawnGroup) || p.Group == spawnGroup))
                .OrderBy(p => p.Order)
                .ThenBy(p => p.transform.GetSiblingIndex())
                .ThenBy(p => p.name, StringComparer.Ordinal)
                .ToList();
        }

        private List<SpawnPoint> OrderForMode(ulong clientId, List<SpawnPoint> points)
        {
            switch (mode)
            {
                case SpawnMode.Fixed:
                {
                    // Start at this client's slot, then try the following points in order.
                    var start = GetFixedSlot(clientId) % points.Count;
                    var ordered = new List<SpawnPoint>(points.Count);
                    for (var i = 0; i < points.Count; i++) ordered.Add(points[(start + i) % points.Count]);
                    return ordered;
                }
                case SpawnMode.RandomUnique:
                {
                    // Points not yet used this cycle come first (in bag order); used ones after, shuffled.
                    _uniqueBag.RemoveAll(p => p == null || !points.Contains(p));
                    if (_uniqueBag.Count == 0) _uniqueBag.AddRange(Shuffled(points));
                    var rest = Shuffled(points.Where(p => !_uniqueBag.Contains(p)));
                    return _uniqueBag.Concat(rest).ToList();
                }
                default:
                    return Shuffled(points);
            }
        }

        private int GetFixedSlot(ulong clientId)
        {
            if (_fixedSlots.TryGetValue(clientId, out var slot)) return slot;
            slot = 0;
            while (_fixedSlots.ContainsValue(slot)) slot++;
            _fixedSlots[clientId] = slot;
            return slot;
        }

        private List<SpawnPoint> Shuffled(IEnumerable<SpawnPoint> source)
        {
            var list = source.ToList();
            for (var i = list.Count - 1; i > 0; i--)
            {
                var j = Rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
            return list;
        }

        private void Choose(SpawnPoint point, Vector3 position)
        {
            _uniqueBag.Remove(point);
            _reservations.Add((position, Time.realtimeSinceStartup + reservationSeconds));
        }

        // ---------------- Clearance ----------------

        private bool IsClear(Vector3 position, Quaternion rotation, SpawnClearance clearance)
        {
            var now = Time.realtimeSinceStartup;
            _reservations.RemoveAll(r => r.until <= now);

            // Reserved spots: keep a full body-width apart.
            var minDistance = clearance.FootprintRadius * 2f;
            foreach (var reservation in _reservations)
            {
                if ((reservation.position - position).sqrMagnitude < minDistance * minDistance) return false;
            }

            return !preventOverlap || !clearance.Overlaps(position, rotation, blockingLayers, floorSkin);
        }

        private bool TryFindClearNear(Vector3 origin, Quaternion rotation, SpawnClearance clearance, out Vector3 position)
        {
            const int directions = 8;
            var step = clearance.FootprintRadius * 2.2f;
            for (var ring = 1; ring <= fallbackRings; ring++)
            {
                for (var i = 0; i < directions; i++)
                {
                    var angle = (Mathf.PI * 2f / directions) * i;
                    var candidate = origin + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * (step * ring);
                    if (!IsClear(candidate, rotation, clearance)) continue;
                    position = candidate;
                    return true;
                }
            }
            position = origin;
            return false;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            _cachedClearance = null;
            clearancePadding = Mathf.Max(0f, clearancePadding);
            clearanceRadius = Mathf.Max(0.01f, clearanceRadius);
            clearanceHeight = Mathf.Max(clearanceRadius * 2f, clearanceHeight);
            reservationSeconds = Mathf.Max(0f, reservationSeconds);
            fallbackRings = Mathf.Max(0, fallbackRings);
        }
#endif
    }
}
