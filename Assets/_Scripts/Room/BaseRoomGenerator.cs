// BaseRoomGenerator.cs
// Fonte: VoxelProject (_Scripts/Room)
// PR-02 (opcional/robustez): anexar automaticamente VoxelDoorController aos roots de portas
// e indexar os voxels da “cortina” (blocos filhos do root), mantendo mudanças mínimas.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using RoomInstance = RoomsData.RoomInstance;
using DoorRect     = RoomsData.DoorRect;
using WallSide     = WallSide;

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

    [Header("Pooling")]
    [Tooltip("Tamanho inicial do pool por prefab.")]
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

    [Header("Debug")]
    public bool verbose = false;
    #endregion

    #region Internals
    protected System.Random _rng;
    private Transform _roomsRoot;

    // Mapeia o root da sala para sua RoomInstance (RoomsData)
    private readonly Dictionary<Transform, RoomInstance> _roomsByRoot = new Dictionary<Transform, RoomInstance>();

    // Pool por prefab
    private readonly Dictionary<GameObject, Queue<GameObject>> _pool = new Dictionary<GameObject, Queue<GameObject>>();

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

        // Prewarm minimal (opcional) — evita hitches no primeiro uso
        if (voxelFundamentalPrefab != null && initialPoolPerPrefab > 0)
            PrewarmPool(voxelFundamentalPrefab, Mathf.Min(initialPoolPerPrefab, 256));
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

        // Exemplo de preenchimento básico de chão (simples/visual)
        for (int x = 0; x < size.x; x++)
        {
            for (int y = 0; y < size.y; y++)
            {
                Vector2Int grid = originGrid + new Vector2Int(x, y);
                Vector3 pos = GridToWorld(grid);
                var go = SpawnFromPool(voxelFundamentalPrefab, pos, Quaternion.identity, room.root);
                if (go == null) continue;
                room.voxels.Add(go);
            }
        }

        // Como este gerador base não preenche portas, nada a reindexar aqui.
        // Geradores especializados devem chamar:
        // EnsureDoorController(room.entryDoorRoot, entryRect) / EnsureDoorController(room.exitDoorRoot, exitRect)
        // após preencher as “cortinas” de voxels via BuildDoorwayFill(...).
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

        // remover do índice e destruir root
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

    #region Pooling (protected)
    private void PrewarmPool(GameObject prefab, int count)
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

            // parent para manter hierarquia limpa
            if (_roomsRoot != null) go.transform.SetParent(_roomsRoot, true);
        }
    }

    protected GameObject SpawnFromPool(GameObject prefab, Vector3 worldPos, Quaternion rot, Transform parent)
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

        // Ensure PoolableObject tag existe
        var tag = go.GetComponent<PoolableObject>() ?? go.AddComponent<PoolableObject>();
        tag.OriginalPrefab = prefab;

        return go;
    }

    protected void ReturnToPool(GameObject go)
    {
        if (go == null) return;

        var tag = go.GetComponent<PoolableObject>();
        if (tag == null || tag.OriginalPrefab == null)
        {
            // objeto não saiu do pool — destruir
#if UNITY_EDITOR
            if (!Application.isPlaying) DestroyImmediate(go);
            else Destroy(go);
#else
            Destroy(go);
#endif
            return;
        }

        go.SetActive(false);

        // reparent para manter hierarquia organizada
        if (_roomsRoot != null) go.transform.SetParent(_roomsRoot, true);

        if (!_pool.ContainsKey(tag.OriginalPrefab))
            _pool[tag.OriginalPrefab] = new Queue<GameObject>();

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
            root      = go.transform,
            voxelSize = voxelSize,
            built     = false,
            populated = false
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

        // PR-02 (robustez): garante VoxelDoorController anexado aos roots de porta.
        EnsureDoorController(room.entryDoorRoot); // indexação ocorrerá quando a cortina existir
        EnsureDoorController(room.exitDoorRoot);  // idem

        _roomsByRoot[room.root] = room;
        RegisterDebugRoom(room);
        return room;
    }

    /// <summary>
    /// Coroutine simples de "aparecer" (compatível com StartCoroutine/yield), útil para feedback visual.
    /// </summary>
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
            // ease-out
            k = 1f - Mathf.Pow(1f - k, 2f);
            t.localScale = Vector3.LerpUnclamped(Vector3.zero, targetScale, k);
            yield return null;
        }

        t.localScale = targetScale;
    }
    #endregion

    #region Door Controller Helpers (NEW)
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
                m = t.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
            else
            {
                var argTypes = new Type[args.Length];
                for (int i = 0; i < args.Length; i++)
                    argTypes[i] = args[i]?.GetType() ?? typeof(object);

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
