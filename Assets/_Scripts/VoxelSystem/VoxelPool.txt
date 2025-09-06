// VoxelPool.cs
using System;
using System.Collections.Generic;
using UnityEngine;

#region Contracts

/// <summary>
/// Contrato para obtenção/devolução de instâncias via pool.
/// Integra com VoxelCache (usa PrefabId para retornar ao "balde" correto).
/// </summary>
public interface IPooledFactory
{
    Transform Get(int prefabId, Vector3 position, Quaternion rotation, Transform parent = null);
    Transform Get(int prefabId, Transform parent = null);

    void Release(Transform instance);
    void ReleaseAllChildren(Transform parent, bool includeInactive = true);

    bool ContainsPrefabId(int prefabId);
    int RegisterPrefab(GameObject prefab, int maxPoolSize = 256, int prewarm = 0);
    GameObject GetPrefabById(int prefabId);
}

/// <summary>
/// (Opcional) Hooks de ciclo de vida para objetos que desejam ser notificados
/// durante o fluxo de pool (sem alocações).
/// </summary>
public interface IPoolable
{
    // Objeto ainda INATIVO, antes de SetActive(true)
    void OnBeforeSpawn();
    // Objeto ATIVO, após SetActive(true)
    void OnAfterSpawn();
    // Objeto ATIVO, prestes a ser desativado
    void OnBeforeDespawn();
    // Objeto INATIVO, após SetActive(false)
    void OnAfterDespawn();
}

#endregion

/// <summary>
/// Pool de objetos por ID de prefab, com bins por-prefab, preaquecer (prewarm),
/// limite de capacidade, trimming e utilitários de limpeza em massa.
/// Responsabilidades do pool:
/// - Reparenting, ativação/desativação.
/// - Garantir VoxelCache + PrefabId.
/// - Chamar hooks de IPoolable/PoolableObject (se presentes).
/// </summary>
[AddComponentMenu("Voxel Nightmare/Voxel Pool")]
public sealed class VoxelPool : MonoBehaviour, IPooledFactory
{
    // ---------- Singleton seguro mesmo sem domain reload ----------
    public static VoxelPool Instance { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        Instance = null;
    }

    // ---------- Configuração dos prefabs ----------
    [Serializable]
    public sealed class PrefabEntry
    {
        [Tooltip("Identificador único deste prefab no pool.")]
        public int id = 0;

        [Tooltip("Prefab a ser poolado. Deve conter (ou receber) VoxelCache.")]
        public GameObject prefab;

        [Tooltip("Quantidade para pré-aquecer (instanciar no início).")]
        [Min(0)] public int prewarm = 0;

        [Tooltip("Limite máximo de instâncias guardadas em pool (por prefab).")]
        [Min(1)] public int maxPoolSize = 512;

        [HideInInspector] public Transform binRoot; // pasta no hierarchy

        [NonSerialized] public Stack<Transform> stack = new(); // LIFO → melhor cache locality

        public override string ToString() => $"[ID:{id}] {prefab?.name ?? ""} (prewarm:{prewarm} max:{maxPoolSize})";
    }

    [Header("Tabela de Prefabs")]
    [SerializeField] private List<PrefabEntry> prefabs = new();

    [Header("Opções")]
    [Tooltip("Manter este objeto entre cenas.")]
    [SerializeField] private bool dontDestroyOnLoad = true;

    [Tooltip("Pai dos 'bins' (pastas) por prefab. Se vazio, cria automaticamente.")]
    [SerializeField] private Transform binsRoot;

    [Tooltip("Clampa a escala local no 'Get' para a escala do prefab, garantindo consistência.")]
    [SerializeField] private bool forcePrefabLocalScaleOnGet = true;

    [Tooltip("Se verdadeiro, o pool chamará hooks de IPoolable/PoolableObject em spawn/despawn.")]
    [SerializeField] private bool invokePoolableHooks = true;

    [Tooltip("Se verdadeiro, procura hooks IPoolable em toda a hierarquia (inativa). Custa mais.")]
    [SerializeField] private bool searchChildrenForPoolable = false;

    // ---------- Índices para lookups rápidos ----------
    private readonly Dictionary<int, PrefabEntry> _idToEntry = new();
    private readonly Dictionary<GameObject, int> _prefabToId = new();

    // Buffer estático para evitar alocações ao coletar IPoolable
    private static readonly List<IPoolable> _poolableBuffer = new(8);

    // ---------- Lifecycle ----------
    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[VoxelPool] Outra instância encontrada. Destruindo a nova.");
            Destroy(gameObject);
            return;
        }

        Instance = this;
        if (dontDestroyOnLoad) DontDestroyOnLoad(gameObject);

        BuildLookupsAndBins();
        PrewarmAll();
    }

    private void BuildLookupsAndBins()
    {
        _idToEntry.Clear();
        _prefabToId.Clear();

        if (!binsRoot)
        {
            var binsGo = new GameObject("VoxelPool_Bins");
            binsGo.transform.SetParent(transform, false);
            binsRoot = binsGo.transform;
            binsGo.hideFlags = HideFlags.DontSave;
        }

        var usedIds = new HashSet<int>();
        foreach (var e in prefabs)
        {
            if (e == null || e.prefab == null)
            {
                Debug.LogError("[VoxelPool] Entrada nula ou prefab ausente.");
                continue;
            }

            if (!usedIds.Add(e.id))
            {
                Debug.LogError($"[VoxelPool] ID duplicado: {e.id} ({e.prefab.name}). Ajuste para IDs únicos.");
                continue;
            }

            if (e.binRoot == null)
            {
                var binGo = new GameObject($"bin_{e.id}_{e.prefab.name}");
                binGo.transform.SetParent(binsRoot, false);
                e.binRoot = binGo.transform;
                binGo.SetActive(false); // só pasta organizacional
                binGo.hideFlags = HideFlags.DontSave;
            }

            e.stack ??= new Stack<Transform>();
            e.stack.Clear();

            _idToEntry[e.id] = e;
            _prefabToId[e.prefab] = e.id;
        }
    }

    private void PrewarmAll()
    {
        foreach (var e in prefabs)
        {
            if (e == null || e.prefab == null) continue;
            if (e.prewarm <= 0) continue;

            for (int i = 0; i < e.prewarm; i++)
            {
                var t = SpawnNewInactive(e); // cria desativado
                InternalDespawn(t, e);       // envia ao bin, respeitando limite
            }
        }
    }

    /// <summary>
    /// PR-03: Prewarm sob demanda por PrefabId (para perfis por tipo de sala).
    /// Retorna a quantidade efetivamente adicionada ao bin (podendo ser menor se atingiu o maxPoolSize).
    /// </summary>
    public int Prewarm(int prefabId, int count)
    {
        if (count <= 0) return 0;
        if (!_idToEntry.TryGetValue(prefabId, out var e) || e.prefab == null) return 0;

        int added = 0;
        for (int i = 0; i < count; i++)
        {
            var t = SpawnNewInactive(e);
            InternalDespawn(t, e);
            added++;
            // se estourou e maxPoolSize, InternalDespawn fará trimming
        }
        return added;
    }

    // ---------- API pública (IPooledFactory) ----------
    public bool ContainsPrefabId(int prefabId) => _idToEntry.ContainsKey(prefabId);

    public GameObject GetPrefabById(int prefabId) =>
        _idToEntry.TryGetValue(prefabId, out var e) ? e.prefab : null;

    /// <summary>
    /// Registra dinamicamente um prefab no pool (retorna o id). Se já existir, retorna o existente.
    /// </summary>
    public int RegisterPrefab(GameObject prefab, int maxPoolSize = 256, int prewarm = 0)
    {
        if (prefab == null)
        {
            Debug.LogError("[VoxelPool] RegisterPrefab: prefab nulo.");
            return -1;
        }

        if (_prefabToId.TryGetValue(prefab, out var existingId))
            return existingId;

        // Gera ID não utilizado (simples incremental)
        int newId = 1;
        while (_idToEntry.ContainsKey(newId)) newId++;

        var entry = new PrefabEntry
        {
            id = newId,
            prefab = prefab,
            maxPoolSize = Mathf.Max(1, maxPoolSize),
            prewarm = Mathf.Max(0, prewarm)
        };

        var binGo = new GameObject($"bin_{entry.id}_{prefab.name}");
        binGo.transform.SetParent(binsRoot, false);
        binGo.SetActive(false);
        binGo.hideFlags = HideFlags.DontSave;
        entry.binRoot = binGo.transform;
        entry.stack = new Stack<Transform>();

        _idToEntry.Add(entry.id, entry);
        _prefabToId.Add(entry.prefab, entry.id);
        prefabs.Add(entry);

        if (entry.prewarm > 0)
        {
            for (int i = 0; i < entry.prewarm; i++)
            {
                var t = SpawnNewInactive(entry);
                InternalDespawn(t, entry);
            }
        }

        return entry.id;
    }

    public Transform Get(int prefabId, Transform parent = null) =>
        Get(prefabId, Vector3.zero, Quaternion.identity, parent);

    public Transform Get(int prefabId, Vector3 position, Quaternion rotation, Transform parent = null)
    {
        if (!_idToEntry.TryGetValue(prefabId, out var e) || e.prefab == null)
        {
            Debug.LogError($"[VoxelPool] Get: PrefabId {prefabId} não registrado.");
            return null;
        }

        Transform t = (e.stack.Count > 0) ? e.stack.Pop() : SpawnNewInactive(e);

        // Parent/transform antes dos hooks
        if (parent != null) t.SetParent(parent, false);
        else t.SetParent(null, false);

        t.localPosition = position;   // usa local se parent != null
        t.localRotation = rotation;

        if (forcePrefabLocalScaleOnGet)
            t.localScale = e.prefab.transform.localScale;

        // Garante VoxelCache + PrefabId + reset leve de RB (inativo ainda)
        if (!t.TryGetComponent(out VoxelCache cache))
            cache = t.gameObject.AddComponent<VoxelCache>();
        cache.EnsureCached();
        if (cache.PrefabId != e.id) cache.SetPrefabId(e.id);
        if (cache.Rb)
        {
            cache.Rb.velocity = Vector3.zero;
            cache.Rb.angularVelocity = Vector3.zero;
        }

        // ---- Hooks: OnBeforeSpawn (objeto ainda INATIVO) ----
        if (invokePoolableHooks)
        {
            InvokePoolable(t, static p => p.OnBeforeSpawn());
        }

        // Ativa no final
        var go = t.gameObject;
        go.SetActive(true);

        // ---- Hooks: OnAfterSpawn (objeto ATIVO) ----
        if (invokePoolableHooks)
        {
            InvokePoolable(t, static p => p.OnAfterSpawn());
        }

        return t;
    }

    public void Release(Transform instance)
    {
        if (instance == null) return;

        // ---- Hooks: OnBeforeDespawn (objeto ATIVO) ----
        if (invokePoolableHooks)
        {
            InvokePoolable(instance, static p => p.OnBeforeDespawn());
        }

        // Identidade do pool
        int id = -1;
        PrefabEntry e = null;

        if (instance.TryGetComponent(out VoxelCache cache))
        {
            id = cache.PrefabId;
            _idToEntry.TryGetValue(id, out e);
        }

        // Se desconhecido, despacha para raiz
        if (e == null || e.prefab == null)
        {
            instance.gameObject.SetActive(false);
            instance.SetParent(binsRoot, false);

            // ---- Hooks: OnAfterDespawn (objeto INATIVO) ----
            if (invokePoolableHooks)
            {
                InvokePoolable(instance, static p => p.OnAfterDespawn());
            }
            return;
        }

        InternalDespawn(instance, e);

        // ---- Hooks: OnAfterDespawn (objeto INATIVO) ----
        if (invokePoolableHooks)
        {
            InvokePoolable(instance, static p => p.OnAfterDespawn());
        }
    }

    public void ReleaseAllChildren(Transform parent, bool includeInactive = true)
    {
        if (parent == null) return;

        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            var child = parent.GetChild(i);
            if (!includeInactive && !child.gameObject.activeSelf) continue;
            Release(child);
        }
    }

    // ---------- Helpers internos ----------
    private Transform SpawnNewInactive(PrefabEntry e)
    {
        var go = Instantiate(e.prefab);
        go.SetActive(false);

        var t = go.transform;
        t.SetParent(e.binRoot, false);

        if (!go.TryGetComponent(out VoxelCache cache))
            cache = go.AddComponent<VoxelCache>();
        cache.SetPrefabId(e.id);
        cache.EnsureCached();

        return t;
    }

    private void InternalDespawn(Transform t, PrefabEntry e)
    {
        if (t == null) return;

        var go = t.gameObject;
        if (go.activeSelf) go.SetActive(false);

        t.SetParent(e.binRoot, false);
        e.stack.Push(t);

        // trimming se exceder maxPoolSize
        while (e.stack.Count > e.maxPoolSize)
        {
            var extra = e.stack.Pop();
            if (extra) Destroy(extra.gameObject);
        }
    }

    private void InvokePoolable(Transform root, Action<IPoolable> call)
    {
        _poolableBuffer.Clear();

        if (searchChildrenForPoolable)
            root.GetComponentsInChildren(true, _poolableBuffer);
        else
            root.GetComponents(_poolableBuffer);

        for (int i = 0; i < _poolableBuffer.Count; i++)
        {
            try
            {
                call(_poolableBuffer[i]);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        _poolableBuffer.Clear();
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.3f, 0.9f, 1f, 0.6f);
        var p = transform.position;

        float y = 0f;
        foreach (var e in prefabs)
        {
            if (e == null || e.prefab == null) continue;

            var label = $"ID:{e.id} {e.prefab.name} pool:{(e.stack != null ? e.stack.Count : 0)}/{e.maxPoolSize}";
            UnityEditor.Handles.Label(p + Vector3.up * (y += 0.25f), label);
        }
    }
#endif
}
