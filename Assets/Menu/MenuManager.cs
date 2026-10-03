using System;
using System.Collections.Generic;
using System.Linq;
using Audio;
using Core;
using Game;
using Menu.Components;
using Network;
using Save;
using Unity.Services.Multiplayer;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using Button = UnityEngine.UIElements.Button;

namespace Menu
{
    /// <summary>
    /// PURPOSE: Main menu, settings and lobby screens (UI Toolkit).
    /// Menu is the UI wiring layer: it may call other managers directly (Audio, Game,
    /// Network, Save); no other subsystem may depend on Menu. See Architecture-Guide.md.
    /// The menu shows itself whenever the menu scene loads and hides for any other scene.
    /// PUBLIC API: ShowMainMenu, HideMenu
    /// </summary>
    public class MenuManager : Singleton<MenuManager>
    {
        private const int VolumeSliderMax = 100; // sliders are 0-100, AudioManager uses 0-1

        [Tooltip("Scene in which the main menu is shown. Loading any other scene (single mode) hides the menu.")]
        [SerializeField] private string menuSceneName = "Menu";

        // General References
        private UIDocument _document;
        private VisualElement _root;
        private readonly Stack<VisualElement> _history = new();
        private VisualElement _currentSubpanel;
        private List<VisualElement> _allPanels;
        private NetworkManager _network;
        private ISession _lobbySession;
        private bool _startingGame;

        // Session list paging
        private const float LoadMoreThresholdPixels = 40f; // load the next page this close to the bottom
        private const int MaxAutoFillPages = 5;            // pages fetched just to fill an unscrollable list
        private readonly HashSet<string> _listedSessionIds = new();
        private string _sessionPageToken;
        private bool _sessionListHasMore;
        private bool _loadingSessionPage;
        private int _sessionListGeneration; // bumped on refresh so stale page results are dropped

        // Main Menu
        private Button _playButton;
        private Button _settingsButton;
        private Button _quitButton;

        // Play Menu - Main
        private Button _soloButton;
        private Button _lobbiesButton;
        private ScrollView _sessionList;
        private Button _newLobbyButton;

        // Play Menu - New Lobby
        private TextField _lobbyName;
        private TextField _lobbyPassword;
        private Button _createLobbyButton;

        // Lobby
        private Label _lobbyNameText;
        private VisualElement _lobbyPlayers;
        private Button _startGameButton;
        private Toggle _lockOnStartToggle;

        // Settings Menu - Main
        private Button _audioButton;
        private Button _graphicsButton;

        // Settings Menu - Audio
        private SliderInt _masterSlider;
        private SliderInt _musicSlider;
        private SliderInt _sfxSlider;
        private SliderInt _uiSlider;

        // Settings Menu - Graphics
        private DropdownField _resolutionDropdown;
        private DropdownField _displayModeDropdown;
        private SliderInt _vSyncSlider;
        private SliderInt _antiAliasingSlider;

        // UI is bound in Start rather than Awake: the UIDocument builds its tree in its own
        // OnEnable, and by Start every Awake/OnEnable (including other managers') has run.
        private void Start()
        {
            if (IsDuplicate) return;

            _document = GetComponent<UIDocument>();
            _root = _document.rootVisualElement;

            BindElements();
            RegisterCallbacks();
            InitializeSettingValues();

            _network = NetworkManager.Instance;
            if (_network != null)
            {
                _network.SessionJoined += OnSessionJoined;
                _network.SessionLeft += OnSessionLeft;
                _network.GameStateChanged += OnGameStateChanged;
                _lockOnStartToggle?.SetValueWithoutNotify(_network.LockSessionOnStart);
            }

            SceneManager.sceneLoaded += OnSceneLoaded;
            // Usually still the bootstrap scene here; the menu scene loads next frame.
            ApplyMenuVisibilityFor(SceneManager.GetActiveScene());
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            if (IsDuplicate) return;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            UnsubscribeLobbySession();
            if (_network != null)
            {
                _network.SessionJoined -= OnSessionJoined;
                _network.SessionLeft -= OnSessionLeft;
                _network.GameStateChanged -= OnGameStateChanged;
            }
        }

        private void BindElements()
        {
            _playButton = _root.Q<Button>("PlayButton");
            _settingsButton = _root.Q<Button>("SettingsButton");
            _quitButton = _root.Q<Button>("QuitButton");

            _soloButton = _root.Q<Button>("SoloButton");
            _lobbiesButton = _root.Q<Button>("LobbiesButton");
            _sessionList = _root.Q<ScrollView>("SessionList");
            _newLobbyButton = _root.Q<Button>("NewLobbyButton");

            _lobbyName = _root.Q<TextField>("LobbyName");
            _lobbyPassword = _root.Q<TextField>("LobbyPassword");
            _createLobbyButton = _root.Q<Button>("CreateLobbyButton");

            _lobbyNameText = _root.Q<Label>("LobbyNameText");
            _lobbyPlayers = _root.Q<VisualElement>("LobbyPanel")?.Q<VisualElement>("Players");
            _startGameButton = _root.Q<Button>("StartGameButton");
            _lockOnStartToggle = _root.Q<Toggle>("LockOnStartToggle");

            _audioButton = _root.Q<Button>("AudioButton");
            _graphicsButton = _root.Q<Button>("GraphicsButton");

            _masterSlider = _root.Q<SliderInt>("MasterVolume");
            _musicSlider = _root.Q<SliderInt>("MusicVolume");
            _sfxSlider = _root.Q<SliderInt>("SFXVolume");
            _uiSlider = _root.Q<SliderInt>("UIVolume");

            _resolutionDropdown = _root.Q<DropdownField>("Resolution");
            _displayModeDropdown = _root.Q<DropdownField>("DisplayMode");
            _vSyncSlider = _root.Q<SliderInt>("VSyncFrames");
            _antiAliasingSlider = _root.Q<SliderInt>("AntiAliasingQuality");

            _allPanels = _root.Query<VisualElement>()
                .Where(e => e.name != null && e.name.Contains("Panel", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        private void RegisterCallbacks()
        {
            _playButton.clicked += OnPlayButtonClicked;
            _settingsButton.clicked += OnSettingsButtonClicked;
            _quitButton.clicked += OnQuitButtonClicked;

            _soloButton.clicked += OnSoloButtonClicked;
            _lobbiesButton.clicked += OnLobbiesButtonClicked;
            _newLobbyButton.clicked += OnNewLobbyButtonClicked;

            _createLobbyButton.clicked += OnCreateLobbyButtonClicked;
            if (_startGameButton != null) _startGameButton.clicked += OnStartGameButtonClicked;
            _sessionList.verticalScroller.valueChanged += OnSessionListScrolled;

            _audioButton.clicked += OnAudioButtonClicked;
            _graphicsButton.clicked += OnGraphicsButtonClicked;

            _masterSlider.RegisterValueChangedCallback(evt => SetVolumeFromSlider(AudioCategory.Master, evt.newValue));
            _musicSlider.RegisterValueChangedCallback(evt => SetVolumeFromSlider(AudioCategory.Music, evt.newValue));
            _sfxSlider.RegisterValueChangedCallback(evt => SetVolumeFromSlider(AudioCategory.Sfx, evt.newValue));
            _uiSlider.RegisterValueChangedCallback(evt => SetVolumeFromSlider(AudioCategory.Ui, evt.newValue));

            _resolutionDropdown.RegisterValueChangedCallback(OnResolutionChanged);
            _displayModeDropdown.RegisterValueChangedCallback(OnDisplayModeChanged);
            _vSyncSlider.RegisterValueChangedCallback(OnVSyncSliderChanged);
            _antiAliasingSlider.RegisterValueChangedCallback(OnAntiAliasingSliderChanged);

            foreach (var button in _root.Query<Button>("BackButton").ToList())
            {
                button.clicked += OnBackButtonClicked;
            }
        }

        /// <summary>Fills controls from current values without firing change callbacks,
        /// so opening the menu never re-applies or overwrites saved settings.</summary>
        private void InitializeSettingValues()
        {
            var audio = AudioManager.Instance;
            if (audio != null)
            {
                _masterSlider.SetValueWithoutNotify(ToSliderValue(audio.GetVolume(AudioCategory.Master)));
                _musicSlider.SetValueWithoutNotify(ToSliderValue(audio.GetVolume(AudioCategory.Music)));
                _sfxSlider.SetValueWithoutNotify(ToSliderValue(audio.GetVolume(AudioCategory.Sfx)));
                _uiSlider.SetValueWithoutNotify(ToSliderValue(audio.GetVolume(AudioCategory.Ui)));
            }

            // Resolution choices come from the display; fall back to the UXML list if none are reported.
            var choices = Screen.resolutions
                .Select(r => ToResolutionName(r.width, r.height))
                .Distinct()
                .Reverse()
                .ToList();
            if (choices.Count == 0) choices = new List<string>(_resolutionDropdown.choices);
            var current = ToResolutionName(Screen.width, Screen.height);
            if (!choices.Contains(current)) choices.Insert(0, current);
            _resolutionDropdown.choices = choices;
            _resolutionDropdown.SetValueWithoutNotify(current);

            _displayModeDropdown.SetValueWithoutNotify(ToDisplayModeName(Screen.fullScreenMode));
            _vSyncSlider.SetValueWithoutNotify(QualitySettings.vSyncCount);
            _antiAliasingSlider.SetValueWithoutNotify(ToAntiAliasingQuality(GameManager.GetMsaa()));
        }

        // ---------------- Show / hide ----------------

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode == LoadSceneMode.Single) ApplyMenuVisibilityFor(scene);
        }

        private void ApplyMenuVisibilityFor(Scene scene)
        {
            if (scene.name == menuSceneName)
                ShowMainMenu();
            else
                HideMenu();
        }

        /// <summary>Shows the menu (if hidden) and resets it to the main panel.</summary>
        public void ShowMainMenu()
        {
            _root.style.display = DisplayStyle.Flex;
            CloseSubpanel();
            foreach (var panel in _allPanels) panel.visible = false;
            _history.Clear();
            OpenPanel("MainMenu");
        }

        /// <summary>Hides the whole menu. The UIDocument stays enabled so element references
        /// and callbacks survive; call ShowMainMenu to bring it back.</summary>
        public void HideMenu()
        {
            CloseSubpanel();
            foreach (var panel in _allPanels) panel.visible = false;
            _history.Clear();
            _root.style.display = DisplayStyle.None;
            SaveManager.Flush();
        }

        // ---------------- Panel navigation ----------------

        private void OpenPanel(string panelName)
        {
            var target = _root.Q<VisualElement>(panelName + "Panel");
            if (target == null)
            {
                Debug.LogError($"[MenuManager] No panel named '{panelName}Panel'.");
                return;
            }

            CloseSubpanel();
            if (_history.Count != 0) _history.Peek().visible = false;
            target.visible = true;
            _history.Push(target);
        }

        private void ClosePanel()
        {
            if (_history.Count <= 1) return;
            CloseSubpanel();
            var closing = _history.Pop();
            closing.visible = false;
            _history.Peek().visible = true;

            if (closing.name == "SettingsPanel") SaveManager.Flush();
        }

        private bool IsTopPanel(string panelName) =>
            _history.Count != 0 && _history.Peek().name == panelName + "Panel";

        private void OpenSubpanel(string subpanelName)
        {
            var target = _root.Q<VisualElement>(subpanelName + "Subpanel");
            if (target == null)
            {
                Debug.LogError($"[MenuManager] No subpanel named '{subpanelName}Subpanel'.");
                return;
            }
            if (_currentSubpanel == target && target.visible) return;
            if (_currentSubpanel != null) _currentSubpanel.visible = false;
            _currentSubpanel = target;
            _currentSubpanel.visible = true;
        }

        private void CloseSubpanel()
        {
            if (_currentSubpanel == null) return;
            _currentSubpanel.visible = false;
            _currentSubpanel = null;
        }

        private void OnBackButtonClicked()
        {
            // Leaving the lobby screen means leaving the session; OnSessionLeft closes the panel.
            if (IsTopPanel("Lobby"))
            {
                LeaveLobby();
                return;
            }
            ClosePanel();
        }

        // ---------------- Main / Play ----------------

        private void OnPlayButtonClicked()
        {
            OpenPanel("Play");
        }

        private void OnSettingsButtonClicked()
        {
            OpenPanel("Settings");
            OpenSubpanel("Audio");
        }

        private static void OnQuitButtonClicked()
        {
            SaveManager.Flush();
            Application.Quit();
        }

        private void OnSoloButtonClicked()
        {
            GameManager.LoadScene("DefaultScene");
            HideMenu();
        }

        private void OnLobbiesButtonClicked()
        {
            OpenSubpanel("Lobbies");
            RefreshSessions();
        }

        private void OnNewLobbyButtonClicked()
        {
            OpenSubpanel("NewLobby");
        }

        // ---------------- Lobbies ----------------

        /// <summary>Clears the session list and loads it again from the first page.</summary>
        private void RefreshSessions()
        {
            if (_network == null) return;
            if (!_network.IsReady)
            {
                Debug.LogWarning("[MenuManager] Still connecting to Unity Services; try again in a moment.");
                return;
            }

            _sessionListGeneration++;
            _sessionList.Clear();
            _listedSessionIds.Clear();
            _sessionPageToken = null;
            _sessionListHasMore = true;
            _loadingSessionPage = false;
            LoadNextSessionPage(MaxAutoFillPages);
        }

        /// <summary>Appends the next page of sessions. Called on refresh, when the list is scrolled near
        /// the bottom, and (up to autoFillPagesLeft times) while the list is too short to scroll.</summary>
        private async void LoadNextSessionPage(int autoFillPagesLeft = 0)
        {
            if (_loadingSessionPage || !_sessionListHasMore || _network == null) return;

            _loadingSessionPage = true;
            var generation = _sessionListGeneration;
            try
            {
                var results = await _network.QuerySessionsAsync(_sessionPageToken);
                if (generation != _sessionListGeneration) return; // refreshed while this page was loading

                foreach (var info in results.Sessions)
                {
                    // Skip duplicates (lists shift between pages) and sessions that can't be joined.
                    if (info.IsLocked || !_listedSessionIds.Add(info.Id)) continue;
                    AddSessionButton(info);
                }

                _sessionPageToken = results.ContinuationToken;
                _sessionListHasMore = !string.IsNullOrEmpty(_sessionPageToken) && results.Sessions.Count > 0;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                if (generation == _sessionListGeneration) _sessionListHasMore = false; // reopen Lobbies to retry
            }
            finally
            {
                if (generation == _sessionListGeneration) _loadingSessionPage = false;
            }

            // A short list can't be scrolled, so keep fetching until it can (or pages run out).
            if (generation == _sessionListGeneration && autoFillPagesLeft > 0 && _sessionListHasMore)
            {
                _sessionList.schedule.Execute(() =>
                {
                    if (generation == _sessionListGeneration && !IsSessionListScrollable())
                        LoadNextSessionPage(autoFillPagesLeft - 1);
                });
            }
        }

        private void AddSessionButton(ISessionInfo info)
        {
            var sessionId = info.Id;
            var lobbyButton = new LobbyButton
            {
                SessionName = info.Name,
                MaxPlayers = info.MaxPlayers,
                PlayerCount = info.MaxPlayers - info.AvailableSlots
            };
            lobbyButton.clicked += () => OnLobbyButtonClicked(sessionId);
            _sessionList.Add(lobbyButton);
        }

        private bool IsSessionListScrollable() =>
            _sessionList.contentContainer.layout.height > _sessionList.contentViewport.layout.height + 1f;

        private void OnSessionListScrolled(float value)
        {
            var scroller = _sessionList.verticalScroller;
            if (scroller.highValue > 0f && value >= scroller.highValue - LoadMoreThresholdPixels)
                LoadNextSessionPage();
        }

        private async void OnLobbyButtonClicked(string sessionId)
        {
            try
            {
                // NOTE: password-protected sessions will be rejected here until a password prompt exists.
                await _network.JoinSessionById(sessionId);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        private async void OnCreateLobbyButtonClicked()
        {
            var password = _lobbyPassword.value;
            if (!NetworkManager.IsValidPassword(password, out var error))
            {
                Debug.LogWarning($"[MenuManager] {error}");
                return;
            }

            try
            {
                await _network.StartSessionAsHost(_lobbyName.value, password);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        private async void LeaveLobby()
        {
            try
            {
                if (_network != null && _network.InSession)
                    await _network.LeaveSession("lobby Back button"); // raises SessionLeft -> OnSessionLeft
                else
                    OnSessionLeft();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        private void OnSessionJoined(ISession session)
        {
            UnsubscribeLobbySession();
            _lobbySession = session;
            _lobbySession.Changed += UpdateLobby;

            if (!IsTopPanel("Lobby")) OpenPanel("Lobby");
            UpdateLobby();
        }

        // Fires when we leave, are kicked, the host deletes the session, or we lose the connection.
        private void OnSessionLeft()
        {
            UnsubscribeLobbySession();

            // Left while in a level (host quit, disconnected, ...): go back to the menu scene,
            // which shows the main menu when it loads.
            if (SceneManager.GetActiveScene().name != menuSceneName)
            {
                GameManager.LoadScene(menuSceneName);
                return;
            }

            if (IsTopPanel("Lobby")) ClosePanel();
        }

        private void OnGameStateChanged(SessionGameState state) => UpdateLobby();

        private async void OnStartGameButtonClicked()
        {
            if (_network == null || _startingGame) return;

            _startingGame = true;
            UpdateStartControls();
            try
            {
                var started = await _network.StartGame(_lockOnStartToggle?.value);
                if (!started) Debug.LogWarning("[MenuManager] The game didn't start; see the messages above.");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                _startingGame = false;
                UpdateStartControls();
            }
        }

        // Start + lock toggle: host only, and only while the session is still in the lobby.
        private void UpdateStartControls()
        {
            var isHost = _lobbySession is { IsHost: true };
            var inLobby = _network == null || _network.GameState == SessionGameState.Lobby;
            var display = isHost ? DisplayStyle.Flex : DisplayStyle.None;

            if (_startGameButton != null)
            {
                _startGameButton.style.display = display;
                _startGameButton.SetEnabled(inLobby && !_startingGame);
            }
            if (_lockOnStartToggle != null)
            {
                _lockOnStartToggle.style.display = display;
                _lockOnStartToggle.SetEnabled(inLobby && !_startingGame);
            }
        }

        private void UnsubscribeLobbySession()
        {
            if (_lobbySession == null) return;
            _lobbySession.Changed -= UpdateLobby;
            _lobbySession = null;
        }

        private void UpdateLobby()
        {
            var session = _lobbySession;
            if (session == null) return;

            UpdateStartControls();

            if (_lobbyNameText != null)
            {
                var title = string.IsNullOrEmpty(session.Code) ? session.Name : $"{session.Name}  [{session.Code}]";
                if (_network != null && _network.GameState != SessionGameState.Lobby)
                    title += _network.GameState == SessionGameState.Loading ? "  - starting..." : "  - in game";
                _lobbyNameText.text = title;
            }

            if (_lobbyPlayers == null) return;
            _lobbyPlayers.Clear();

            var selfId = session.CurrentPlayer?.Id;
            foreach (var player in session.Players)
            {
                var playerName = player.GetPlayerName();
                if (string.IsNullOrEmpty(playerName)) playerName = "Player";
                if (player.Id == selfId) playerName += " (You)";

                var entry = new Label(playerName);
                entry.AddToClassList("lobby-player-entry");
                _lobbyPlayers.Add(entry);
            }
        }

        // ---------------- Settings ----------------

        private void OnAudioButtonClicked()
        {
            OpenSubpanel("Audio");
        }

        private void OnGraphicsButtonClicked()
        {
            OpenSubpanel("Graphics");
        }

        private static void SetVolumeFromSlider(AudioCategory category, int sliderValue)
        {
            AudioManager.Instance.SetVolume(category, (float)sliderValue / VolumeSliderMax);
        }

        private static int ToSliderValue(float linear01) => Mathf.RoundToInt(linear01 * VolumeSliderMax);

        private static void OnResolutionChanged(ChangeEvent<string> evt)
        {
            if (!TryParseResolution(evt.newValue, out var width, out var height))
            {
                Debug.LogWarning($"[MenuManager] Couldn't parse resolution '{evt.newValue}'.");
                return;
            }
            GameManager.ApplyResolution(width, height);
        }

        private static void OnDisplayModeChanged(ChangeEvent<string> evt)
        {
            GameManager.ApplyDisplayMode(FromDisplayModeName(evt.newValue));
        }

        private static void OnVSyncSliderChanged(ChangeEvent<int> evt)
        {
            GameManager.ApplyVSync(evt.newValue);
        }

        private static void OnAntiAliasingSliderChanged(ChangeEvent<int> evt)
        {
            GameManager.ApplyMsaa(FromAntiAliasingQuality(evt.newValue));
        }

        // ---------------- Conversions ----------------

        private static string ToResolutionName(int width, int height) => width + "x" + height;

        private static bool TryParseResolution(string value, out int width, out int height)
        {
            width = height = 0;
            if (string.IsNullOrEmpty(value)) return false;
            var parts = value.Split('x');
            return parts.Length == 2
                   && int.TryParse(parts[0], out width)
                   && int.TryParse(parts[1], out height);
        }

        private static string ToDisplayModeName(FullScreenMode fullScreenMode)
        {
            return fullScreenMode switch
            {
                FullScreenMode.ExclusiveFullScreen => "Fullscreen",
                FullScreenMode.FullScreenWindow => "Windowed Fullscreen",
                _ => "Windowed"
            };
        }

        private static FullScreenMode FromDisplayModeName(string modeName)
        {
            return modeName switch
            {
                "Fullscreen" => FullScreenMode.ExclusiveFullScreen,
                "Windowed Fullscreen" => FullScreenMode.FullScreenWindow,
                _ => FullScreenMode.Windowed
            };
        }

        // Slider index 0-3 <-> MSAA sample count 1 (off), 2, 4, 8
        private static int ToAntiAliasingQuality(int sampleCount)
        {
            return sampleCount switch
            {
                <= 1 => 0,
                2 => 1,
                4 => 2,
                _ => 3
            };
        }

        private static int FromAntiAliasingQuality(int quality)
        {
            return quality switch
            {
                <= 0 => 1,
                1 => 2,
                2 => 4,
                _ => 8
            };
        }
    }
}
