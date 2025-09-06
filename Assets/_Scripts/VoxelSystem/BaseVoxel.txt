// BaseVoxel.cs (corrigido e otimizado)
// Autor: ChatGPT — otimização para reduzir mudanças desnecessárias e chamadas caras.

using System.Runtime.CompilerServices;
using UnityEngine;

/// <summary>
/// Tipos de voxels. Mantido separado e simples.
/// </summary>
public enum VoxelType : byte
{
    Floor = 0,
    Ceiling = 1,
    Wall_North = 2,
    Wall_South = 3,
    Wall_East = 4,
    Wall_West = 5,
    Pillar = 6,
    ClosedBox = 7,
    Empty = 255
}

/// <summary>
/// Classe base para todos os voxels.
/// - Mantém cache interno do último tipo/isSolid para evitar reconfigurações desnecessárias.
/// - Fornece contrato protegido OnInitialize(...) que subclasses devem implementar.
/// - Minimiza alocações e branches quando possível.
/// </summary>
[DisallowMultipleComponent]
public abstract class BaseVoxel : MonoBehaviour
{
    // Estado cacheado — leitura rápida e sem alocações.
    private VoxelType _type;
    private bool _isSolid;
    private bool _initialized;

    /// <summary>Tipo atual do voxel (readonly público).</summary>
    public VoxelType Type
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _type;
    }

    /// <summary>Se o voxel considera-se "sólido".</summary>
    public bool IsSolid
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _isSolid;
    }

    /// <summary>
    /// Inicializa o voxel. Evita re-trabalhos se o tipo e isSolid não mudarem.
    /// Subclasses devem implementar OnInitialize(...) para definir comportamento visual/colisional.
    /// </summary>
    /// <param name="type">Tipo do voxel</param>
    /// <param name="isSolid">Se deve tratar como sólido</param>
    public virtual void Initialize(VoxelType type, bool isSolid = false)
    {
        // Evita re-configurar se nada mudou — muito útil ao gerar salas grandes.
        if (_initialized && _type == type && _isSolid == isSolid)
        {
            return;
        }

        _type = type;
        _isSolid = isSolid;
        _initialized = true;

        // Delega o trabalho específico da implementação para as subclasses.
        OnInitialize(type, isSolid);
    }

    /// <summary>
    /// Método que as subclasses devem implementar para aplicar alterações ao GameObject.
    /// Use este ponto para:
    /// - ativar/desativar renderers (evite SetActive em massa quando possível)
    /// - ajustar colisores
    /// - aplicar material/cores (preferir MaterialPropertyBlock para menos instâncias)
    /// </summary>
    protected abstract void OnInitialize(VoxelType type, bool isSolid);

    /// <summary>
    /// Força re-aplicação mesmo que os parâmetros sejam iguais — útil em casos onde
    /// algo externo (ex: iluminação global) mudou e é necessário reprocessar.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ForceReinitialize()
    {
        _initialized = false;
        Initialize(_type, _isSolid);
    }
}
//