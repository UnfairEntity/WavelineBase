using System;
using System.Collections.Generic;
using Core;
using Game;
using UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace Menu
{
    public class MenuManager : Singleton<MenuManager>
    {
        // Populate in Inspector with each menu's component
        [Obsolete] [SerializeField] private MainMenu mainMenu;
        [Obsolete] [SerializeField] private PauseMenu pauseMenu;
        [Obsolete] [SerializeField] private SettingsMenu settingsMenu;
        [Obsolete] [SerializeField] private CreditsMenu creditsMenu;
 
        [Obsolete] private readonly Stack<MenuBase> _historyOld = new();
        [Obsolete] private MenuBase _currentMenu;
        
        private UIDocument _document;
        private readonly Stack<VisualElement> _history = new();
        
        // Main Menu
        private Button _playButton;
        private Button _settingsButton;
        private Button _quitButton;

        protected override void Awake()
        {
            base.Awake();
            
            _document = GetComponent<UIDocument>();
            _playButton = _document.rootVisualElement.Q<Button>("PlayButton");
            _settingsButton = _document.rootVisualElement.Q<Button>("SettingsButton");
            _quitButton = _document.rootVisualElement.Q<Button>("QuitButton");

            _playButton.clicked += OnPlayButtonClicked;
            _settingsButton.clicked += OnSettingsButtonClicked;
            _quitButton.clicked += OnQuitButtonClicked;
            
            var backButtons = _document.rootVisualElement.Query<Button>("BackButton").ToList();

            foreach (var button in backButtons)
            {
                button.clicked += OnBackButtonClicked;
            }
            
            // Hide panels so you don't have to when editing
            var panels = _document.rootVisualElement.Query<VisualElement>()
                .Where(e => e.name.Contains("Panel")).ToList();

            foreach (var panel in panels)
            {
                panel.visible = false;
            }
        }
        
        private void Start()
        {
            OpenMenu("MainMenu");
        }
        
        private void OpenMenu(string menuName)
        {
            var target = _document.rootVisualElement.Q<VisualElement>(menuName+"Panel");
            if (_history.Count != 0) _history.Peek().visible = false;
            _history.Push(target);
            target.visible = true;
        }
        
        private void OnBackButtonClicked()
        {
            if (_history.Count <= 1) return;
            var target = _history.Pop();
            target.visible = false;
            _history.Peek().visible = true;
        }

        private void OnPlayButtonClicked()
        {
            GameManager.Instance.LoadScene("DefaultScene");
        }

        private void OnSettingsButtonClicked()
        {
            OpenMenu("Settings");
        }

        private void OnQuitButtonClicked()
        {
            Application.Quit();
        }

        // Open a menu and record it in history
        [Obsolete] public void OpenMenu(MenuBase menu)
        {
            if (menu == null) { Debug.LogError("[UIManager] Menu is null!"); return; }
            
            if (_currentMenu != null)
            {
                _currentMenu.Close();
                _historyOld.Push(_currentMenu);
            }
            
            _currentMenu = menu;
            _currentMenu.Open();
        }

        // Close menu and clear history
        [Obsolete] public void CloseMenu()
        {
            _currentMenu?.Close();
            _historyOld.Clear();
        }
    }
}