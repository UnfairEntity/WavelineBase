using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Audio;
using Core;
using Game;
using Menu.Components;
using Network;
using UnityEngine;
using UnityEngine.UIElements;
using Button = UnityEngine.UIElements.Button;

namespace Menu
{
    public class MenuManager : Singleton<MenuManager>
    {
        // General References
        private UIDocument _document;
        private readonly Stack<VisualElement> _history = new();
        private VisualElement _currentPanel;
        
        // Main Menu
        private Button _playButton;
        private Button _settingsButton;
        private Button _quitButton;
        
        // Play Menu
        private Button _soloButton;
        private Button _lobbiesButton;
        private ScrollView _sessionList;
        private Button _newLobbyButton;
        
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
        
        protected override void Awake()
        {
            base.Awake();
            
            _document = GetComponent<UIDocument>();
            
            _playButton = _document.rootVisualElement.Q<Button>("PlayButton");
            _settingsButton = _document.rootVisualElement.Q<Button>("SettingsButton");
            _quitButton = _document.rootVisualElement.Q<Button>("QuitButton");
            
            _soloButton = _document.rootVisualElement.Q<Button>("SoloButton");
            _lobbiesButton = _document.rootVisualElement.Q<Button>("LobbiesButton");
            _sessionList = _document.rootVisualElement.Q<ScrollView>("SessionList");
            _newLobbyButton = _document.rootVisualElement.Q<Button>("NewLobbyButton");
            
            _audioButton = _document.rootVisualElement.Q<Button>("AudioButton");
            _graphicsButton = _document.rootVisualElement.Q<Button>("GraphicsButton");
            
            _masterSlider = _document.rootVisualElement.Q<SliderInt>("MasterVolume");
            _musicSlider = _document.rootVisualElement.Q<SliderInt>("MusicVolume");
            _sfxSlider = _document.rootVisualElement.Q<SliderInt>("SFXVolume");
            _uiSlider = _document.rootVisualElement.Q<SliderInt>("UIVolume");

            _resolutionDropdown = _document.rootVisualElement.Q<DropdownField>("Resolution");
            _displayModeDropdown = _document.rootVisualElement.Q<DropdownField>("DisplayMode");
            _vSyncSlider = _document.rootVisualElement.Q<SliderInt>("VSyncFrames");
            _antiAliasingSlider = _document.rootVisualElement.Q<SliderInt>("AntiAliasingQuality");

            _playButton.clicked += OnPlayButtonClicked;
            _settingsButton.clicked += OnSettingsButtonClicked;
            _quitButton.clicked += OnQuitButtonClicked;
            
            _soloButton.clicked += OnSoloButtonClicked;
            _lobbiesButton.clicked += OnLobbiesButtonClicked;
            _newLobbyButton.clicked += OnNewLobbyButtonClicked;
            
            _audioButton.clicked += OnAudioButtonClicked;
            _graphicsButton.clicked += OnGraphicsButtonClicked;

            _masterSlider.RegisterValueChangedCallback(OnMasterSliderChanged);
            _musicSlider.RegisterValueChangedCallback(OnMusicSliderChanged);
            _sfxSlider.RegisterValueChangedCallback(OnSfxSliderChanged);
            _uiSlider.RegisterValueChangedCallback(OnUiSliderChanged);
            
            _resolutionDropdown.RegisterValueChangedCallback(OnResolutionChanged);
            _displayModeDropdown.RegisterValueChangedCallback(OnDisplayModeChanged);
            _vSyncSlider.RegisterValueChangedCallback(OnVSyncSliderChanged);
            _antiAliasingSlider.RegisterValueChangedCallback(OnAntiAliasingSliderChanged);
            
            
            var backButtons = _document.rootVisualElement.Query<Button>("BackButton").ToList();

            foreach (var button in backButtons)
            {
                button.clicked += ClosePanel;
            }
            
            // Hide panels so you don't have to when editing
            var panels = _document.rootVisualElement.Query<VisualElement>()
                .Where(e => e.name.Contains("Panel", StringComparison.CurrentCultureIgnoreCase)).ToList();

            foreach (var panel in panels)
            {
                panel.visible = false;
            }
        }

        private void Start()
        {
            _masterSlider.value = Mathf.RoundToInt(AudioManager.Instance.GetVolume(AudioCategory.Master));
            _musicSlider.value = Mathf.RoundToInt(AudioManager.Instance.GetVolume(AudioCategory.Music));
            _sfxSlider.value = Mathf.RoundToInt(AudioManager.Instance.GetVolume(AudioCategory.Sfx));
            _uiSlider.value = Mathf.RoundToInt(AudioManager.Instance.GetVolume(AudioCategory.Ui));
            
            _resolutionDropdown.value = Screen.currentResolution.width + "x" + Screen.currentResolution.height;
            _displayModeDropdown.value = ToDisplayModeName(Screen.fullScreenMode);
            _vSyncSlider.value = QualitySettings.vSyncCount;
            _antiAliasingSlider.value = ToAntiAliasingQuality(QualitySettings.antiAliasing);
            
            OpenPanel("MainMenu"); 
        }

        private async void RefreshSessions()
        {
            try
            {
                var results = await NetworkManager.Instance.QuerySessionsAsync();
                foreach (var result in results.Sessions)
                {
                    var lobbyButton = new LobbyButton
                    {
                        SessionName = result.Name,
                        PlayerCount = result.MaxPlayers - result.AvailableSlots,
                        MaxPlayers = result.MaxPlayers
                    };
                    lobbyButton.clicked += () => OnLobbyButtonClicked(result.Id);
                    _sessionList.Add(lobbyButton);
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError(e);
            }
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
                _ => FullScreenMode.MaximizedWindow
            };
        }
        
        private static int ToAntiAliasingQuality(int value)
        {
            return value switch
            {
                0 => 0,
                2 => 1,
                4 => 2,
                _ => 3
            };
        }
        
        private static int FromAntiAliasingQuality(int value)
        {
            return value switch
            {
                0 => 0,
                1 => 2,
                2 => 4,
                _ => 8
            };
        }

        private void OpenPanel(string panelName)
        {
            var target = _document.rootVisualElement.Q<VisualElement>(panelName+"Panel");
            if (_currentPanel != null) _currentPanel.visible = false;
            if (_history.Count != 0) _history.Peek().visible = false;
            target.visible = true;
            _history.Push(target);
        }
        
        private void ClosePanel()
        {
            if (_history.Count <= 1) return;
            if (_currentPanel != null) _currentPanel.visible = false;
            var target = _history.Pop();
            target.visible = false;
            _history.Peek().visible = true;
            CloseSubpanel();
        }

        private void OpenSubpanel(string subpanelName)
        {
            var target = _document.rootVisualElement.Q<VisualElement>(subpanelName+"Subpanel");
            if (_currentPanel == target) return;
            if (_currentPanel != null) _currentPanel.visible = false;
            _currentPanel = target;
            _currentPanel.visible = true;
        }

        private void CloseSubpanel()
        {
            if (_currentPanel == null) return;
            _currentPanel.visible = false;
            _currentPanel = null;
        }
        
        private void CloseMenu()
        {
            _history.Clear();
            _document.enabled = false;
        }

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
            Application.Quit();
        }

        private void OnSoloButtonClicked()
        {
            GameManager.LoadScene("DefaultScene");
            CloseMenu();
        }

        private void OnLobbiesButtonClicked()
        {
            OpenSubpanel("Lobbies");
            RefreshSessions();
        }

        private void OnLobbyButtonClicked(string lobbyId)
        {
            _ = NetworkManager.Instance.JoinSessionById(lobbyId);
        }

        private void OnNewLobbyButtonClicked()
        {
            OpenSubpanel("NewLobby");
        }

        private void OnAudioButtonClicked()
        {
            OpenSubpanel("Audio");
        }

        private void OnGraphicsButtonClicked()
        {
            OpenSubpanel("Graphics");
        }
        
        private static void OnMasterSliderChanged(ChangeEvent<int> evt)
        {
            AudioManager.Instance.SetVolume(AudioCategory.Master, evt.newValue);
        }
        
        private static void OnMusicSliderChanged(ChangeEvent<int> evt)
        {
            AudioManager.Instance.SetVolume(AudioCategory.Music, evt.newValue);
        }

        private static void OnSfxSliderChanged(ChangeEvent<int> evt)
        {
            AudioManager.Instance.SetVolume(AudioCategory.Sfx, evt.newValue);
        }

        private static void OnUiSliderChanged(ChangeEvent<int> evt)
        { 
            AudioManager.Instance.SetVolume(AudioCategory.Ui, evt.newValue);
        }
        
        private static void OnResolutionChanged(ChangeEvent<string> evt)
        {
            var resolution = evt.newValue.Split('x');
            Screen.SetResolution(int.Parse(resolution[0]), int.Parse(resolution[1]), Screen.fullScreen);
            SaveManager.SaveInt("ResolutionX", Screen.currentResolution.width);
            SaveManager.SaveInt("ResolutionY", Screen.currentResolution.height);
        }
        
        private static void OnDisplayModeChanged(ChangeEvent<string> evt)
        {
            Screen.fullScreenMode = FromDisplayModeName(evt.newValue);
            SaveManager.SaveInt("FullScreenMode", (int)Screen.fullScreenMode);
        }
        
        private static void OnVSyncSliderChanged(ChangeEvent<int> evt)
        {
            QualitySettings.vSyncCount = evt.newValue;
            SaveManager.SaveInt("VSyncCount", QualitySettings.vSyncCount);
        }

        private static void OnAntiAliasingSliderChanged(ChangeEvent<int> evt)
        { 
            QualitySettings.antiAliasing = FromAntiAliasingQuality(evt.newValue);
            SaveManager.SaveInt("AntiAliasing", QualitySettings.antiAliasing);
        }
    }
}