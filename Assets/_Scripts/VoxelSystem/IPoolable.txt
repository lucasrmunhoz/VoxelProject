// Assets/_Scripts/VoxelSystem/IPoolable.cs
using UnityEngine;

/// <summary>
/// Contrato opcional para objetos que participam do ciclo de vida do VoxelPool.
/// Nenhum componente é obrigado a implementar; se não houver IPoolable,
/// o pool simplesmente não invoca hooks.
/// </summary>
public interface IPoolable
{
    /// <summary>Chamado imediatamente antes do objeto ser (re)ativado pelo pool.</summary>
    void OnBeforeSpawn();

    /// <summary>Chamado imediatamente após o objeto ser (re)ativado pelo pool.</summary>
    void OnAfterSpawn();

    /// <summary>Chamado imediatamente antes do objeto ser desativado e devolvido ao pool.</summary>
    void OnBeforeDespawn();

    /// <summary>Chamado imediatamente após o objeto ser desativado e devolvido ao pool.</summary>
    void OnAfterDespawn();
}
