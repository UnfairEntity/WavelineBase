using Core;
using Save;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace Game
{
    /// <summary>
    /// PURPOSE: Scene loading and graphics settings (apply + persist + restore on boot).
    /// DEPENDENCIES: Core, Save, URP.
    /// PUBLIC API: LoadScene, ApplyResolution, ApplyDisplayMode, ApplyVSync,
    ///             ApplyMsaa, GetMsaa
    /// </summary>
    public class GameManager : Singleton<GameManager>
    {
        private const string ResolutionXKey = "ResolutionX";
        private const string ResolutionYKey = "ResolutionY";
        private const string FullScreenModeKey = "FullScreenMode";
        private const string VSyncCountKey = "VSyncCount";
        private const string AntiAliasingKey = "AntiAliasing";

        private void Start()
        {
            LoadGraphics();
        }

        private static void LoadGraphics()
        {
            var resX = SaveManager.LoadInt(ResolutionXKey, Screen.width);
            var resY = SaveManager.LoadInt(ResolutionYKey, Screen.height);
            var mode = (FullScreenMode)SaveManager.LoadInt(FullScreenModeKey, (int)Screen.fullScreenMode);
            var vSyncCount = SaveManager.LoadInt(VSyncCountKey, QualitySettings.vSyncCount);
            var msaa = SaveManager.LoadInt(AntiAliasingKey, GetMsaa());

            Screen.SetResolution(resX, resY, mode);
            QualitySettings.vSyncCount = Mathf.Clamp(vSyncCount, 0, 4);
            SetMsaa(msaa);
        }

        public static void LoadScene(string sceneName)
        {
            SceneManager.LoadScene(sceneName);
        }

        // ---------------- Graphics settings (apply + save) ----------------

        public static void ApplyResolution(int width, int height)
        {
            Screen.SetResolution(width, height, Screen.fullScreenMode);
            // Save what was requested: Screen.width/height only update next frame.
            SaveManager.SaveInt(ResolutionXKey, width);
            SaveManager.SaveInt(ResolutionYKey, height);
        }

        public static void ApplyDisplayMode(FullScreenMode mode)
        {
            Screen.fullScreenMode = mode;
            SaveManager.SaveInt(FullScreenModeKey, (int)mode);
        }

        public static void ApplyVSync(int vSyncCount)
        {
            QualitySettings.vSyncCount = Mathf.Clamp(vSyncCount, 0, 4);
            SaveManager.SaveInt(VSyncCountKey, QualitySettings.vSyncCount);
        }

        /// <summary>Sets MSAA sample count (1 = off, 2, 4 or 8) and saves it.</summary>
        public static void ApplyMsaa(int sampleCount)
        {
            SetMsaa(sampleCount);
            SaveManager.SaveInt(AntiAliasingKey, GetMsaa());
        }

        /// <summary>Current MSAA sample count (1 = off).</summary>
        public static int GetMsaa()
        {
            return GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp
                ? urp.msaaSampleCount
                : Mathf.Max(1, QualitySettings.antiAliasing);
        }

        private static void SetMsaa(int sampleCount)
        {
            sampleCount = sampleCount switch
            {
                <= 1 => 1,
                2 => 2,
                <= 4 => 4,
                _ => 8
            };

            // URP takes MSAA from the active pipeline asset; QualitySettings.antiAliasing is ignored.
            // Note: in the Editor this edits the URP asset itself, so the change persists after Play mode.
            if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp)
                urp.msaaSampleCount = sampleCount;
            else
                QualitySettings.antiAliasing = sampleCount == 1 ? 0 : sampleCount;
        }
    }
}
