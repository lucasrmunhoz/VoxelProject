using System;
using System.Linq;
using UnityEngine;

/// <summary>
/// CompositeVoxel - voxel composto por "faces" (cada face agora é um GameObject que agrupa Renderers).
/// Objetivo de otimização:
/// - Minimizar chamadas caras (GetComponentsInChildren é feito apenas uma vez em Awake).
/// - Atualizar apenas o necessário usando bitmasks.
/// - Usar MaterialPropertyBlock para evitar instanciar materiais dinamicamente.
/// - API pequena e rápida para ser usada em geração em massa.
/// Compatível com BaseVoxel (usa OnInitialize).
/// </summary>
[DisallowMultipleComponent]
public class CompositeVoxel : BaseVoxel
{
    [Flags]
    public enum Face : byte
    {
        None   = 0,
        Top    = 1 << 0,
        Bottom = 1 << 1,
        North  = 1 << 2,
        South  = 1 << 3,
        East   = 1 << 4,
        West   = 1 << 5,
        All    = Top | Bottom | North | South | East | West
    }

    // MUDANÇA: Os campos agora esperam um GameObject (o organizador) em vez de um Renderer.
    [Header("Organizadores das Faces (GameObjects)")]
    [SerializeField] private GameObject topFaceGroup;
    [SerializeField] private GameObject bottomFaceGroup;
    [SerializeField] private GameObject northFaceGroup;
    [SerializeField] private GameObject southFaceGroup;
    [SerializeField] private GameObject eastFaceGroup;
    [SerializeField] private GameObject westFaceGroup;

    // Optional collider to toggle solidity without SetActive
    [SerializeField] private Collider primaryCollider;

    // MUDANÇA: O cache agora é um array de arrays de Renderers (um grupo para cada face).
    private Renderer[][] _renderersByFaceGroup = new Renderer[6][];
    private bool[] _rendererEnabledCache = new bool[6]; // cache do último estado (habilitado/desabilitado) para o GRUPO
    private MaterialPropertyBlock _mpb;
    private bool _isCacheBuilt = false;

    // Last applied mask (for quick diff)
    private Face _currentMask = Face.None;

    // Small enum index mapping for arrays
    private static readonly Face[] _faceOrder = new[] {
        Face.Top, Face.Bottom, Face.North, Face.South, Face.East, Face.West
    };

    #region Unity lifecycle & caching
    private void Awake()
    {
        // Cache MPB
        _mpb = new MaterialPropertyBlock();

        // MUDANÇA: Constrói o cache de todos os renderers filhos.
        BuildRendererCache();

        // If primaryCollider not assigned, try to obtain one on this GameObject (cheap)
        if (primaryCollider == null)
            primaryCollider = GetComponent<Collider>();

        // A inicialização do cache de visibilidade (_rendererEnabledCache)
        // é feita de forma implícita na primeira chamada de ApplyFaceMask.
    }

    // MUDANÇA: Novo método para encontrar e armazenar todos os renderers de cada grupo de face.
    private void BuildRendererCache()
    {
        if (_isCacheBuilt) return;

        // Para cada GameObject de face, encontra todos os Renderers nos filhos e armazena no array.
        // Se o objeto não for atribuído, cria um array vazio para evitar erros.
        _renderersByFaceGroup[0] = topFaceGroup ? topFaceGroup.GetComponentsInChildren<Renderer>(true) : new Renderer[0];
        _renderersByFaceGroup[1] = bottomFaceGroup ? bottomFaceGroup.GetComponentsInChildren<Renderer>(true) : new Renderer[0];
        _renderersByFaceGroup[2] = northFaceGroup ? northFaceGroup.GetComponentsInChildren<Renderer>(true) : new Renderer[0];
        _renderersByFaceGroup[3] = southFaceGroup ? southFaceGroup.GetComponentsInChildren<Renderer>(true) : new Renderer[0];
        _renderersByFaceGroup[4] = eastFaceGroup ? eastFaceGroup.GetComponentsInChildren<Renderer>(true) : new Renderer[0];
        _renderersByFaceGroup[5] = westFaceGroup ? westFaceGroup.GetComponentsInChildren<Renderer>(true) : new Renderer[0];

        // Inicializa o cache assumindo que todos os grupos começam desabilitados
        for(int i = 0; i < _rendererEnabledCache.Length; i++)
        {
            _rendererEnabledCache[i] = false;
        }

        _isCacheBuilt = true;
    }
    #endregion

    #region BaseVoxel implementation
    protected override void OnInitialize(VoxelType type, bool isSolid)
    {
        // Garante que o cache foi construído antes de qualquer operação.
        if (!_isCacheBuilt) BuildRendererCache();
        
        // A lógica original é mantida: calcula a máscara e a aplica.
        Face defaultMask = GetDefaultMaskForType(type);
        ApplyFaceMask(defaultMask);

        // Toggle collider enabled instead of GameObject.SetActive for performance
        if (primaryCollider)
        {
            if (primaryCollider.enabled != isSolid)
                primaryCollider.enabled = isSolid;
        }

        // Exemplo: cor base via MPB
        Color baseColor = isSolid ? Color.gray : Color.white;
        SetColor(baseColor);
    }
    #endregion

    #region Face mask API
    /// <summary>
    /// Aplica uma máscara de faces. Operação rápida — apenas altera grupos de Renderers cujo estado mudou.
    /// </summary>
    public void ApplyFaceMask(Face mask)
    {
        if (_currentMask == mask) return; // nada a fazer

        for (int i = 0; i < _faceOrder.Length; i++)
        {
            var face = _faceOrder[i];
            var rendererGroup = _renderersByFaceGroup[i];
            if (rendererGroup == null || rendererGroup.Length == 0) continue;

            bool shouldEnable = (mask & face) != 0;
            
            // A otimização principal: só itera nos renderers se o estado do grupo precisar mudar.
            if (_rendererEnabledCache[i] != shouldEnable)
            {
                // MUDANÇA: Itera sobre todos os renderers no grupo para aplicar o novo estado.
                for (int j = 0; j < rendererGroup.Length; j++)
                {
                    rendererGroup[j].enabled = shouldEnable;
                }
                _rendererEnabledCache[i] = shouldEnable;
            }
        }

        _currentMask = mask;
    }

    /// <summary>
    /// Retorna a máscara atual aplicada.
    /// </summary>
    public Face GetCurrentMask() => _currentMask;
    #endregion

    #region Material / color helpers
    /// <summary>
    /// Aplica cor a todas as faces via MaterialPropertyBlock (não instancia novos materiais).
    /// </summary>
    public void SetColor(Color color)
    {
        _mpb.Clear();
        _mpb.SetColor("_Color", color); // assume que o shader usa a propriedade "_Color"

        // MUDANÇA: Itera sobre cada grupo de face e, em seguida, sobre cada renderer dentro do grupo.
        for (int i = 0; i < _renderersByFaceGroup.Length; i++)
        {
            var group = _renderersByFaceGroup[i];
            if (group == null) continue;

            for (int j = 0; j < group.Length; j++)
            {
                var r = group[j];
                if (r == null) continue;
                r.SetPropertyBlock(_mpb);
            }
        }
    }
    #endregion

    #region Utilities
    /// <summary>
    /// Retorna uma máscara padrão com base em VoxelType (sem alterações, lógica mantida).
    /// </summary>
    private Face GetDefaultMaskForType(VoxelType type)
    {
        switch (type)
        {
            case VoxelType.Floor:      return Face.Top;
            case VoxelType.Ceiling:    return Face.Bottom;
            case VoxelType.Wall_North: return Face.North;
            case VoxelType.Wall_South: return Face.South;
            case VoxelType.Wall_East:  return Face.East;
            case VoxelType.Wall_West:  return Face.West;
            case VoxelType.Pillar:     return Face.All;
            case VoxelType.ClosedBox:  return Face.All;
            case VoxelType.Empty:      return Face.None;
            default:                   return Face.Top;
        }
    }

    /// <summary>
    /// Alterna todas as faces rapidamente (sem alterações, funciona com a nova lógica).
    /// </summary>
    [ContextMenu("Toggle All Faces")]
    private void ToggleAllFaces()
    {
        ApplyFaceMask(_currentMask == Face.All ? Face.None : Face.All);
    }
    #endregion
}