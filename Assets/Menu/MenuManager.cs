using System;
using System.Collections.Generic;
using Audio;
using Core;
using Game;
using UnityEngine;
using UnityEngine.UIElements;

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
        
        // Settings Menu - Main
        private Button _audioButton;
        private Button _graphicsButton;
        
        // Settings Menu - Audio
        private SliderInt _masterSlider;
        private SliderInt _musicSlider;
        private SliderInt _sfxSlider;
        private SliderInt _uiSlider;
        
        // Settings Menu - Graphics
        
        
        protected override void Awake()
        {
            base.Awake();
            
            _document = GetComponent<UIDocument>();
            
            _playButton = _document.rootVisualElement.Q<Button>("PlayButton");
            _settingsButton = _document.rootVisualElement.Q<Button>("SettingsButton");
            _quitButton = _document.rootVisualElement.Q<Button>("QuitButton");
            
            _audioButton = _document.rootVisualElement.Q<Button>("AudioButton");
            _graphicsButton = _document.rootVisualElement.Q<Button>("GraphicsButton");
            
            _masterSlider = _document.rootVisualElement.Q<SliderInt>("MasterVolume");
            _musicSlider = _document.rootVisualElement.Q<SliderInt>("MusicVolume");
            _sfxSlider = _document.rootVisualElement.Q<SliderInt>("SFXVolume");
            _uiSlider = _document.rootVisualElement.Q<SliderInt>("UIVolume");

            _playButton.clicked += OnPlayButtonClicked;
            _settingsButton.clicked += OnSettingsButtonClicked;
            _quitButton.clicked += OnQuitButtonClicked;
            
            _audioButton.clicked += OnAudioButtonClicked;
            _graphicsButton.clicked += OnGraphicsButtonClicked;

            _masterSlider.RegisterValueChangedCallback(OnMasterSliderChanged);
            _musicSlider.RegisterValueChangedCallback(OnMusicSliderChanged);
            _sfxSlider.RegisterValueChangedCallback(OnSfxSliderChanged);
            _uiSlider.RegisterValueChangedCallback(OnUiSliderChanged);
            
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
            
            OpenPanel("MainMenu"); 
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
            GameManager.Instance.LoadScene("DefaultScene");
            CloseMenu();
        }

        private void OnSettingsButtonClicked()
        {
            OpenPanel("Settings");
            OpenSubpanel("Audio");
        }

        private void OnQuitButtonClicked()
        {
            Application.Quit();
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
    }
}