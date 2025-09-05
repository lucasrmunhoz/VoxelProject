// BaseRoomGenerator.cs
// Fonte: VoxelProject (_Scripts/Room). Ajustes mínimos p/ PR-01:
// - Remove a classe aninhada RoomInstance e usa a RoomInstance definida em RoomsData.cs.
// - Alinha coleções e nomes: voxels/props/root/entryDoorRoot/exitDoorRoot (RoomsData).
// - Mantém API pública e pooling do arquivo base.
// - Sem alterações comportamentais além do necessário para compilar.

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

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
    [Tooltip("Prefab de porta (opcional). Se null, portas serão montadas com voxels).")]
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

    #endregion

    #region Internals

    protected System.Random _rng;
    private Transform _roomsRoot;

    // Mapear o root da sala para sua RoomInstance (RoomsData)
    private readonly Dictionary<Transform, RoomInstance> _roomsByRoot = new Dictionary<Transform, RoomInstance>();

    // Pool por prefab
    private readonly Dictionary<GameObject, Queue<GameObject>> _pool = new Dictionary<GameObject, Queue<GameObject>>();

    [Header("Debug")]
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
        {
            PrewarmPool(voxelFundamentalPrefab, Mathf.Min(initialPoolPerPrefab, 256));
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
            if (_roomsByRoot.ContainsKey(room.root))
                _roomsByRoot.Remove(room.root);

            UnregisterDebugRoom(room);
            Destroy(room.root.gameObject);
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

        Destroy(container.gameObject);
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

            // parent to rooms root to keep hierarchy clean
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

        // Ensure PoolableObject tag exists
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
            Destroy(go);
            return;
        }

        go.SetActive(false);

        // reparent to root to keep hierarchy tidy
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
            root = go.transform,
            voxelSize = voxelSize,
            built = false,
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

        _roomsByRoot[room.root] = room;
        RegisterDebugRoom(room);
        return room;
    }

    // Public coroutine to play a simple appear animation (compatível com callers que usam StartCoroutine or yield)
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
