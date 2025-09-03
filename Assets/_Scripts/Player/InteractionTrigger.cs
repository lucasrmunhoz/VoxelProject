using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Componente para ser adicionado a um GameObject do jogador (ex: um child com um SphereCollider).
/// Responsável por detectar e manter uma lista de todos os IInteractables que entram em seu alcance.
/// Essa abordagem é altamente otimizada, pois:
/// 1. Usa a física do Unity (OnTriggerEnter/Exit), que é muito mais rápida do que raycasts contínuos ou SphereCasts.
/// 2. Filtra objetos por layer, ignorando colisões desnecessárias.
/// 3. Fornece uma pequena lista de alvos para o script PlayerInteraction processar, em vez de iterar sobre todos os objetos da cena.
/// </summary>
[RequireComponent(typeof(Collider))]
public class InteractionTrigger : MonoBehaviour
{
    [Tooltip("Define em quais layers os objetos interativos se encontram. Essencial para performance.")]
    [SerializeField] private LayerMask _interactableLayers;

    // Usar um HashSet é ideal aqui:
    // - Previne a adição de duplicatas automaticamente.
    // - Oferece adição e remoção em tempo O(1), muito mais rápido que List<T> para coleções que mudam com frequência.
    private readonly HashSet<IInteractable> _nearbyInteractables = new HashSet<IInteractable>();

    /// <summary>
    /// Fornece acesso somente leitura à coleção de interativos próximos.
    /// Outros scripts podem ler esta lista, mas não podem modificá-la diretamente, garantindo o encapsulamento.
    /// </summary>
    public IReadOnlyCollection<IInteractable> NearbyInteractables => _nearbyInteractables;

    private void Awake()
    {
        // Garante que o collider deste GameObject está configurado como trigger.
        // Sem isso, os métodos OnTriggerEnter/Exit não seriam chamados.
        var triggerCollider = GetComponent<Collider>();
        if (!triggerCollider.isTrigger)
        {
            // Avisa o desenvolvedor sobre a configuração incorreta e a corrige em tempo de execução para evitar falhas.
            Debug.LogWarning($"O Collider em '{gameObject.name}' precisa ser 'Is Trigger = true'. Corrigindo automaticamente.", this);
            triggerCollider.isTrigger = true;
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        // Primeira otimização: checar a layer do objeto ANTES de qualquer chamada a GetComponent.
        // A expressão `(_interactableLayers.value & (1 << other.gameObject.layer)) != 0` é uma forma
        // extremamente rápida de verificar se a layer do 'other' pertence à nossa LayerMask.
        if ((_interactableLayers.value & (1 << other.gameObject.layer)) == 0)
        {
            return;
        }

        // Busca pelo componente que implementa IInteractable.
        // Usar GetComponentInParent é mais robusto, pois permite que o collider de interação
        // esteja em um objeto filho do objeto que contém a lógica principal.
        var interactable = other.GetComponentInParent<IInteractable>();
        if (interactable != null)
        {
            _nearbyInteractables.Add(interactable);
        }
    }

    private void OnTriggerExit(Collider other)
    {
        // A mesma lógica de otimização se aplica na saída do trigger.
        if ((_interactableLayers.value & (1 << other.gameObject.layer)) == 0)
        {
            return;
        }
        
        var interactable = other.GetComponentInParent<IInteractable>();
        if (interactable != null)
        {
            // Se o objeto que saiu tem um IInteractable, ele é removido da lista de alvos próximos.
            _nearbyInteractables.Remove(interactable);
        }
    }

    private void OnDisable()
    {
        // Limpa a lista se o componente for desativado, para evitar referências nulas
        // ou interações "fantasmas" se ele for reativado depois.
        _nearbyInteractables.Clear();
    }
}