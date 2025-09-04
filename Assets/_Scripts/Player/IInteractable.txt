using UnityEngine;

/// <summary>
/// Define a interface para qualquer objeto que possa ser interagido pelo jogador.
/// Esta abordagem de interface é altamente flexível e otimizada, pois permite que qualquer
/// componente, incluindo os já existentes 'SimpleVoxel', 'CompositeVoxel' ou 'MicroVoxel',
/// implemente a lógica de interação sem precisar alterar sua hierarquia de herança.
/// </summary>
public interface IInteractable
{
    /// <summary>
    /// Um texto descritivo para a interação, que pode ser exibido na UI.
    /// Ex: "Abrir Porta", "Ativar Cristal".
    /// Usar uma propriedade permite que o texto seja dinâmico se necessário.
    /// </summary>
    string InteractionPrompt { get; }

    /// <summary>
    /// Executa a ação principal de interação com o objeto.
    /// </summary>
    /// <param name="interactor">O GameObject que está realizando a interação (geralmente o jogador).</param>
    /// <returns>Retorna 'true' se a interação foi concluída com sucesso, 'false' caso contrário.</returns>
    bool Interact(GameObject interactor);

    /// <summary>
    /// Chamado pelo sistema de interação quando o jogador foca neste objeto.
    /// Ideal para feedback visual, como realçar o objeto ou exibir o 'InteractionPrompt'.
    /// A implementação deve ser leve (ex: iniciar um fade de cor, que já é otimizado nos voxels).
    /// </summary>
    void OnFocusEnter();

    /// <summary>
    /// Chamado pelo sistema de interação quando o jogador deixa de focar neste objeto.
    /// Usado para reverter o feedback visual aplicado em 'OnFocusEnter'.
    /// </summary>
    void OnFocusExit();
}