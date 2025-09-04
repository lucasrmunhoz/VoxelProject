// AUTOR: Gemini
// DATA: 16 de Agosto de 2025 (VERSÃO CORRIGIDA E EFICIENTE)
// PROPÓSITO: Controlar o movimento e a visão do jogador em primeira pessoa.
// DESCRIÇÃO: Este script já segue boas práticas de performance para um CharacterController.
//            - Referências são cacheadas em Awake.
//            - Movimento e gravidade são combinados em uma única chamada .Move() por frame.

using UnityEngine;

[RequireComponent(typeof(CharacterController))]
public class PlayerMovement : MonoBehaviour
{
    #region CONFIGURAÇÕES (INSPECTOR)
    // ... (nenhuma alteração aqui)
    [Header("1. Configurações de Movimento")]
    [Tooltip("A velocidade com que o jogador se move pelo cenário.")]
    [SerializeField] private float moveSpeed = 5f;
    [Tooltip("A força da gravidade aplicada ao jogador.")]
    [SerializeField] private float gravity = -9.81f;

    [Header("2. Configurações da Câmera")]
    [Tooltip("A sensibilidade da rotação da câmera com o mouse.")]
    [SerializeField] private float mouseSensitivity = 100f;
    #endregion

    #region ESTADO INTERNO
    private CharacterController _characterController;
    private Camera _playerCamera;
    private float _cameraVerticalRotation = 0f;
    private float _verticalVelocity;
    #endregion

    #region CICLO DE VIDA (UNITY)
    private void Awake()
    {
        _characterController = GetComponent<CharacterController>();
        _playerCamera = GetComponentInChildren<Camera>();

        if (_playerCamera == null)
        {
            Debug.LogError($"[{nameof(PlayerMovement)}] Nenhuma câmera encontrada como filha do jogador! A visão não funcionará.", this);
        }

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private void Update()
    {
        // A ordem está correta: primeiro processa o input de visão, depois o de movimento.
        HandleCameraLook();
        HandleMovementAndGravity();
    }
    #endregion

    #region LÓGICA DE CONTROLE
    private void HandleCameraLook()
    {
        if (_playerCamera == null) return;

        float mouseX = Input.GetAxis("Mouse X") * mouseSensitivity * Time.deltaTime;
        transform.Rotate(Vector3.up * mouseX);

        float mouseY = Input.GetAxis("Mouse Y") * mouseSensitivity * Time.deltaTime;
        _cameraVerticalRotation -= mouseY;
        _cameraVerticalRotation = Mathf.Clamp(_cameraVerticalRotation, -90f, 90f);
        _playerCamera.transform.localRotation = Quaternion.Euler(_cameraVerticalRotation, 0f, 0f);
    }
    
    /// <summary>
    /// Esta função já é otimizada. Ela calcula todos os vetores de força (horizontal e vertical)
    /// e os aplica de uma só vez na chamada .Move(), o que é a forma mais eficiente de usar o
    /// CharacterController.
    /// </summary>
    private void HandleMovementAndGravity()
    {
        if (_characterController.isGrounded && _verticalVelocity < 0)
        {
            _verticalVelocity = -2f; // Mantém o jogador "colado" no chão.
        }
        
        _verticalVelocity += gravity * Time.deltaTime;

        float horizontalInput = Input.GetAxis("Horizontal");
        float verticalInput = Input.GetAxis("Vertical");
        
        Vector3 horizontalMove = (transform.right * horizontalInput + transform.forward * verticalInput) * moveSpeed;
        Vector3 finalMove = horizontalMove + (Vector3.up * _verticalVelocity);
        
        _characterController.Move(finalMove * Time.deltaTime);
    }
    #endregion
}