using System;
using UnityEngine;

/// <summary>
/// Gerencia a lógica de interação do jogador.
/// - Deve ser adicionado ao GameObject principal do jogador.
/// - Determina qual 'IInteractable' está em foco com base na direção da câmera e na lista do InteractionTrigger.
/// - Gerencia os estados de foco (OnFocusEnter/OnFocusExit) para fornecer feedback visual.
/// - Processa o input do jogador para acionar a ação de interação.
/// - Comunica-se com a UI de forma desacoplada através de um evento.
/// </summary>
public class PlayerInteraction : MonoBehaviour
{
    [Header("Componentes Essenciais")]
    [Tooltip("Referência ao InteractionTrigger que detecta os interativos ao alcance. Geralmente em um GameObject filho do jogador.")]
    [SerializeField] private InteractionTrigger _interactionTrigger;
    [Tooltip("Transform da câmera do jogador, usado para calcular a direção do olhar.")]
    [SerializeField] private Transform _playerCameraTransform;

    [Header("Configurações de Interação")]
    [Tooltip("Tecla que o jogador pressionará para interagir.")]
    [SerializeField] private KeyCode _interactKey = KeyCode.E;
    [Tooltip("Distância máxima em que uma interação pode ocorrer. Objetos dentro do trigger, mas além desta distância, serão ignorados.")]
    [SerializeField, Min(0)] private float _maxInteractionDistance = 4f;
    [Tooltip("O quão 'centralizado' um objeto precisa estar para ser focado (1 = perfeitamente no centro, -1 = atrás). Um valor alto torna a mira mais precisa.")]
    [SerializeField, Range(0f, 1f)] private float _focusPrecision = 0.85f;


    /// <summary>
    /// Evento para notificar a UI sobre qual texto de interação exibir.
    /// Uma UI pode se inscrever neste evento para mostrar/ocultar prompts dinamicamente.
    /// Envia o texto do prompt do interativo focado, ou uma string vazia se não houver foco.
    /// </summary>
    public event Action<string> OnInteractionPromptUpdate;

    // Armazena a referência do interativo atualmente em foco.
    private IInteractable _focusedInteractable;

    private void OnValidate()
    {
        // Garante que referências essenciais não sejam nulas no editor.
        if (_interactionTrigger == null)
            _interactionTrigger = GetComponentInChildren<InteractionTrigger>();

        if (_playerCameraTransform == null && Camera.main != null)
            _playerCameraTransform = Camera.main.transform;
    }

    private void Update()
    {
        // A lógica principal é dividida em duas partes para clareza.
        FindBestTarget();
        HandleInteractionInput();
    }

    /// <summary>
    /// Itera sobre os interativos próximos (fornecidos pelo InteractionTrigger)
    /// e seleciona o melhor alvo com base na distância e no ângulo da visão do jogador.
    /// </summary>
    private void FindBestTarget()
    {
        IInteractable bestTarget = null;
        float highestScore = -1f; // Usamos um "score" para decidir o melhor alvo.

        // Otimização: se não há nada no trigger, não há o que processar.
        if (_interactionTrigger == null || _interactionTrigger.NearbyInteractables.Count == 0)
        {
            UpdateFocus(null);
            return;
        }

        Vector3 cameraPos = _playerCameraTransform.position;
        Vector3 cameraFwd = _playerCameraTransform.forward;

        foreach (var interactable in _interactionTrigger.NearbyInteractables)
        {
            // A interface não pode ser nula, mas o MonoBehaviour pode ter sido destruído.
            var interactableMono = interactable as MonoBehaviour;
            if (interactableMono == null) continue;

            Vector3 targetPos = interactableMono.transform.position;
            Vector3 directionToTarget = targetPos - cameraPos;

            // Otimização: Usa 'sqrMagnitude' que é mais rápido que 'magnitude' (evita a raiz quadrada).
            if (directionToTarget.sqrMagnitude > _maxInteractionDistance * _maxInteractionDistance)
            {
                continue; // Alvo muito distante.
            }
            
            directionToTarget.Normalize();

            // Usa o produto escalar (Dot Product) para medir o quão alinhado o alvo está com a visão do jogador.
            // O valor vai de -1 (atrás) a 1 (exatamente na frente).
            float dotProduct = Vector3.Dot(cameraFwd, directionToTarget);

            // Otimização: Se o alvo não está no cone de visão definido pela precisão, ignora.
            if (dotProduct < _focusPrecision)
            {
                continue;
            }

            // O objeto com o maior 'dotProduct' (mais centralizado) é considerado o melhor alvo.
            if (dotProduct > highestScore)
            {
                highestScore = dotProduct;
                bestTarget = interactable;
            }
        }

        UpdateFocus(bestTarget);
    }

    /// <summary>
    /// Atualiza o estado de foco, chamando OnFocusExit e OnFocusEnter apenas quando necessário.
    /// </summary>
    private void UpdateFocus(IInteractable newTarget)
    {
        // Se o alvo não mudou, não faz nada. Isso evita chamadas desnecessárias a cada frame.
        if (_focusedInteractable == newTarget) return;

        // Notifica o alvo antigo que ele perdeu o foco.
        _focusedInteractable?.OnFocusExit();

        _focusedInteractable = newTarget;

        // Notifica o novo alvo que ele ganhou o foco.
        _focusedInteractable?.OnFocusEnter();
        
        // Dispara o evento para a UI, que se encarregará de exibir o prompt.
        OnInteractionPromptUpdate?.Invoke(_focusedInteractable?.InteractionPrompt ?? string.Empty);
    }

    /// <summary>
    /// Verifica se a tecla de interação foi pressionada e, se houver um alvo focado, executa a interação.
    /// </summary>
    private void HandleInteractionInput()
    {
        if (Input.GetKeyDown(_interactKey) && _focusedInteractable != null)
        {
            _focusedInteractable.Interact(this.gameObject);
        }
    }

    private void OnDisable()
    {
        // Garante que, ao desativar este componente, qualquer foco ativo seja limpo.
        UpdateFocus(null);
    }
}