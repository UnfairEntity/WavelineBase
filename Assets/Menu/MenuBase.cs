using System.Collections;
using Game;
using UnityEngine;
using UnityEngine.UI;

namespace UI
{
    [RequireComponent(typeof(CanvasGroup))]
    public abstract class MenuBase : MonoBehaviour
    {
        protected CanvasGroup CanvasGroup { get; private set; }
 
        [SerializeField] protected float fadeTime = 0.25f;
        [SerializeField] private Selectable defaultSelectable;  // focused on open
 
        protected virtual void Awake()
            => CanvasGroup = GetComponent<CanvasGroup>();
 
        // Call these through MenuManager — not directly
        public virtual void Open()
        {
            gameObject.SetActive(true);
            StartCoroutine(FadeIn());
        }

        public virtual void Close()
        {
            StartCoroutine(FadeOut());
        }

        public void FocusDefaultControl()
        {
            defaultSelectable?.Select();
        }

        protected IEnumerator FadeIn()
        {
            CanvasGroup.interactable   = false;
            CanvasGroup.blocksRaycasts = true;
            float t = 0;
            
            while (t < fadeTime)
            {
                CanvasGroup.alpha = t / fadeTime;
                t += Time.unscaledDeltaTime;   // unscaled: works even when paused
                yield return null;
            }
            CanvasGroup.alpha          = 1f;
            CanvasGroup.interactable   = true;
            FocusDefaultControl();
        }

        protected IEnumerator FadeOut()
        {
            CanvasGroup.interactable = false;
            float t = fadeTime;
            while (t > 0)
            {
                CanvasGroup.alpha = t / fadeTime;
                t -= Time.unscaledDeltaTime;
                yield return null;
            }

            CanvasGroup.alpha = 0f;
            CanvasGroup.blocksRaycasts = false;
            gameObject.SetActive(false);
        }
    }
}