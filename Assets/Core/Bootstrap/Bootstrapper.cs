using UnityEngine;
using UnityEngine.SceneManagement;

namespace Core.Bootstrap
{
    public class Bootstrapper : MonoBehaviour
    {
        [SerializeField] private string firstSceneAfterBoot;
 
        // References to manager prefabs that will persist
        [SerializeField] private GameObject gameManagerPrefab;
        [SerializeField] private GameObject audioManagerPrefab;
        [SerializeField] private GameObject inputManagerPrefab;
        [SerializeField] private GameObject menuManagerPrefab;
        [SerializeField] private GameObject saveManagerPrefab;
 
        private void Awake()
        {
            // Instantiate and persist each manager
            InstantiateAndPersist(gameManagerPrefab);
            InstantiateAndPersist(audioManagerPrefab);
            InstantiateAndPersist(inputManagerPrefab);
            InstantiateAndPersist(menuManagerPrefab);
            InstantiateAndPersist(saveManagerPrefab);
        
            // Load the first real scene
            if (firstSceneAfterBoot == null) return;
            SceneManager.LoadScene(firstSceneAfterBoot);
        }
 
        private void InstantiateAndPersist(GameObject prefab)
        {
            if (prefab == null) return;
            var instance = Instantiate(prefab);
            DontDestroyOnLoad(instance);
        }
    }
}