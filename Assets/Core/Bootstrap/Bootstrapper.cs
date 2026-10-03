using UnityEngine;
using UnityEngine.SceneManagement;

namespace Core.Bootstrap
{
    public class Bootstrapper : MonoBehaviour
    {
        /// <summary>Editor-only SessionState key: the scene that was open when Play was pressed.
        /// Written by BootstrapPlayModeStarter (Editor folder), read and cleared here.</summary>
        public const string EditorReturnSceneKey = "WavelineBase.Bootstrap.ReturnScenePath";

        [SerializeField] private string firstSceneAfterBoot;

        // References to manager prefabs that will persist
        [SerializeField] private GameObject[] managerPrefabs;

        private void Awake()
        {
            // Instantiate and persist each manager
            foreach (var prefab in managerPrefabs)
            {
                InstantiateAndPersist(prefab);
            }

#if UNITY_EDITOR
            // Play was pressed in another scene: go back to it instead of the normal first scene.
            if (TryLoadEditorReturnScene()) return;
#endif

            // Load the first real scene (serialized strings are never null, only empty)
            if (string.IsNullOrWhiteSpace(firstSceneAfterBoot))
            {
                Debug.LogWarning("[Bootstrapper] No first scene set; staying in the bootstrap scene.", this);
                return;
            }
            SceneManager.LoadScene(firstSceneAfterBoot);
        }

        private static void InstantiateAndPersist(GameObject prefab)
        {
            if (prefab == null) return;
            var instance = Instantiate(prefab);
            DontDestroyOnLoad(instance);
        }

#if UNITY_EDITOR
        private static bool TryLoadEditorReturnScene()
        {
            var path = UnityEditor.SessionState.GetString(EditorReturnSceneKey, string.Empty);
            if (string.IsNullOrEmpty(path)) return false;

            UnityEditor.SessionState.EraseString(EditorReturnSceneKey);
            // LoadSceneInPlayMode also works for scenes that aren't in the Build Settings list.
            UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(
                path, new LoadSceneParameters(LoadSceneMode.Single));
            return true;
        }
#endif
    }
}
