using UnityEngine;
using UnityEngine.Events;
using System.Collections;

/// <summary>
/// Implementação da interface IInteractable para um botão simples.
/// Este componente permite que o jogador interaja com o objeto, disparando um evento
/// e fornecendo feedback visual, sendo totalmente compatível com o sistema PlayerInteraction.
/// </summary>
[RequireComponent(typeof(Collider))] // Garante que o objeto tenha um collider para ser detectado pelo InteractionTrigger.
public class InteractableButton : MonoBehaviour, IInteractable
{
    [Header("Feedback Visual")]
    [Tooltip("O Renderer do botão cujo material será alterado para feedback visual.")]
    [SerializeField] private Renderer _buttonRenderer;
    [SerializeField] private Color _defaultColor = Color.red;
    [SerializeField] private Color _focusColor = Color.green;
    [SerializeField] private Color _interactColor = Color.white;
    [Tooltip("Duração do feedback visual de interação antes de retornar à cor de foco/padrão.")]
    [SerializeField] private float _interactFeedbackDuration = 0.2f;

    [Header("Evento de Ativação")]
    [Tooltip("Evento disparado quando o jogador interage com sucesso com o botão.")]
    public UnityEvent OnActivated;

    // Otimização: MaterialPropertyBlock evita criar instâncias de material, melhorando a performance.
    private MaterialPropertyBlock _mpb;
    
    // Otimização: Acessar propriedades do shader por ID é muito mais rápido do que por string.
    private static readonly int ColorID = Shader.PropertyToID("_Color");

    private Coroutine _resetColorCoroutine;
    private bool _isFocused = false;

    // --- PATCH: Implementações mínimas da interface IInteractable ---
    // Propriedade da interface IInteractable. Fornece o texto para a UI.
    public string Prompt => "Pressione [E]";

    /// <summary>
    /// Método da interface IInteractable. Verifica se a interação é possível.
    /// </summary>
    public bool CanInteract(Transform interactor)
    {
        // Mantemos a lógica neutra: habilitado e com interactor válido.
        return isActiveAndEnabled && interactor != null;
    }

    /// <summary>
    /// Método chamado quando o jogador pressiona a tecla de interação enquanto foca neste objeto.
    /// Este método implementa a interface e aciona a lógica original do botão.
    /// </summary>
    public void Interact(Transform interactor)
    {
        // Dispara o evento de forma segura. Outros sistemas podem se inscrever neste evento.
        OnActivated?.Invoke();
        Debug.Log($"Botão pressionado por '{interactor.name}'.");

        // Para a corrotina anterior se ela estiver em execução, para evitar comportamentos inesperados.
        if (_resetColorCoroutine != null)
        {
            StopCoroutine(_resetColorCoroutine);
        }

        // Inicia a rotina de feedback visual sem usar Invoke.
        _resetColorCoroutine = StartCoroutine(InteractionFeedbackCoroutine());
        
        // A interface agora é void, então não há retorno. A lógica de sucesso é implícita.
    }
     // --- fim do PATCH ---

    private void Awake()
    {
        _mpb = new MaterialPropertyBlock();

        // Garante que o Renderer seja o do próprio objeto se nenhum for atribuído.
        if (_buttonRenderer == null)
        {
            TryGetComponent(out _buttonRenderer);
        }
        
        // Aplica a cor padrão inicial de forma otimizada.
        SetColor(_defaultColor);
    }

    private void OnValidate()
    {
        // Facilita a configuração no editor, buscando o componente automaticamente.
        if (_buttonRenderer == null)
        {
            _buttonRenderer = GetComponent<Renderer>();
        }
    }

    /// <summary>
    /// Chamado pelo PlayerInteraction quando o objeto ganha foco.
    /// </summary>
    public void OnFocusEnter()
    {
        _isFocused = true;
        SetColor(_focusColor);
    }

    /// <summary>
    /// Chamado pelo PlayerInteraction quando o objeto perde o foco.
    /// </summary>
    public void OnFocusExit()
    {
        _isFocused = false;
        SetColor(_defaultColor);
    }

    /// <summary>
    /// Aplica a cor ao renderer usando um MaterialPropertyBlock para otimização.
    /// </summary>
    private void SetColor(Color color)
    {
        if (_buttonRenderer == null) return;
        
        // Obtém o bloco de propriedades atual para não sobrescrever outras possíveis alterações.
        _buttonRenderer.GetPropertyBlock(_mpb);
        _mpb.SetColor(ColorID, color);
        _buttonRenderer.SetPropertyBlock(_mpb);
    }

    /// <summary>
    /// Corrotina que gerencia o feedback visual da interação.
    /// Muda a cor para a de interação e, após um tempo, retorna para a cor de foco ou padrão.
    /// </summary>
    private IEnumerator InteractionFeedbackCoroutine()
    {
        // 1. Mostra a cor de feedback da interação.
        SetColor(_interactColor);
        
        // 2. Aguarda a duração definida.
        yield return new WaitForSeconds(_interactFeedbackDuration);
        
        // 3. Retorna à cor apropriada (foco se ainda estiver focado, padrão caso contrário).
        SetColor(_isFocused ? _focusColor : _defaultColor);

        // Limpa a referência da corrotina.
        _resetColorCoroutine = null;
    }
}