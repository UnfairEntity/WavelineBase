using System.Collections.Generic;
using Core;
using UI;
using UnityEngine;

namespace Menu
{
    public class MenuManager : Singleton<MenuManager>
    {
        // Populate in Inspector with each menu's component
        [SerializeField] private MainMenu mainMenu;
        [SerializeField] private PauseMenu pauseMenu;
        [SerializeField] private SettingsMenu settingsMenu;
        [SerializeField] private CreditsMenu creditsMenu;
 
        private readonly Stack<MenuBase> _history = new();
        private MenuBase _currentMenu;
        
        private void Start()
        {
            OpenMainMenu();
        }

        // Open a menu and record it in history
        public void OpenMenu(MenuBase menu)
        {
            if (menu == null) { Debug.LogError("[UIManager] Menu is null!"); return; }
            
            if (_currentMenu != null)
            {
                _currentMenu.Close();
                _history.Push(_currentMenu);
            }
            
            _currentMenu = menu;
            _currentMenu.Open();
        }
 
        // Close current menu, return to previous
        public void GoBack()
        {
            if (_history.Count == 0) return;
            _currentMenu?.Close();
            _currentMenu = _history.Pop();
            _currentMenu.Open();
        }

        // Close menu and clear history
        public void CloseMenu()
        {
            _currentMenu?.Close();
            _history.Clear();
        }
 
        // Convenience accessors
        public void OpenMainMenu() => OpenMenu(mainMenu);
        public void OpenPause() => OpenMenu(pauseMenu);
        public void OpenSettings() => OpenMenu(settingsMenu);
        public void OpenCredits() => OpenMenu(creditsMenu);
    }
}