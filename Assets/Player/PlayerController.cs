using Player.Input;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Player
{
    [RequireComponent(typeof(PlayerInput))]
    [RequireComponent(typeof(CharacterController))]
    public class PlayerController : MonoBehaviour
    {
        [SerializeField] private float moveSpeed = 5f;

        private PlayerInput _playerInput;
        private CharacterController _cc;

        private InputAction _moveAction;
        private InputAction _jumpAction;
        private InputAction _attackAction;

        private void Awake()
        {
            _playerInput = GetComponent<PlayerInput>();
            _cc = GetComponent<CharacterController>();

            _moveAction = _playerInput.actions["Player/Move"];
            _jumpAction = _playerInput.actions["Player/Jump"];
            _attackAction = _playerInput.actions["Player/Attack"];
        }

        private void OnEnable()
        {
            _jumpAction.performed += OnJump;
            _attackAction.performed += OnAttack;
        }

        private void OnDisable()
        {
            _jumpAction.performed -= OnJump;
            _attackAction.performed -= OnAttack;
        }

        private void Update()
        {
            Vector2 moveInput = _moveAction.ReadValue<Vector2>();
            Vector3 moveDirection = new Vector3(moveInput.x, 0, moveInput.y).normalized;
            _cc.Move(moveDirection * (moveSpeed * Time.deltaTime));
        }

        /// <summary>Function for use when pressing a rebind button.</summary>
        public void StartRebind(string actionName, int bindingIndex)
        {
            InputManager.Instance.StartRebind(_playerInput.playerIndex, actionName, bindingIndex);
        }

        private void OnJump(InputAction.CallbackContext ctx) { /* jump logic */ }
        private void OnAttack(InputAction.CallbackContext ctx) { /* attack logic */ }
    }
}
