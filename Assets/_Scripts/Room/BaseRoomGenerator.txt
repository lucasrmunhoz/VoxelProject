// Assets/_Scripts/Room/BaseRoomGenerator.cs
// PR-03 — Unificação de pooling na geração (BaseRoomGenerator → VoxelPool)
// Alterações mínimas e focadas:
// 1) Mantido o fluxo e estrutura originais do arquivo (helpers, reflexão, etc.).
// 2) Adicionado funil de pooling padrão via VoxelPool (PrefabId) com FALLBACK local antigo.
// 3) Adicionado perfil de Prewarm por tipo de sala (usa VoxelPool.Prewarm quando disponível).
// 4) Corrigido CS0246 assegurando os aliases de tipos de RoomsData (RoomInstance, DoorRect, WallSide).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

// === Aliases (mantidos/necessários para evitar CS0246) ===
using RoomInstance = RoomsData.RoomInstance;
using DoorRect     = RoomsData.DoorRect;
using WallSide     = RoomsData.WallSide;

[DisallowMultipleComponent]
public class BaseRoomGenerator : MonoBehaviour
{
    #region Inspector

    [Header("Grid / Room Size")]
    [Tooltip("Tamanho do grid em 'blocos' (limite de geração).")]
    public Vector2Int worldGridSize = new Vector2Int(200, 200);

    [Tooltip("Tamanho mínimo e máximo (em tiles) que uma sala pode ter.")]
    public Vector2Int minRoomSize = new Vector2Int(4, 4);
    public Vector2Int maxRoomSize = new Vector2Int(12, 12);

    [Tooltip("Escala em unidades do mundo de cada voxel.")]
    public float voxelSize = 1.0f;

    [Header("Voxel Prefab")]
    [Tooltip("Prefab para voxel fundamental. Deve conter CompositeVoxel ou VoxelFaceController.")]
    public GameObject voxelFundamentalPrefab;

    [Header("Door & Props")]
    [Tooltip("Prefab de porta (opcional). Se null, portas serão montadas com voxels.")]
    public GameObject doorPrefab;

    [Tooltip("Prefabs de props opcionais.")]
    public GameObject[] propPrefabs;

    [Header("Door Settings")]
    [Tooltip("Largura mínima e máxima de portas (em voxels).")]
    public Vector2Int doorWidthRange = new Vector2Int(1, 3);

    [Tooltip("Altura da porta em voxels.")]
    [Min(1)] public int doorHeight = 3;

    [Header("Pooling (Fallback Local)")]
    [Tooltip("Tamanho inicial do pool local por prefab (usado apenas se VoxelPool não estiver disponível).")]
    public int initialPoolPerPrefab = 64;

    [Header("Public Fields for Reflection Fallback")]
    [Tooltip("Campo público que o GameFlowManager pode setar por reflexão (origin em grid).")]
    public Vector2Int roomOriginGrid = Vector2Int.zero;

    [Tooltip("Campo público que o GameFlowManager pode setar por reflexão (size em tiles).")]
    public Vector2Int roomSize = new Vector2Int(8, 8);

    [Header("Generation")]
    [Tooltip("Tentativas máximas de geração antes de desistir.")]
    public int maxGenerateAttempts = 8;

    [Tooltip("Padding entre salas no grid (tiles).")]
    public int interRoomPadding = 1;

    // === PR-03: Perfil de Prewarm por tipo de sala (via VoxelPool) ===
    [Serializable]
    public struct PrewarmEntry
    {
        public GameObject prefab;
        [Min(0)] public int count;
    }

    [Header("PR-03 — Prewarm por Tipo de Sala (via VoxelPool)")]
    [Tooltip("Lista de pré-aquecimento específico deste gerador. Se VoxelPool estiver presente, usa VoxelPool.Prewarm().")]
    public List<PrewarmEntry> prewarmProfile = new List<PrewarmEntry>();

    [Header("Debug")]
    public bool verbose = false;

    #endregion

    #region Internals

    protected System.Random _rng;
    private Transform _roomsRoot;

    // Mapeia o root da sala para sua RoomInstance (RoomsData)
    private readonly Dictionary<Transform, RoomInstance> _roomsByRoot = new Dictionary<Transform, RoomInstance>();

    // === Fallback: pool local (apenas se VoxelPool não estiver disponível) ===
    private readonly Dictionary<GameObject, Queue<GameObject>> _pool = new Dictionary<GameObject, Queue<GameObject>>();

    // === PR-03: cache de PrefabId (para chamadas rápidas ao VoxelPool) ===
    private readonly Dictionary<GameObject, int> _prefabIds = new Dictionary<GameObject, int>(16);

    // Auxílio para inspeção no Editor
    [Tooltip("Lista de RoomInstance atualmente registradas — útil para debug no Inspector.")]
    public List<RoomInstance> debugRoomInstances = new List<RoomInstance>();

    #endregion

    #region Unity lifecycle

    private void Awake()
    {
        _rng = new System.Random(Environment.TickCount ^ GetInstanceID());

        // Garantir root para organização
        var existing = transform.Find("GeneratedRooms");
        if (existing != null) _roomsRoot = existing;
        else
        {
            var go = new GameObject("GeneratedRooms");
            go.transform.SetParent(this.transform, false);
            _roomsRoot = go.transform;
        }

        // === PR-03: Prewarm via VoxelPool (por perfil) ===
        var pool = VoxelPool.Instance;
        if (pool != null)
        {
            // Prewarm explícito por perfil configurado
            if (prewarmProfile != null)
            {
                for (int i = 0; i < prewarmProfile.Count; i++)
                {
                    var e = prewarmProfile[i];
                    if (!e.prefab || e.count <= 0) continue;

                    int id = GetOrRegisterPrefabId(e.prefab);
                    if (id > 0) pool.Prewarm(id, e.count);
                }
            }

            // Prewarm mínimo para voxel fundamental (compatibilidade com comportamento antigo)
            if (voxelFundamentalPrefab && initialPoolPerPrefab > 0)
            {
                int id = GetOrRegisterPrefabId(voxelFundamentalPrefab);
                if (id > 0) pool.Prewarm(id, Mathf.Min(initialPoolPerPrefab, 256));
            }
        }
        else
        {
            // Fallback local anterior (apenas se não houver VoxelPool)
            if (voxelFundamentalPrefab != null && initialPoolPerPrefab > 0)
                PrewarmPool_Local(voxelFundamentalPrefab, Mathf.Min(initialPoolPerPrefab, 256));
        }
    }

    #endregion

    #region Public API - Generate / Clear

    // Overloads compatíveis com GameFlowManager e outros:
    public virtual void GenerateRoom() => GenerateRoom(roomOriginGrid, roomSize);
    public virtual void GenerateRoom(Vector2Int size) => GenerateRoom(roomOriginGrid, size);

    public virtual void GenerateRoom(Vector2Int originGrid, Vector2Int size)
    {
        // Implementação padrão simples: cria container e voxels (fallback).
        // Derivados (SimpleRoomGenerator, BedroomGenerator) normalmente fazem override.
        var room = CreateRoomContainer(originGrid, size, -1);

        // Exemplo visual de preenchimento básico de chão (1 voxel por tile)
        for (int x = 0; x < size.x; x++)
        {
            for (int y = 0; y < size.y; y++)
            {
                Vector2Int grid = originGrid + new Vector2Int(x, y);
                Vector3 pos = GridToWorld(grid);

                var go = SpawnFromPool(voxelFundamentalPrefab, pos, Quaternion.identity, room.root);
                if (go == null) continue;

                if (room.voxels == null) room.voxels = new List<GameObject>(size.x * size.y);
                room.voxels.Add(go);
            }
        }

        // Geradores especializados devem preencher portas e então:
        // EnsureDoorController(room.entryDoorRoot, entryRect);
        // EnsureDoorController(room.exitDoorRoot,  exitRect);
        // ReindexDoorCurtain(doorRoot) após BuildDoorwayFill(...).
    }

    /// <summary>Clear and return voxels/props for the given room.</summary>
    public virtual void ClearRoom(RoomInstance room)
    {
        if (room == null) return;

        // devolver voxels
        if (room.voxels != null)
        {
            for (int i = room.voxels.Count - 1; i >= 0; i--)
            {
                var go = room.voxels[i];
                if (go == null) continue;
                ReturnToPool(go);
            }
            room.voxels.Clear();
        }

        // devolver props
        if (room.props != null)
        {
            for (int i = room.props.Count - 1; i >= 0; i--)
            {
                var p = room.props[i];
                if (p == null) continue;
                ReturnToPool(p);
            }
            room.props.Clear();
        }

        // remover do índice e destruir root (apenas o container vazio)
        if (room.root != null)
        {
            if (_roomsByRoot.ContainsKey(room.root)) _roomsByRoot.Remove(room.root);
            UnregisterDebugRoom(room);

#if UNITY_EDITOR
            if (!Application.isPlaying) DestroyImmediate(room.root.gameObject);
            else Destroy(room.root.gameObject);
#else
            Destroy(room.root.gameObject);
#endif
            room.root = null;
        }
    }

    public virtual void ClearRoom(Transform container)
    {
        if (container == null) return;

        if (_roomsByRoot.TryGetValue(container, out var room))
        {
            ClearRoom(room);
            return;
        }

        var match = debugRoomInstances.Find(r => r.root == container);
        if (match != null)
        {
            ClearRoom(match);
            return;
        }

#if UNITY_EDITOR
        if (!Application.isPlaying) DestroyImmediate(container.gameObject);
        else Destroy(container.gameObject);
#else
        Destroy(container.gameObject);
#endif
    }

    public virtual void ClearRoom(GameObject container) => ClearRoom(container ? container.transform : null);

    #endregion

    #region Pooling — PR-03 (VoxelPool como padrão + FALLBACK local)

    /// <summary>
    /// PR-03: tenta usar VoxelPool; se não houver, usa o pool local anterior.
    /// </summary>
    protected GameObject SpawnFromPool(GameObject prefab, Vector3 worldPos, Quaternion rot, Transform parent)
    {
        if (prefab == null) return null;

        var pool = VoxelPool.Instance;
        if (pool != null)
        {
            int id = GetOrRegisterPrefabId(prefab);
            if (id > 0)
            {
                var t = pool.Get(id, worldPos, rot, parent);
                return t ? t.gameObject : null;
            }
        }

        // === Fallback local (comportamento antigo) ===
        return SpawnFromPool_Local(prefab, worldPos, rot, parent);
    }

    /// <summary>
    /// PR-03: tenta devolver ao VoxelPool; se não houver, volta ao pool local.
    /// </summary>
    protected void ReturnToPool(GameObject go)
    {
        if (!go) return;

        var pool = VoxelPool.Instance;
        if (pool != null)
        {
            // Se o objeto veio do VoxelPool, terá VoxelCache com PrefabId configurado.
            if (go.TryGetComponent(out VoxelCache cache) && cache.PrefabId > 0)
            {
                pool.Release(go.transform);
                return;
            }

            // Caso raro: instância sem VoxelCache mas sabemos o prefab original (tag do fallback)
            if (go.TryGetComponent(out PoolableObject tag) && tag.OriginalPrefab)
            {
                int id = GetOrRegisterPrefabId(tag.OriginalPrefab);
                if (id > 0)
                {
                    go.transform.SetParent(null, true); // reparent breve para limpeza visual
                    pool.Release(go.transform);
                    return;
                }
            }
        }

        // === Fallback local (comportamento antigo) ===
        ReturnToPool_Local(go);
    }

    /// <summary>Obtém (ou registra) o PrefabId no VoxelPool para o prefab informado.</summary>
    protected int GetOrRegisterPrefabId(GameObject prefab)
    {
        if (!prefab) return -1;

        if (_prefabIds.TryGetValue(prefab, out int cached) && cached > 0)
            return cached;

        var pool = VoxelPool.Instance;
        if (pool == null) return -1;

        // RegisterPrefab é idempotente: devolve o ID existente se já houver registro.
        int id = pool.RegisterPrefab(prefab, maxPoolSize: 512, prewarm: 0);
        if (id > 0) _prefabIds[prefab] = id;
        return id;
    }

    // ---------- Implementação anterior (FALLBACK LOCAL) ----------

    private void PrewarmPool_Local(GameObject prefab, int count)
    {
        if (prefab == null || count <= 0) return;
        if (!_pool.ContainsKey(prefab)) _pool[prefab] = new Queue<GameObject>();

        var q = _pool[prefab];
        for (int i = 0; i < count; i++)
        {
            var go = Instantiate(prefab);
            go.SetActive(false);

            var tag = go.GetComponent<PoolableObject>() ?? go.AddComponent<PoolableObject>();
            tag.OriginalPrefab = prefab;

            q.Enqueue(go);

            // parent para manter a hierarquia limpa
            if (_roomsRoot != null) go.transform.SetParent(_roomsRoot, true);
        }
    }

    private GameObject SpawnFromPool_Local(GameObject prefab, Vector3 worldPos, Quaternion rot, Transform parent)
    {
        if (prefab == null) return null;
        GameObject go = null;

        if (_pool.TryGetValue(prefab, out var q) && q.Count > 0)
        {
            go = q.Dequeue();
            if (go == null) go = Instantiate(prefab);
        }
        else
        {
            go = Instantiate(prefab);
        }

        go.transform.SetParent(parent, false);
        go.transform.position = worldPos;
        go.transform.rotation = rot;
        go.SetActive(true);

        var tag = go.GetComponent<PoolableObject>() ?? go.AddComponent<PoolableObject>();
        tag.OriginalPrefab = prefab;

        return go;
    }

    private void ReturnToPool_Local(GameObject go)
    {
        if (go == null) return;

        var tag = go.GetComponent<PoolableObject>();
        if (tag == null || tag.OriginalPrefab == null)
        {
            // objeto não saiu do pool local — destruir
#if UNITY_EDITOR
            if (!Application.isPlaying) DestroyImmediate(go);
            else Destroy(go);
#else
            Destroy(go);
#endif
            return;
        }

        go.SetActive(false);

        // reparent para root para manter a hierarquia organizada
        if (_roomsRoot != null) go.transform.SetParent(_roomsRoot, true);

        if (!_pool.ContainsKey(tag.OriginalPrefab)) _pool[tag.OriginalPrefab] = new Queue<GameObject>();
        _pool[tag.OriginalPrefab].Enqueue(go);
    }

    #endregion

    #region Utilities (protected / public)

    // Protected para uso por classes derivadas
    protected Vector3 GridToWorld(Vector2Int grid)
    {
        return new Vector3(grid.x * voxelSize, 0f, grid.y * voxelSize);
    }

    /// <summary>
    /// Cria e registra um container de sala, retornando a RoomInstance (RoomsData).
    /// Preenche fields que os derivados/gestores esperam (root/door roots/voxelSize).
    /// </summary>
    protected RoomInstance CreateRoomContainer(Vector2Int originGrid, Vector2Int size, int index)
    {
        var go = new GameObject($"Room_{originGrid.x}_{originGrid.y}");
        if (_roomsRoot != null) go.transform.SetParent(_roomsRoot, false);

        var room = new RoomInstance
        {
            // plan: ficará a cargo do orquestrador preencher, se necessário
            root = go.transform,
            voxelSize = voxelSize,
            built = false,
            populated = false,
            builder = this // útil p/ streaming e PR-02/PR-03
        };

        // Raízes das "portas" (preenchidas com voxels conforme gerador)
        var entryGO = new GameObject("EntryDoor");
        entryGO.transform.SetParent(room.root, false);
        entryGO.transform.localPosition = Vector3.zero;
        room.entryDoorRoot = entryGO.transform;

        var exitGO = new GameObject("ExitDoor");
        exitGO.transform.SetParent(room.root, false);
        exitGO.transform.localPosition = Vector3.zero;
        room.exitDoorRoot = exitGO.transform;

        // PR-02 (robustez): garante VoxelDoorController nos roots (método já compatível via reflexão)
        EnsureDoorController(room.entryDoorRoot);
        EnsureDoorController(room.exitDoorRoot);

        _roomsByRoot[room.root] = room;
        RegisterDebugRoom(room);

        return room;
    }

    // Public coroutine to play a simple appear animation (compatível com callers que usam StartCoroutine/yield)
    // Exemplo de uso: StartCoroutine(base.PlaySpawnAppear(someTransform, 0.12f));
    public IEnumerator PlaySpawnAppear(Transform t, float duration)
    {
        if (t == null) yield break;
        duration = Mathf.Max(0.0001f, duration);

        Vector3 targetScale = t.localScale;
        t.localScale = Vector3.zero;

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float k = Mathf.Clamp01(elapsed / duration);
            // suavização (ease-out)
            k = 1f - Mathf.Pow(1f - k, 2f);
            t.localScale = Vector3.LerpUnclamped(Vector3.zero, targetScale, k);
            yield return null;
        }
        t.localScale = targetScale;
    }

    #endregion

    #region Door Controller Helpers (compatível por reflexão)

    /// <summary>
    /// Garante que exista um VoxelDoorController no root informado e reindexa a “cortina”.
    /// Use esta versão quando não houver (ou não importar) o lado da parede ainda.
    /// </summary>
    protected void EnsureDoorController(Transform doorRoot)
    {
        if (!doorRoot) return;

        var ctrl = doorRoot.GetComponent<VoxelDoorController>();
        if (!ctrl) ctrl = doorRoot.gameObject.AddComponent<VoxelDoorController>();

        // Tenta usar InitializeFromChildren() se existir (indexação interna da cortina)
        if (!TryInvokeNoThrow(ctrl, "InitializeFromChildren"))
        {
            // Fallback: tenta Clear(); depois AddVoxel(child) por reflexão
            TryInvokeNoThrow(ctrl, "Clear");

            int childCount = doorRoot.childCount;
            for (int i = 0; i < childCount; i++)
            {
                var child = doorRoot.GetChild(i);
                if (!child) continue;

                // Somente objetos ativos contam como parte da “cortina”
                if (!child.gameObject.activeSelf) continue;

                if (!TryInvokeNoThrow(ctrl, "AddVoxel", new object[] { child }))
                {
                    // Se não existe AddVoxel(Transform), não há mais nada seguro a fazer.
                    // O controller ainda estará anexado para responder a sinais Open/Close.
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Versão com DoorRect: se o VoxelDoorController expuser Setup(WallSide, float),
    /// passamos o lado e o voxelSize para ordenar a onda de animação.
    /// </summary>
    protected void EnsureDoorController(Transform doorRoot, in DoorRect rect)
    {
        EnsureDoorController(doorRoot);
        var ctrl = doorRoot ? doorRoot.GetComponent<VoxelDoorController>() : null;
        if (!ctrl) return;

        // Tenta Setup(WallSide, float) por reflexão (mantém compatibilidade com versões antigas)
        TryInvokeNoThrow(ctrl, "Setup", new object[] { rect.side, voxelSize });

        // Reindexa após eventual Setup
        ReindexDoorCurtain(doorRoot);
    }

    /// <summary>
    /// Reindexa a cortina após mudanças nos filhos (ex.: depois de BuildDoorwayFill).
    /// </summary>
    public void ReindexDoorCurtain(Transform doorRoot)
    {
        if (!doorRoot) return;

        var ctrl = doorRoot.GetComponent<VoxelDoorController>();
        if (!ctrl) return;

        if (!TryInvokeNoThrow(ctrl, "InitializeFromChildren"))
        {
            // Fallback igual ao Ensure...
            TryInvokeNoThrow(ctrl, "Clear");
            int childCount = doorRoot.childCount;
            for (int i = 0; i < childCount; i++)
            {
                var child = doorRoot.GetChild(i);
                if (!child || !child.gameObject.activeSelf) continue;
                TryInvokeNoThrow(ctrl, "AddVoxel", new object[] { child });
            }
        }
    }

    /// <summary>
    /// Invoca método público por nome (sem exceção). Retorna true se método foi encontrado e invocado.
    /// </summary>
    private static bool TryInvokeNoThrow(object target, string methodName, object[] args = null)
    {
        if (target == null || string.IsNullOrEmpty(methodName)) return false;

        try
        {
            var t = target.GetType();
            MethodInfo m;

            if (args == null || args.Length == 0)
            {
                m = t.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
            }
            else
            {
                var argTypes = new Type[args.Length];
                for (int i = 0; i < args.Length; i++) argTypes[i] = args[i]?.GetType() ?? typeof(object);

                // Busca por assinatura exata primeiro, depois por nome (qualquer assinatura pública)
                m = t.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public, null, argTypes, null)
                    ?? t.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
            }

            if (m == null) return false;
            m.Invoke(target, args == null ? Array.Empty<object>() : args);
            return true;
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region Debug helpers

    private void RegisterDebugRoom(RoomInstance room)
    {
        if (room == null) return;
        if (!debugRoomInstances.Contains(room)) debugRoomInstances.Add(room);
    }

    private void UnregisterDebugRoom(RoomInstance room)
    {
        if (room == null) return;
        if (debugRoomInstances.Contains(room)) debugRoomInstances.Remove(room);
    }

    #endregion
}

// Nota: RoomInstance/DoorRect/WallSide são definidos em Assets/_Scripts/Room/RoomsData.cs (namespace RoomsData).
// Nota: VoxelPool/VoxelCache/PoolableObject estão em VoxelSystem.
// Este arquivo foi ajustado para PR-03 mantendo a estrutura original e apenas introduzindo o funil de pooling global.
