// IInteractable.cs
using UnityEngine;

/// <summary>
/// Contrato mínimo para objetos interativos do jogador.
/// Implementações típicas: Interruptor (InteractableButton), Portas (VoxelDoorController), etc.
/// </summary>
public interface IInteractable
{
    /// <summary>
    /// Retorna true se o 'interactor' (ex.: Player) pode interagir AGORA.
    /// Use para validar distância, ângulo, estado, cooldown, etc.
    /// </summary>
    bool CanInteract(Transform interactor);

    /// <summary>
    /// Executa a interação (efeito imediato ou início de animação/estado).
    /// </summary>
    void Interact(Transform interactor);

    /// <summary>
    /// Texto curto para UI/Prompt (ex.: "[E] Ligar Luz").
    /// </summary>
    string Prompt { get; }
}
