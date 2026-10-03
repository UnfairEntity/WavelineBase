using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Core;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine;

namespace Network
{
    /// <summary>
    /// PURPOSE: Unity Gaming Services sign-in plus session (lobby) create/join/leave/query,
    ///          and (NetworkManager.GameFlow.cs) starting the game: level transition, optional
    ///          lock, connection approval for late joiners, and handing players to SpawnManager.
    /// DEPENDENCIES: com.unity.services.multiplayer, and Netcode for GameObjects for the
    ///               relay connection: a Unity.Netcode.NetworkManager (+ UnityTransport, with
    ///               Enable Scene Management on) on the same prefab. See Architecture-Guide.md.
    /// EVENTS PUBLISHED: Ready, SessionJoined(ISession), SessionLeft, GameStateChanged
    /// PUBLIC API: StartSessionAsHost, JoinSessionByCode, JoinSessionById, LeaveSession,
    ///             QuerySessionsAsync (paged), Set/Remove String/Number filters, sort options,
    ///             SetSessionIsLocked, IsValidPassword, StartGame, GameState, Spawner
    /// </summary>
    public partial class NetworkManager : Singleton<NetworkManager>
    {
        public const int MinPasswordLength = 8;
        public const int MaxPasswordLength = 64;

        private static readonly FilterField[] StringFields =
        {
            FilterField.StringIndex1, FilterField.StringIndex2, FilterField.StringIndex3,
            FilterField.StringIndex4, FilterField.StringIndex5
        };

        private static readonly FilterField[] NumberFields =
        {
            FilterField.NumberIndex1, FilterField.NumberIndex2, FilterField.NumberIndex3,
            FilterField.NumberIndex4, FilterField.NumberIndex5
        };

        [Header("Host Defaults")]
        [SerializeField] private int maxPlayers = 4;
        [SerializeField] private bool isPrivate;
        [SerializeField] private string defaultSessionName = "Unnamed";

        [Header("Queries")]
        [Tooltip("Sessions fetched per page of the session list.")]
        [SerializeField] private int queryCount = 10;

        [Header("Quitting")]
        [Tooltip("When quitting while in a session, how long to wait for the leave to finish before quitting anyway.")]
        [SerializeField] private float quitLeaveTimeoutSeconds = 3f;

        [Header("Diagnostics")]
        [Tooltip("Log every session join/leave with the reason and frame, so Console traces show which code path caused them.")]
        [SerializeField] private bool logSessionLifecycle = true;

        /// <summary>The session this player is currently in, or null.</summary>
        public ISession CurrentSession { get; private set; }
        public bool InSession => CurrentSession != null;

        /// <summary>True once Unity Services are initialized and the player is signed in.</summary>
        public bool IsReady { get; private set; }

        public event Action Ready;
        public event Action<ISession> SessionJoined;
        public event Action SessionLeft;

        private readonly Dictionary<FilterField, FilterOption> _filters = new();
        private float _lastQueryTime = float.NegativeInfinity;
        private readonly List<SortOption> _sortOptions = new();

#if !UNITY_EDITOR
        private bool _leavingForQuit; // only used by the build quit path
#endif
        private bool _quitApproved;
        private bool _shuttingDown; // Play mode exiting (Editor) or application quitting

        protected override void Awake()
        {
            base.Awake();
            if (IsDuplicate) return;

            // Leave the session ourselves *before* quitting starts. Otherwise the session service's
            // own Application.quitting handler leaves after Netcode has already shut down, which logs
            // "NetworkManagerSession.StopAsync: Called after dispose".
            // Not raised when stopping Play mode in the Editor - see the note in the QUITTING region.
            Application.wantsToQuit += OnWantsToQuit;
            Application.quitting += OnQuitting;
#if UNITY_EDITOR
            // Only used to label log lines - no leave is started from here (see EDITOR NOTE below).
            UnityEditor.EditorApplication.playModeStateChanged += OnEditorPlayModeStateChanged;
#endif
            InitializeGameFlow();
        }

        private async void Start()
        {
            try
            {
                await UnityServices.InitializeAsync();
                if (!AuthenticationService.Instance.IsSignedIn)
                    await AuthenticationService.Instance.SignInAnonymouslyAsync();
                Debug.Log($"Sign in anonymously succeeded! PlayerID: {AuthenticationService.Instance.PlayerId}");

                // WithPlayerName() requires the player name to have been fetched from the
                // Authentication service first (one is generated if the player has none).
                try
                {
                    await AuthenticationService.Instance.GetPlayerNameAsync();
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[NetworkManager] Could not fetch player name; lobby names may be blank. {e.Message}");
                }

                IsReady = true;
                Ready?.Invoke();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            Application.wantsToQuit -= OnWantsToQuit;
            Application.quitting -= OnQuitting;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.playModeStateChanged -= OnEditorPlayModeStateChanged;
#endif
            ShutdownGameFlow();
            // Unity's session service leaves automatically on application quit; just drop our hooks.
            if (CurrentSession != null) UnhookSession(CurrentSession);
        }

        // SESSION JOINING //

        /// <summary>Creates and joins a new session as host. Leaves any current session first.
        /// An empty password means no password; otherwise it must be 8-64 characters.</summary>
        public async Task StartSessionAsHost(string sessionName = null, string password = null)
        {
            EnsureReady();
            EnsureNetcodeReady();
            if (!IsValidPassword(password, out var passwordError))
                throw new ArgumentException(passwordError, nameof(password));

            if (InSession) await LeaveSession("hosting a new session");

            var options = new SessionOptions
            {
                MaxPlayers = maxPlayers,
                IsPrivate = isPrivate,
                Name = string.IsNullOrWhiteSpace(sessionName) ? defaultSessionName : sessionName.Trim(),
                Password = NormalizePassword(password),
                SessionProperties = CreateInitialSessionProperties(),
            }
            .WithRelayNetwork()
            .WithPlayerName(VisibilityPropertyOptions.Member);

            var session = await MultiplayerService.Instance.CreateSessionAsync(options);
            Debug.Log($"Session {session.Id} created! Join code: {session.Code}");
            SetSession(session);
        }

        public async Task JoinSessionByCode(string code, string password = null)
        {
            EnsureReady();
            EnsureNetcodeReady();
            if (InSession) await LeaveSession("joining another session");

            var session = await MultiplayerService.Instance.JoinSessionByCodeAsync(code, CreateJoinOptions(password));
            SetSession(session);
        }

        public async Task JoinSessionById(string id, string password = null)
        {
            EnsureReady();
            EnsureNetcodeReady();
            if (InSession) await LeaveSession("joining another session");

            var session = await MultiplayerService.Instance.JoinSessionByIdAsync(id, CreateJoinOptions(password));
            SetSession(session);
        }

        private static JoinSessionOptions CreateJoinOptions(string password)
        {
            return new JoinSessionOptions { Password = NormalizePassword(password) }
                .WithPlayerName(VisibilityPropertyOptions.Member);
        }

        // SESSION QUERIES //

        /// <summary>
        /// Fetches one page (Queries > Query Count sessions) of the session list. Pass the previous
        /// page's <see cref="QuerySessionsResults.ContinuationToken"/> to get the next page; a null or
        /// empty token on the result means there are no more pages. Unless "Show Unjoinable Sessions"
        /// is on, only sessions that can be joined right now are returned. Calls closer together than
        /// "Min Query Interval Seconds" are delayed to stay under the service's rate limit.
        /// </summary>
        public async Task<QuerySessionsResults> QuerySessionsAsync(string continuationToken = null)
        {
            EnsureReady();

            var wait = minQueryIntervalSeconds - (Time.realtimeSinceStartup - _lastQueryTime);
            if (wait > 0f) await Awaitable.WaitForSecondsAsync(wait);
            _lastQueryTime = Time.realtimeSinceStartup;

            // A fresh options object per call, so overlapping queries can't change each other's paging.
            var options = new QuerySessionsOptions
            {
                Count = Mathf.Max(1, queryCount),
                ContinuationToken = string.IsNullOrEmpty(continuationToken) ? null : continuationToken,
            };
            foreach (var filter in _filters.Values) options.FilterOptions.Add(filter);
            if (!showUnjoinableSessions)
                options.FilterOptions.Add(new FilterOption(JoinableFilterField, "1", FilterOperation.Equal));
            foreach (var sort in _sortOptions) options.SortOptions.Add(sort);

            return await MultiplayerService.Instance.QuerySessionsAsync(options);
        }

        /// <summary>Adds a filter on custom string property 0-3, replacing any existing filter on that property.
        /// (Index 4 is reserved for the joinable flag.)</summary>
        public void SetStringFilter(int propertyIndex, string value, FilterOperation operation = default)
        {
            if (propertyIndex == ReservedStringPropertyIndex)
            {
                Debug.LogError($"[NetworkManager] String property index {ReservedStringPropertyIndex} is reserved for the '{JoinablePropertyKey}' flag.", this);
                return;
            }
            if (!TryGetField(StringFields, propertyIndex, out var field)) return;
            _filters[field] = new FilterOption(field, value, operation);
        }

        /// <summary>Adds a filter on custom number property 0-4, replacing any existing filter on that property.</summary>
        public void SetNumberFilter(int propertyIndex, int value, FilterOperation operation)
        {
            if (!TryGetField(NumberFields, propertyIndex, out var field)) return;
            _filters[field] = new FilterOption(field, value.ToString(), operation);
        }

        public void RemoveStringFilter(int propertyIndex)
        {
            if (TryGetField(StringFields, propertyIndex, out var field)) _filters.Remove(field);
        }

        public void RemoveNumberFilter(int propertyIndex)
        {
            if (TryGetField(NumberFields, propertyIndex, out var field)) _filters.Remove(field);
        }

        public void ClearFilters() => _filters.Clear();

        public void AddSortOption(bool isAscending, SortField sortField)
        {
            _sortOptions.Add(new SortOption(isAscending ? SortOrder.Ascending : SortOrder.Descending, sortField));
        }

        public void RemoveSortOption(int optionIndex)
        {
            if (optionIndex >= 0 && optionIndex < _sortOptions.Count) _sortOptions.RemoveAt(optionIndex);
        }

        public void ClearSortOptions() => _sortOptions.Clear();

        private static bool TryGetField(FilterField[] fields, int propertyIndex, out FilterField field)
        {
            field = default;
            if (propertyIndex < 0 || propertyIndex >= fields.Length)
            {
                Debug.LogError($"[NetworkManager] Property index {propertyIndex} out of range (min: 0, max: {fields.Length - 1}).");
                return false;
            }
            field = fields[propertyIndex];
            return true;
        }

        // SESSION LEAVING //

        /// <param name="reason">Shown in the diagnostic log so traces say which code path left.</param>
        public async Task LeaveSession(string reason = "unspecified")
        {
            var session = CurrentSession;
            if (session == null) return;

            var sessionId = session.Id;
            LogLifecycle($"Leaving session {sessionId} (reason: {reason})");

            // Clear local state first so UI/game code reacts even if the service call fails.
            ClearSession();
            try
            {
                await session.LeaveAsync();
                LogLifecycle($"Left session {sessionId} (reason: {reason})");
            }
            catch (ObjectDisposedException)
            {
                // Expected when a leave is still running as the Editor exits Play mode: the Wire
                // package disposes all lobby subscriptions on ExitingPlayMode, so the final
                // unsubscribe step fails. The player has already been removed from the lobby by then.
                // (The service's own quit handler swallows this same exception.)
                LogLifecycle($"Left session {sessionId} (reason: {reason}); lobby event subscription was already " +
                             $"disposed{(_shuttingDown ? " by shutdown" : "")} - expected, ignored.");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        // QUITTING //

        // Builds (and Application.Quit in the Editor): hold the quit until we've left the session.
        private bool OnWantsToQuit()
        {
#if UNITY_EDITOR
            // Application.Quit does nothing in the Editor, and stopping Play mode can't be delayed.
            // Let the service's own quit handler leave instead of racing Play mode teardown.
            LogLifecycle("wantsToQuit in Editor - not holding the quit.");
            return true;
#else
            if (_quitApproved || !InSession) return true;

            if (!_leavingForQuit)
            {
                _leavingForQuit = true;
                StartCoroutine(QuitAfterTimeout());
                LeaveThenQuit();
            }
            return false; // cancel this quit; QuitNow() quits again once the leave is done
#endif
        }

        private async void LeaveThenQuit()
        {
            try
            {
                await LeaveSession("application quit");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            QuitNow();
        }

        // Safety net so a slow or failed service call can't stop the game from closing.
        private IEnumerator QuitAfterTimeout()
        {
            yield return new WaitForSecondsRealtime(quitLeaveTimeoutSeconds);
            if (!_quitApproved) LogLifecycle($"Leave didn't finish within {quitLeaveTimeoutSeconds}s; quitting anyway.");
            QuitNow();
        }

        private void QuitNow()
        {
            if (_quitApproved) return;
            _quitApproved = true;
            Application.Quit();
        }

        // EDITOR NOTE: stopping Play mode can't be delayed, so there's no clean way to leave first.
        // Starting a leave on ExitingPlayMode was tried: Unity tears down the services while the
        // leave is still running, which throws ObjectDisposedException/OperationCanceledException.
        // So in the Editor the service's own quit handler leaves the session, logging one harmless
        // "Called after dispose" warning. Press Back in the lobby before stopping to avoid it.

        private void OnQuitting()
        {
            _shuttingDown = true;
            LogLifecycle($"Application quitting (in session: {InSession}).");
        }

#if UNITY_EDITOR
        private void OnEditorPlayModeStateChanged(UnityEditor.PlayModeStateChange state)
        {
            if (state != UnityEditor.PlayModeStateChange.ExitingPlayMode) return;
            _shuttingDown = true;
            LogLifecycle($"Exiting Play mode (in session: {InSession}).");
        }
#endif

        private void LogLifecycle(string message)
        {
            if (logSessionLifecycle)
                Debug.Log($"[NetworkManager] {message} [frame {Time.frameCount}, t={Time.realtimeSinceStartup:F2}s]", this);
        }

        // SESSION STATE //

        public async Task SetSessionIsLocked(bool isLocked = true)
        {
            if (CurrentSession is not { IsHost: true }) return; // Only the host can lock

            var host = CurrentSession.AsHost();
            host.IsLocked = isLocked;
            ApplyStateProperties(host); // keep the "joinable" flag in step with the lock
            await host.SavePropertiesAsync();
        }

        // HELPERS //

        /// <summary>Empty/null means "no password"; anything else must be 8-64 characters (Unity session service rule).</summary>
        public static bool IsValidPassword(string password, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(password)) return true;
            if (password.Length is >= MinPasswordLength and <= MaxPasswordLength) return true;

            error = $"Session passwords must be {MinPasswordLength}-{MaxPasswordLength} characters (or empty for no password).";
            return false;
        }

        private static string NormalizePassword(string password) => string.IsNullOrEmpty(password) ? null : password;

        private void EnsureReady()
        {
            if (!IsReady)
                throw new InvalidOperationException("[NetworkManager] Unity Services aren't ready yet (still signing in, or sign-in failed).");
        }

        private void EnsureNetcodeReady()
        {
            if (global::Unity.Netcode.NetworkManager.Singleton == null)
                throw new InvalidOperationException(
                    "[NetworkManager] No Netcode for GameObjects NetworkManager found. Relay sessions need one: add " +
                    "Unity.Netcode.NetworkManager and UnityTransport components to the Network prefab (see Architecture-Guide.md).");
            EnsureApprovalCallback();
        }

        private void SetSession(ISession session)
        {
            CurrentSession = session;
            LogLifecycle($"Joined session {session.Id} as {(session.IsHost ? "host" : "client")}.");
            session.RemovedFromSession += HandleSessionLost;
            session.Deleted += HandleSessionLost;
            session.Changed += OnSessionChangedForGameFlow;
            OnSessionChangedForGameFlow();
            SessionJoined?.Invoke(session);
        }

        private void ClearSession()
        {
            if (CurrentSession == null) return;
            UnhookSession(CurrentSession);
            CurrentSession = null;
            OnSessionClearedForGameFlow();
            SessionLeft?.Invoke();
        }

        private void UnhookSession(ISession session)
        {
            session.RemovedFromSession -= HandleSessionLost;
            session.Deleted -= HandleSessionLost;
            session.Changed -= OnSessionChangedForGameFlow;
        }

        // Kicked, or the host deleted the session.
        private void HandleSessionLost()
        {
            LogLifecycle("Removed from session (kicked, or the session was deleted).");
            ClearSession();
        }
    }
}
