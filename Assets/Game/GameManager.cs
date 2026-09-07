using Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game
{
    public class GameManager : Singleton<GameManager>
    {
        private void Start()
        {
            LoadGraphics();
        }

        private static void LoadGraphics()
        {
            var resX = SaveManager.HasSetting("ResolutionX") ? SaveManager.LoadInt("ResolutionX") : Screen.width;
            var resY = SaveManager.HasSetting("ResolutionY") ? SaveManager.LoadInt("ResolutionY") : Screen.height;
            var mode = SaveManager.HasSetting("FullScreenMode") ? SaveManager.LoadInt("FullScreenMode") : (int)Screen.fullScreenMode;
            var vSyncCount = SaveManager.HasSetting("VSyncCount") ? SaveManager.LoadInt("VSyncCount") : QualitySettings.vSyncCount;
            var antiAliasing = SaveManager.HasSetting("AntiAliasing") ? SaveManager.LoadInt("AntiAliasing") : QualitySettings.antiAliasing;
            
            Screen.SetResolution(resX, resY, (FullScreenMode)mode);
            QualitySettings.vSyncCount = vSyncCount;
            QualitySettings.antiAliasing = antiAliasing;
        }

        public static void LoadScene(string sceneName)
        {
            SceneManager.LoadScene(sceneName);
        }
    }
}