using UnityEngine;
using UnityEngine.SceneManagement;

namespace Core.Bootstrap
{
    public class Bootstrapper : MonoBehaviour
    {
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