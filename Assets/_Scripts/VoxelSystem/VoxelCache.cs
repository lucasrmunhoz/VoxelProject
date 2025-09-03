// VoxelCache.cs
using UnityEngine;

/// <summary>
/// Cache leve de componentes usados com frequência em voxels e props.
/// Também armazena o PrefabId (atribuído pela factory/pool no momento do Get).
/// </summary>
[DisallowMultipleComponent]
[AddComponentMenu("Voxel Nightmare/Voxel Cache")]
public sealed class VoxelCache : MonoBehaviour
{
    [Header("Identidade do Pool")]
    [Tooltip("ID do prefab de origem no pool/factory. Definido ao instanciar (Get).")]
    public int PrefabId = -1;

    [Header("Caches de Componentes (opcional preencher manualmente)")]
    public Transform Tr;
    public Renderer Rend;
    public Collider Col;
    public Rigidbody Rb;
    public MeshFilter MeshF;
    public MeshCollider MeshCol;

    /// <summary>Indica se já fizemos o cache pelo menos uma vez.</summary>
    public bool IsCached { get; private set; }

    void Awake()
    {
        EnsureCached();
    }

    void Reset()
    {
        // Facilita o preenchimento no editor sem alocar em runtime
        TryAutoFill();
    }

    void OnValidate()
    {
        // Mantém referências consistentes quando editado no Inspector
        if (Tr == null) Tr = transform;
    }

    /// <summary>
    /// Garante que referências comuns estejam cacheadas.
    /// Evita chamadas GetComponent repetidas durante o jogo.
    /// </summary>
    public void EnsureCached()
    {
        if (IsCached) return;
        TryAutoFill();
        IsCached = true;
    }

    /// <summary>
    /// Tenta preencher campos nulos usando GetComponent (uma vez só).
    /// </summary>
    private void TryAutoFill()
    {
        if (Tr == null) Tr = transform;
        if (Rend == null) TryGetComponent(out Rend);
        if (Col == null) TryGetComponent(out Col);
        if (Rb == null) TryGetComponent(out Rb);
        if (MeshF == null) TryGetComponent(out MeshF);
        if (MeshCol == null) TryGetComponent(out MeshCol);
    }

    /// <summary>
    /// Define (ou redefine) o PrefabId de forma segura ao pegar do pool.
    /// </summary>
    public void SetPrefabId(int id)
    {
        PrefabId = id;
    }
}
