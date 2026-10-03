using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Core.Bootstrap.EditorTools
{
    /// <summary>
    /// Editor-only: makes Play always start from the bootstrap scene (the first enabled scene
    /// in Build Settings) so managers exist, then Bootstrapper returns to whatever scene was
    /// open. Toggle with Tools > WavelineBase > Always Start From Bootstrap (on by default).
    /// Has no effect on builds.
    /// </summary>
    [InitializeOnLoad]
    public static class BootstrapPlayModeStarter
    {
        private const string MenuPath = "Tools/WavelineBase/Always Start From Bootstrap";
        private const string EnabledPrefKey = "WavelineBase.Bootstrap.AlwaysStartFromBootstrap";

        private static bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledPrefKey, true);
            set => EditorPrefs.SetBool(EnabledPrefKey, value);
        }

        static BootstrapPlayModeStarter()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            // playModeStartScene doesn't survive domain reloads, so re-apply after each one.
            EditorApplication.delayCall += ApplyStartScene;
        }

        [MenuItem(MenuPath)]
        private static void Toggle()
        {
            Enabled = !Enabled;
            ApplyStartScene();
        }

        [MenuItem(MenuPath, true)]
        private static bool ToggleValidate()
        {
            // Fully qualified: the project's own "Menu" namespace would shadow UnityEditor.Menu.
            UnityEditor.Menu.SetChecked(MenuPath, Enabled);
            return !EditorApplication.isPlayingOrWillChangePlaymode;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.ExitingEditMode) return;

            SessionState.EraseString(Bootstrapper.EditorReturnSceneKey);
            ApplyStartScene();
            if (!Enabled || EditorSceneManager.playModeStartScene == null) return;

            // Play mode will load scenes from disk, so unsaved edits wouldn't show up. Offer to save;
            // if the user cancels, cancel entering Play mode too.
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                EditorApplication.isPlaying = false;
                return;
            }

            var activePath = EditorSceneManager.GetActiveScene().path;
            var bootstrapPath = AssetDatabase.GetAssetPath(EditorSceneManager.playModeStartScene);
            if (!string.IsNullOrEmpty(activePath) && activePath != bootstrapPath)
                SessionState.SetString(Bootstrapper.EditorReturnSceneKey, activePath);
        }

        private static void ApplyStartScene()
        {
            if (!Enabled)
            {
                EditorSceneManager.playModeStartScene = null;
                return;
            }

            var bootstrapPath = EditorBuildSettings.scenes.FirstOrDefault(s => s.enabled)?.path;
            var bootstrapScene = string.IsNullOrEmpty(bootstrapPath)
                ? null
                : AssetDatabase.LoadAssetAtPath<SceneAsset>(bootstrapPath);

            if (bootstrapScene == null)
                Debug.LogWarning("[BootstrapPlayModeStarter] No enabled scene in Build Settings; Play will start from the open scene.");

            EditorSceneManager.playModeStartScene = bootstrapScene;
        }
    }
}
