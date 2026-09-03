using Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game
{
    public class GameManager : Singleton<GameManager>
    {
        public void LoadScene(string sceneName)
        {
            SceneManager.LoadScene(sceneName);
        }
    }
}