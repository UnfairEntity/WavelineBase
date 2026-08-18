using System;
using System.Collections.Generic;
using Core;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Player.Input
{
    /// <summary>
    /// PURPOSE: Owns local multiplayer join/leave and per-action rebinding.
    ///          Supports any number of local players from 1 upward (device-limited).
    /// DEPENDENCIES: Input System package. Requires a PlayerInputManager component on
    ///               this same GameObject, configured in the Inspector with the shared
    ///               Player Prefab (must have a PlayerInput component pointing at your
    ///               PlayerInputActions asset) and your desired Joining Behavior.
    /// EVENTS PUBLISHED: OnLocalPlayerJoined(PlayerInput), OnLocalPlayerLeft(PlayerInput),
    ///                    OnRebindStarted / OnRebindComplete / OnRebindCanceled(int playerIndex, string actionName)
    /// EVENTS SUBSCRIBED: PlayerInputManager.onPlayerJoined / onPlayerLeft
    /// PUBLIC API: JoinPlayer, RemovePlayer, TryGetPlayer, StartRebind, CancelRebind, ResetRebinds
    /// </summary>
    [RequireComponent(typeof(PlayerInputManager))]
    public class InputManager : Singleton<InputManager>
    {
        [Header("Startup")]
        [Tooltip("Joins one player automatically at Start so single-player games work with zero extra setup. Leave off for pure 'press any button to join' co-op flows.")]
        [SerializeField] private bool autoJoinFirstPlayer = true;

        [Header("Rebind")]
        [Tooltip("Control paths excluded from rebind candidates so incidental mouse/stick noise can't get captured.")]
        [SerializeField]
        private string[] rebindExclusions = { "<Mouse>/position", "<Mouse>/delta", "<Pointer>/position" };

        private PlayerInputManager _playerInputManager;
        private readonly Dictionary<int, PlayerInput> _activePlayers = new();
        private InputActionRebindingExtensions.RebindingOperation _activeRebindOperation;

        public event Action<PlayerInput> OnLocalPlayerJoined;
        public event Action<PlayerInput> OnLocalPlayerLeft;
        public event Action<int, string> OnRebindStarted;
        public event Action<int, string> OnRebindComplete;
        public event Action<int, string> OnRebindCanceled;

        public IReadOnlyDictionary<int, PlayerInput> ActivePlayers => _activePlayers;
        public bool IsRebinding => _activeRebindOperation != null;

        protected override void Awake()
        {
            base.Awake();
            if (IsDuplicate) return;

            _playerInputManager = GetComponent<PlayerInputManager>();
            _playerInputManager.onPlayerJoined += HandlePlayerJoined;
            _playerInputManager.onPlayerLeft += HandlePlayerLeft;
        }

        private void Start()
        {
            if (autoJoinFirstPlayer && _activePlayers.Count == 0)
                JoinPlayer();
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            if (_playerInputManager != null)
            {
                _playerInputManager.onPlayerJoined -= HandlePlayerJoined;
                _playerInputManager.onPlayerLeft -= HandlePlayerLeft;
            }
            _activeRebindOperation?.Dispose();
        }

        // ---------------- Join / Leave ----------------

        /// <summary>Joins a new local player. Pass a device to join with a specific known controller (e.g. from a "press any button" prompt); omit to let the Input System pick any free device.</summary>
        public PlayerInput JoinPlayer(InputDevice pairedDevice = null)
        {
            return pairedDevice != null
                ? _playerInputManager.JoinPlayer(pairWithDevices: new[] { pairedDevice })
                : _playerInputManager.JoinPlayer();
        }

        public void RemovePlayer(int playerIndex)
        {
            if (_activePlayers.TryGetValue(playerIndex, out var playerInput))
                Destroy(playerInput.gameObject); // PlayerInputManager fires onPlayerLeft as part of this
        }

        public bool TryGetPlayer(int playerIndex, out PlayerInput playerInput)
            => _activePlayers.TryGetValue(playerIndex, out playerInput);

        private void HandlePlayerJoined(PlayerInput playerInput)
        {
            _activePlayers[playerInput.playerIndex] = playerInput;
            
            if (playerInput.actions == null) return;
            string json = SaveManager.Instance.LoadString($"PlayerRebinds_{playerInput.playerIndex}", string.Empty);
            if (!string.IsNullOrEmpty(json))
                playerInput.actions.LoadBindingOverridesFromJson(json);
            
            OnLocalPlayerJoined?.Invoke(playerInput);
        }

        private void HandlePlayerLeft(PlayerInput playerInput)
        {
            _activePlayers.Remove(playerInput.playerIndex);
            OnLocalPlayerLeft?.Invoke(playerInput);
        }

        // ---------------- Rebinding ----------------

        /// <summary>
        /// Starts an interactive rebind for one binding on one player's actions.
        /// For composite bindings (WASD-style), pass the composite PART's binding index, not the composite root's.
        /// </summary>
        public void StartRebind(int playerIndex, string actionName, int bindingIndex, Action onComplete = null, Action onCanceled = null)
        {
            if (IsRebinding)
            {
                Debug.LogWarning("[InputManager] A rebind is already in progress; ignoring new request.");
                return;
            }

            if (!_activePlayers.TryGetValue(playerIndex, out var playerInput))
            {
                Debug.LogError($"[InputManager] No active player at index {playerIndex}.");
                return;
            }

            InputAction action = playerInput.actions.FindAction(actionName);
            if (action == null)
            {
                Debug.LogError($"[InputManager] Action '{actionName}' not found.");
                return;
            }

            action.Disable();

            var rebind = action.PerformInteractiveRebinding(bindingIndex)
                .WithCancelingThrough("<Keyboard>/escape")
                .OnMatchWaitForAnother(0.1f); // debounces accidental double-triggers from one physical press

            foreach (string exclusion in rebindExclusions)
                rebind = rebind.WithControlsExcluding(exclusion);

            rebind.OnComplete(op => FinishRebind(playerIndex, actionName, playerInput, action, canceled: false, onComplete, onCanceled))
                  .OnCancel(op => FinishRebind(playerIndex, actionName, playerInput, action, canceled: true, onComplete, onCanceled));

            _activeRebindOperation = rebind.Start();
            OnRebindStarted?.Invoke(playerIndex, actionName);
        }

        public void CancelRebind() => _activeRebindOperation?.Cancel();

        private void FinishRebind(int playerIndex, string actionName, PlayerInput playerInput, InputAction action,
            bool canceled, Action onComplete, Action onCanceled)
        {
            action.Enable();
            _activeRebindOperation?.Dispose();
            _activeRebindOperation = null;

            if (canceled)
            {
                OnRebindCanceled?.Invoke(playerIndex, actionName);
                onCanceled?.Invoke();
                return;
            }
            
            if (playerInput.actions == null) return;
            SaveManager.Instance.SaveString($"PlayerRebinds_{playerIndex}", playerInput.actions.SaveBindingOverridesAsJson());
            
            OnRebindComplete?.Invoke(playerIndex, actionName);
            onComplete?.Invoke();
        }

        public void ResetRebinds(int playerIndex)
        {
            if (!_activePlayers.TryGetValue(playerIndex, out var playerInput)) return;
            playerInput.actions.RemoveAllBindingOverrides();

            SaveManager.Instance.DeleteSetting($"PlayerRebinds_{playerIndex}");
        }
    }
}
