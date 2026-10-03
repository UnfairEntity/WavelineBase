using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Network.Spawning;
using Unity.Netcode;
using Unity.Services.Multiplayer;
using UnityEngine;
using UnityEngine.SceneManagement;
using NetcodeManager = Unity.Netcode.NetworkManager;

namespace Network
{
    /// <summary>Where a session is in its life. Published to the lobby service as a session property.</summary>
    public enum SessionGameState
    {
        /// <summary>Players are in the lobby; anyone can join.</summary>
        Lobby,
        /// <summary>The host has started the game and everyone is loading the level.</summary>
        Loading,
        /// <summary>The level is running.</summary>
        InGame
    }

    // Game start / level transition, connection approval, late joins and spawning hooks.
    // Split from NetworkManager.cs to keep each file readable; it's the same component.
    public partial class NetworkManager
    {
        /// <summary>Session property holding the SessionGameState name (public, readable by browsers).</summary>
        public const string GameStatePropertyKey = "state";
        /// <summary>Session property, "1" or "0": can a new player join right now? Indexed so queries can filter on it.</summary>
        public const string JoinablePropertyKey = "joinable";
        /// <summary>Custom string property index 4 (StringIndex5) is reserved for the joinable flag.</summary>
        public const int ReservedStringPropertyIndex = 4;

        private const PropertyIndex JoinablePropertyIndex = PropertyIndex.String5;
        private const FilterField JoinableFilterField = FilterField.StringIndex5;

        [Header("Game Start")]
        [Tooltip("Scene the host loads for everyone when the game starts. Must be in Build Settings.")]
        [SerializeField] private string gameplaySceneName = "DefaultScene";
        [Tooltip("Lock the session when the host starts the game, so nobody can join the lobby during or after the transition. The lobby's toggle can override this per start.")]
        [SerializeField] private bool lockSessionOnStart = true;
        [Tooltip("Let players join after the game has started. Players connecting while the level loads are held until it finishes; late joiners spawn once they've synchronized.")]
        [SerializeField] private bool allowLateJoin;
        [Tooltip("Disconnect clients that don't finish loading within Netcode's Load Scene Time Out. If off, they spawn whenever they do finish.")]
        [SerializeField] private bool disconnectClientsThatTimeOut;

        [Header("Session Browser")]
        [Tooltip("List sessions that can't be joined right now (started without late join, or locked).")]
        [SerializeField] private bool showUnjoinableSessions;
        [Tooltip("Minimum seconds between session queries; the lobby service rate-limits queries.")]
        [SerializeField] private float minQueryIntervalSeconds = 1f;

        /// <summary>On the host: the authoritative state. On clients: mirrored from the session property.</summary>
        public SessionGameState GameState { get; private set; } = SessionGameState.Lobby;
        public bool LockSessionOnStart => lockSessionOnStart;
        public bool AllowLateJoin => allowLateJoin;
        public string GameplaySceneName => gameplaySceneName;
        public SpawnManager Spawner => _spawnManager;

        public event Action<SessionGameState> GameStateChanged;

        private NetcodeManager _netcode;
        private SpawnManager _spawnManager;
        private Action<NetcodeManager.ConnectionApprovalRequest, NetcodeManager.ConnectionApprovalResponse> _approvalCallback;
        private readonly List<NetcodeManager.ConnectionApprovalResponse> _pendingApprovals = new();
        private NetworkSceneManager _hookedSceneManager;
        private bool _lockedByStart;
        private bool _netcodeClientConnected;

        // ---------------- Setup ----------------

        private void InitializeGameFlow()
        {
            _netcode = GetComponent<NetcodeManager>();
            if (_netcode == null) _netcode = NetcodeManager.Singleton;

            _spawnManager = GetComponent<SpawnManager>();
            if (_spawnManager == null)
            {
                _spawnManager = gameObject.AddComponent<SpawnManager>();
                LogLifecycle("No SpawnManager on the Network prefab; added one with default settings.");
            }

            if (_netcode == null) return; // EnsureNetcodeReady() reports this when a session is created/joined.

            // We spawn players ourselves (after they've loaded the level), so connection approval
            // must be on to stop Netcode auto-spawning them. It's part of Netcode's config hash, so it
            // has to be on for every peer - setting it here on startup does that.
            _netcode.NetworkConfig.ConnectionApproval = true;
            _approvalCallback = ApproveConnection;
            _netcode.ConnectionApprovalCallback = _approvalCallback;

            _netcode.OnServerStarted += OnNetcodeServerStarted;
            _netcode.OnServerStopped += OnNetcodeServerStopped;
            _netcode.OnClientConnectedCallback += OnNetcodeClientConnected;
            _netcode.OnClientDisconnectCallback += OnNetcodeClientDisconnected;
        }

        private void ShutdownGameFlow()
        {
            UnhookSceneEvents();
            if (_netcode == null) return;

            _netcode.OnServerStarted -= OnNetcodeServerStarted;
            _netcode.OnServerStopped -= OnNetcodeServerStopped;
            _netcode.OnClientConnectedCallback -= OnNetcodeClientConnected;
            _netcode.OnClientDisconnectCallback -= OnNetcodeClientDisconnected;
            if (_netcode.ConnectionApprovalCallback == _approvalCallback) _netcode.ConnectionApprovalCallback = null;
        }

        /// <summary>Re-applies our approval callback if something replaced it (with approval on and no
        /// callback, clients could never connect).</summary>
        private void EnsureApprovalCallback()
        {
            if (_netcode == null || _approvalCallback == null) return;
            _netcode.NetworkConfig.ConnectionApproval = true;
            if (_netcode.ConnectionApprovalCallback == _approvalCallback) return;

            Debug.LogWarning("[NetworkManager] Netcode's ConnectionApprovalCallback was replaced; restoring ours " +
                             "(it holds/rejects late joiners and stops Netcode auto-spawning players).", this);
            _netcode.ConnectionApprovalCallback = _approvalCallback;
        }

        // ---------------- Starting the game (host) ----------------

        /// <summary>
        /// Host only: moves everyone from the lobby into the gameplay scene. Optionally locks the
        /// session first (no lobby joins during or after the transition). Players are spawned by
        /// SpawnManager once they've finished loading. Returns false if the start was refused or failed.
        /// </summary>
        /// <param name="lockSession">Lock the session; null = use the Inspector default.</param>
        public async Task<bool> StartGame(bool? lockSession = null)
        {
            if (CurrentSession is not { IsHost: true })
            {
                Debug.LogWarning("[NetworkManager] Only the host can start the game.", this);
                return false;
            }
            if (_netcode == null || !_netcode.IsServer)
            {
                Debug.LogError("[NetworkManager] Can't start: Netcode isn't running as host for this session.", this);
                return false;
            }
            if (_netcode.SceneManager == null)
            {
                Debug.LogError("[NetworkManager] Can't start: turn on 'Enable Scene Management' on Netcode's NetworkManager.", this);
                return false;
            }
            if (GameState != SessionGameState.Lobby)
            {
                Debug.LogWarning($"[NetworkManager] Game already {GameState}; ignoring Start.", this);
                return false;
            }
            if (string.IsNullOrWhiteSpace(gameplaySceneName))
            {
                Debug.LogError("[NetworkManager] Can't start: no Gameplay Scene Name set.", this);
                return false;
            }

            var session = CurrentSession;
            var shouldLock = lockSession ?? lockSessionOnStart;

            // From here on, ApproveConnection holds or rejects anyone who connects.
            SetGameState(SessionGameState.Loading);
            LogLifecycle($"Starting game (lock: {shouldLock}, late join: {allowLateJoin}).");

            try
            {
                var host = session.AsHost();
                if (shouldLock)
                {
                    host.IsLocked = true;
                    _lockedByStart = true;
                }
                ApplyStateProperties(host);
                // Saved before the load starts, so the lobby service refuses new joins from now on.
                await host.SavePropertiesAsync();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                if (shouldLock)
                {
                    // We promised a locked session and can't guarantee it; don't start.
                    LogLifecycle("Start aborted: couldn't lock the session.");
                    await RevertToLobby(session);
                    return false;
                }
                // Without a lock the properties are only advisory (approval still guards Netcode); carry on.
            }

            if (CurrentSession != session || !_netcode.IsServer)
            {
                LogLifecycle("Start aborted: the session ended while it was being prepared.");
                SetGameState(SessionGameState.Lobby);
                return false;
            }

            var status = _netcode.SceneManager.LoadScene(gameplaySceneName, LoadSceneMode.Single);
            if (status != SceneEventProgressStatus.Started)
            {
                Debug.LogError($"[NetworkManager] Couldn't load '{gameplaySceneName}': {status}. Is it in Build Settings?", this);
                await RevertToLobby(session);
                return false;
            }

            LogLifecycle($"Loading '{gameplaySceneName}' for {_netcode.ConnectedClientsIds.Count} connected client(s).");
            return true;
        }

        private async Task RevertToLobby(ISession session)
        {
            SetGameState(SessionGameState.Lobby);
            ResolvePendingApprovals(approve: true, reason: null);
            if (CurrentSession != session || !session.IsHost) return;

            try
            {
                var host = session.AsHost();
                if (_lockedByStart) host.IsLocked = false;
                _lockedByStart = false;
                ApplyStateProperties(host);
                await host.SavePropertiesAsync();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        // ---------------- Connection approval (server) ----------------

        private void ApproveConnection(NetcodeManager.ConnectionApprovalRequest request,
                                       NetcodeManager.ConnectionApprovalResponse response)
        {
            // SpawnManager spawns players once they've loaded the level; never let Netcode do it.
            response.CreatePlayerObject = false;

            if (request.ClientNetworkId == NetcodeManager.ServerClientId)
            {
                response.Approved = true;
                return;
            }

            switch (GameState)
            {
                case SessionGameState.Lobby:
                    response.Approved = true;
                    break;

                case SessionGameState.Loading when allowLateJoin:
                    // Joining mid-transition: hold the connection until the level has loaded, then let
                    // them in as a late joiner. (Netcode's Client Connection Buffer Timeout still applies.)
                    response.Pending = true;
                    _pendingApprovals.Add(response);
                    LogLifecycle($"Holding connection from client {request.ClientNetworkId} until the level has loaded.");
                    break;

                case SessionGameState.Loading:
                    Reject(response, request.ClientNetworkId, "The game is starting.");
                    break;

                case SessionGameState.InGame when allowLateJoin:
                    response.Approved = true;
                    LogLifecycle($"Approved late joiner {request.ClientNetworkId}.");
                    break;

                default:
                    Reject(response, request.ClientNetworkId, "The game is already in progress.");
                    break;
            }
        }

        private void Reject(NetcodeManager.ConnectionApprovalResponse response, ulong clientId, string reason)
        {
            response.Approved = false;
            response.Reason = reason;
            LogLifecycle($"Rejected connection from client {clientId}: {reason}");
        }

        private void ResolvePendingApprovals(bool approve, string reason)
        {
            foreach (var response in _pendingApprovals)
            {
                response.Approved = approve;
                response.Reason = approve ? null : reason;
                response.Pending = false;
            }
            if (_pendingApprovals.Count > 0)
                LogLifecycle($"{(approve ? "Approved" : "Rejected")} {_pendingApprovals.Count} held connection(s).");
            _pendingApprovals.Clear();
        }

        // ---------------- Netcode lifecycle ----------------

        private void OnNetcodeServerStarted()
        {
            EnsureApprovalCallback();
            SetGameState(SessionGameState.Lobby);
            _spawnManager.ResetState();
            HookSceneEvents();
        }

        private void OnNetcodeServerStopped(bool wasHost)
        {
            UnhookSceneEvents();
            ResolvePendingApprovals(approve: false, reason: "The host stopped.");
            _spawnManager.ResetState();
            _lockedByStart = false;
            SetGameState(SessionGameState.Lobby);
        }

        private void OnNetcodeClientConnected(ulong clientId)
        {
            if (!_netcode.IsServer && clientId == _netcode.LocalClientId)
                _netcodeClientConnected = true;
        }

        private void OnNetcodeClientDisconnected(ulong clientId)
        {
            if (_netcode.IsServer)
            {
                if (clientId != NetcodeManager.ServerClientId) _spawnManager.ForgetClient(clientId);
                return;
            }

            // Client side: we lost our connection to the host - rejected by approval (game started,
            // no late join), host left, or a network failure. Leave the session so the UI returns to
            // the menu instead of sitting in a lobby with no connection.
            var reason = _netcode.DisconnectReason;
            var wasConnected = _netcodeClientConnected;
            _netcodeClientConnected = false;
            if (!InSession || _shuttingDown) return;
            if (!wasConnected && string.IsNullOrEmpty(reason)) return; // transient reconnects during setup

            LogLifecycle($"Disconnected from the host{(string.IsNullOrEmpty(reason) ? "" : $": {reason}")}.");
            _ = LeaveSession(string.IsNullOrEmpty(reason) ? "disconnected from host" : $"disconnected from host ({reason})");
        }

        private void HookSceneEvents()
        {
            UnhookSceneEvents();
            var sceneManager = _netcode.SceneManager;
            if (sceneManager == null)
            {
                Debug.LogError("[NetworkManager] 'Enable Scene Management' is off on Netcode's NetworkManager; " +
                               "level transitions and spawning won't work.", this);
                return;
            }

            sceneManager.OnLoadEventCompleted += OnLoadEventCompleted;
            sceneManager.OnLoadComplete += OnClientLoadComplete;
            sceneManager.OnSynchronizeComplete += OnClientSynchronizeComplete;
            _hookedSceneManager = sceneManager;
        }

        private void UnhookSceneEvents()
        {
            if (_hookedSceneManager == null) return;
            _hookedSceneManager.OnLoadEventCompleted -= OnLoadEventCompleted;
            _hookedSceneManager.OnLoadComplete -= OnClientLoadComplete;
            _hookedSceneManager.OnSynchronizeComplete -= OnClientSynchronizeComplete;
            _hookedSceneManager = null;
        }

        // ---------------- Scene events (server) ----------------

        // Everyone has loaded the level (or timed out): the game is on.
        private void OnLoadEventCompleted(string sceneName, LoadSceneMode mode,
                                          List<ulong> clientsCompleted, List<ulong> clientsTimedOut)
        {
            if (GameState != SessionGameState.Loading || sceneName != gameplaySceneName) return;

            SetGameState(SessionGameState.InGame);
            PushStateProperties();
            LogLifecycle($"'{sceneName}' loaded: {clientsCompleted.Count} ready, {clientsTimedOut.Count} timed out.");

            // Spawn everyone who finished in one batch (reservations keep them on separate points).
            // The host is included explicitly in case it isn't listed.
            var toSpawn = new List<ulong>(clientsCompleted);
            if (_netcode.IsHost && !toSpawn.Contains(NetcodeManager.ServerClientId)) toSpawn.Insert(0, NetcodeManager.ServerClientId);
            _spawnManager.SpawnPlayers(toSpawn.Where(id => _netcode.ConnectedClients.ContainsKey(id)));

            foreach (var clientId in clientsTimedOut)
            {
                if (disconnectClientsThatTimeOut)
                {
                    LogLifecycle($"Disconnecting client {clientId}: didn't finish loading in time.");
                    _netcode.DisconnectClient(clientId, "Took too long to load the level.");
                }
                else
                {
                    LogLifecycle($"Client {clientId} is still loading; it will spawn when it finishes.");
                }
            }

            // Anyone held while loading becomes a late joiner now (only held when late join is allowed).
            ResolvePendingApprovals(approve: allowLateJoin, reason: "The game is already in progress.");
        }

        // A client finished loading the level after the batch spawn (it had timed out).
        private void OnClientLoadComplete(ulong clientId, string sceneName, LoadSceneMode mode)
        {
            if (GameState != SessionGameState.InGame || sceneName != gameplaySceneName) return;
            TrySpawnLateClient(clientId, "finished loading late");
        }

        // A client connected while the game was running has synchronized the current level.
        private void OnClientSynchronizeComplete(ulong clientId)
        {
            if (GameState != SessionGameState.InGame) return;
            TrySpawnLateClient(clientId, "late joiner synchronized");
        }

        private void TrySpawnLateClient(ulong clientId, string why)
        {
            if (!_netcode.ConnectedClients.TryGetValue(clientId, out var client) || client.PlayerObject != null) return;
            LogLifecycle($"Spawning client {clientId} ({why}).");
            _spawnManager.SpawnPlayer(clientId);
        }

        // ---------------- Session properties ----------------

        private bool IsJoinable(bool isLocked) =>
            !isLocked && (GameState == SessionGameState.Lobby || (allowLateJoin && GameState == SessionGameState.InGame));

        /// <summary>Properties a new session is created with.</summary>
        private Dictionary<string, SessionProperty> CreateInitialSessionProperties()
        {
            return new Dictionary<string, SessionProperty>
            {
                [GameStatePropertyKey] = new(SessionGameState.Lobby.ToString(), VisibilityPropertyOptions.Public),
                [JoinablePropertyKey] = new("1", VisibilityPropertyOptions.Public, JoinablePropertyIndex),
            };
        }

        private void ApplyStateProperties(IHostSession host)
        {
            host.SetProperty(GameStatePropertyKey,
                new SessionProperty(GameState.ToString(), VisibilityPropertyOptions.Public));
            host.SetProperty(JoinablePropertyKey,
                new SessionProperty(IsJoinable(host.IsLocked) ? "1" : "0", VisibilityPropertyOptions.Public, JoinablePropertyIndex));
        }

        /// <summary>Host: publish the current state/joinable flag (fire-and-forget, errors are logged).</summary>
        private async void PushStateProperties()
        {
            var session = CurrentSession;
            if (session is not { IsHost: true }) return;
            try
            {
                var host = session.AsHost();
                ApplyStateProperties(host);
                await host.SavePropertiesAsync();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        private void SetGameState(SessionGameState state)
        {
            if (GameState == state) return;
            GameState = state;
            LogLifecycle($"Game state: {state}.");
            GameStateChanged?.Invoke(state);
        }

        // Clients mirror the host's state from the session property.
        private void OnSessionChangedForGameFlow()
        {
            var session = CurrentSession;
            if (session == null || session.IsHost) return;
            if (session.Properties != null &&
                session.Properties.TryGetValue(GameStatePropertyKey, out var property) &&
                Enum.TryParse(property.Value, out SessionGameState state))
            {
                SetGameState(state);
            }
        }

        private void OnSessionClearedForGameFlow()
        {
            _netcodeClientConnected = false;
            _lockedByStart = false;
            // On the host, Netcode stopping resets the rest (OnNetcodeServerStopped).
            if (_netcode == null || !_netcode.IsServer) SetGameState(SessionGameState.Lobby);
        }
    }
}
