// RoomTriggerController.cs
// Componente responsável por detectar a entrada do jogador em uma sala
// e notificar o GameFlowManager para iniciar a lógica de transição.
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(Collider))] // Garante que há um colisor para funcionar como trigger
public class RoomTriggerController : MonoBehaviour
{
    [Header("Configuração")]
    [Tooltip("A tag do GameObject do jogador que ativará o trigger.")]
    [SerializeField] private string playerTag = "Player";

    // --- Referências Internas ---
    private GameFlowManager _gameFlowManager;
    private bool _hasBeenTriggered = false; // Garante que o trigger só dispare uma vez por ativação

    /// <summary>
    /// Na inicialização, encontra e armazena a referência ao GameFlowManager para otimização.
    /// </summary>
    void Start()
    {
        // Encontra o GameFlowManager na cena. Esta é uma forma robusta de obter a referência
        // sem precisar de atribuição manual no Inspector.
        _gameFlowManager = FindObjectOfType<GameFlowManager>();

        if (_gameFlowManager == null)
        {
            Debug.LogError("[RoomTriggerController] Não foi possível encontrar uma instância do GameFlowManager na cena!", this);
            // Desativa o componente se o gerenciador principal não for encontrado, para evitar erros.
            this.enabled = false; 
        }
    }

    /// <summary>
    /// Chamado pela Unity quando outro Collider entra neste trigger.
    /// </summary>
    /// <param name="other">O Collider que entrou na área.</param>
    private void OnTriggerEnter(Collider other)
    {
        // Verifica se o GameFlowManager foi encontrado, se o trigger já não foi ativado
        // e se o objeto que entrou tem a tag correta do jogador.
        if (_gameFlowManager != null && !_hasBeenTriggered && other.CompareTag(playerTag))
        {
            // Marca como ativado para evitar chamadas múltiplas.
            _hasBeenTriggered = true;

            if (_gameFlowManager.verbose)
            {
                Debug.Log($"[RoomTriggerController] Player entrou no trigger da sala '{this.gameObject.name}'. Notificando GameFlowManager.");
            }

            // Notifica o GameFlowManager que o jogador entrou nesta sala,
            // passando o transform do container da sala (este GameObject).
            _gameFlowManager.OnPlayerEnterRoom(this.transform);
        }
    }

    /// <summary>
    /// Quando o objeto é desativado e reativado (ex: pooling), reseta o estado do trigger.
    /// </summary>
    private void OnEnable()
    {
        _hasBeenTriggered = false;
    }
}