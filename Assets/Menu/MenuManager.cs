using System;
using System.Collections.Generic;
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
        
        // Settings Menu
        private Button _audioButton;
        private Button _graphicsButton;

        protected override void Awake()
        {
            base.Awake();
            
            _document = GetComponent<UIDocument>();
            
            _playButton = _document.rootVisualElement.Q<Button>("PlayButton");
            _settingsButton = _document.rootVisualElement.Q<Button>("SettingsButton");
            _quitButton = _document.rootVisualElement.Q<Button>("QuitButton");
            
            _audioButton = _document.rootVisualElement.Q<Button>("AudioButton");
            _graphicsButton = _document.rootVisualElement.Q<Button>("GraphicsButton");

            _playButton.clicked += OnPlayButtonClicked;
            _settingsButton.clicked += OnSettingsButtonClicked;
            _quitButton.clicked += OnQuitButtonClicked;
            
            _audioButton.clicked += OnAudioButtonClicked;
            _graphicsButton.clicked += OnGraphicsButtonClicked;
            
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
            OpenPanel("MainMenu");
        }
        
        private void OpenPanel(string panelName)
        {
            var target = _document.rootVisualElement.Q<VisualElement>(panelName+"Panel");
            if (_currentPanel != null) _currentPanel.visible = false;
            if (_history.Count != 0) _history.Peek().visible = false;
            _history.Push(target);
            target.visible = true;
        }
        
        private void ClosePanel()
        {
            if (_history.Count <= 1) return;
            if (_currentPanel != null) _currentPanel.visible = false;
            var target = _history.Pop();
            target.visible = false;
            _history.Peek().visible = true;
        }

        private void OpenSubpanel(string subpanelName)
        {
            if (_currentPanel != null) _currentPanel.visible = false;
            _currentPanel = _document.rootVisualElement.Q<VisualElement>(subpanelName+"Subpanel");
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
    }
}