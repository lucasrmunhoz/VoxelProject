// BaseRoomGenerator.cs
// Versão com otimizações: MaterialPropertyBlock para cores, roomsRoot exposto, time-slicing por tempo,
// e integração com VoxelDoorController para portas construídas a partir de voxels.
// ADICIONADO: Lógica para "adotar" salas pré-fabricadas (hubs) e gerar portas nelas em tempo de execução.
// CORRIGIDO: Implementada "inicialização preguiçosa" (lazy initialization) para o gerador de números aleatórios (Rng)
// para eliminar a NullReferenceException causada por condições de corrida na ordem de execução de scripts.
// CORRIGIDO (28/08/2025): Corrigidos nomes de variáveis de áudio no método InitializePreMadeRoom.
// ALTERADO: O método InitializePreMadeRoom agora lê as dimensões da porta do componente DoorAnchorInfo no ExitAnchor.
// =======================================================================
// INÍCIO DAS ALTERAÇÕES APLICADAS (Máscara de Faces e Altura Y)
// =======================================================================
// ALTERADO (HOJE): O método InitializeVoxelIfPossible agora calcula e aplica uma máscara de faces para criar salas ocas.
// ALTERADO (HOJE): O método GridToWorld agora preserva a altura... Y do roomsRoot ou do hub para posicionamento correto das salas.
// =======================================================================
// FIM DAS ALTERAÇÕES APLICADAS
// =======================================================================
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Random = System.Random;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Classe base para geradores de sala: constrói chão, paredes, teto e portas,
/// e expõe utilidades para as classes filhas popularem o interior.
/// </summary>
public class BaseRoomGenerator : MonoBehaviour
{
    #region Inspector
    [Header("Grid / Room Size")]
    public Vector2Int worldGridSize = new Vector2Int(200, 200);
    public Vector2Int minRoomSize = new Vector2Int(4, 4);
    public Vector2Int maxRoomSize = new Vector2Int(12, 12);
    public float voxelSize = 1.0f;

    [Header("Prefabs")]
    public GameObject voxelFundamentalPrefab;
    public GameObject doorPrefab;
    public GameObject switchPrefab;
    public GameObject[] propPrefabs;

    [Header("Door Audio")]
    public AudioClip doorOpenSound;
    public AudioClip doorCloseSound;
    public AudioClip doorLockedSound;

    [Header("Door Settings")]
    public Vector2Int doorWidthRange = new Vector2Int(2, 5);
    [Min(1)] public int doorHeight = 3;
    public int desiredDoorCount = 2; // Procedural unique sides

    [Header("Switch (player interactable)")]
    public float switchWorldHeight = 1.1f;
    public int switchMinDistanceFromEntryTiles = 3;

    [Header("Generation / Performance")]
    public bool generateGradually = true;
    [Min(1)] public int maxVoxelsPerFrame = 256;
    public int initialPoolPerPrefab = 64;

    [Header("Runtime Parents")]
    [Tooltip("Parent usado para todas as salas geradas. Se nulo, será criado um 'GeneratedRooms' filho deste objeto.")]
    public Transform roomsRoot; // EXPOSTO: pode ser setado externamente pelo GameFlowManager

    [Header("Time-slicing")]
    [Tooltip("Budget em milissegundos por frame para geração gradual. Use ~8-12 ms para manter a taxa de frames.")]
    [Range(0.5f, 20f)] public float frameBudgetMs = 8f;

    [Header("Hub / Pre-made rooms")]
    [Tooltip("Referência ao container da sala inicial (hub) criada no editor.")]
    public Transform initialHubRoom;
    #endregion

    #region Types
    [Serializable]
    public class RoomInstance
    {
        public Vector2Int originGrid;
        public Vector2Int size;
        public int index = -1;

        public Transform container;
        public GameObject primaryDoor;

        public List<GameObject> spawnedVoxels = new List<GameObject>();
        public List<GameObject> spawnedProps = new List<GameObject>();

        public Transform entryAnchor;
        public Transform exitAnchor;

        // Additional metadata
        public int expectedHeight = 1;
        public bool buildCeiling = false;
        public bool IsPopulated = false;

        public List<DoorRect> doorRects = new List<DoorRect>();
    }

    public enum WallSide { North = 0, South = 1, East = 2, West = 3 }
    public class DoorRect { public WallSide side; public int start; public int width; public int height; }
    #endregion

    #region Internals
    protected System.Random _rng; // A declaração permanece
    // ADICIONADO: Propriedade para acesso seguro e inicialização "preguiçosa"
    protected System.Random Rng
    {
        get
        {
            if (_rng == null)
            {
                // Se o _rng ainda não foi criado, ele é criado agora, no primeiro uso.
                _rng = new System.Random(Environment.TickCount ^ GetInstanceID());
            }
            return _rng;
        }
    }

    protected Dictionary<GameObject, GameObject> _prefabToInstance = new Dictionary<GameObject, GameObject>();
    protected Dictionary<GameObject, Queue<GameObject>> _pool = new Dictionary<GameObject, Queue<GameObject>>();
    protected Dictionary<Transform, RoomInstance> _roomsByContainer = new Dictionary<Transform, RoomInstance>();
    protected List<RoomInstance> debugRoomInstances = new List<RoomInstance>();

    protected MaterialPropertyBlock _mpb;
    #endregion

    #region Events
    public static event Action<RoomInstance> OnRoomPopulated;
    #endregion

    #region Unity
    void Awake()
    {
        if (_mpb == null) _mpb = new MaterialPropertyBlock();

        // cria root se necessário
        if (roomsRoot == null)
        {
            var go = new GameObject("GeneratedRooms");
            go.transform.SetParent(this.transform, false);
            roomsRoot = go.transform;
        }

        // Pre-warm pool
        if (voxelFundamentalPrefab != null)
            Prewarm(voxelFundamentalPrefab, initialPoolPerPrefab);

        if (doorPrefab != null)
            Prewarm(doorPrefab, Mathf.Min(16, initialPoolPerPrefab / 4));

        if (switchPrefab != null)
            Prewarm(switchPrefab, Mathf.Min(8, initialPoolPerPrefab / 8));
    }
    #endregion

    #region Public API (compat com GameFlowManager)
    public virtual void GenerateRoom() => GenerateRoom(Vector2Int.zero, minRoomSize); // Corrigido para fornecer valores padrão
    public virtual void GenerateRoom(Vector2Int size) => GenerateRoom(Vector2Int.zero, size); // Corrigido para fornecer valores padrão

    public virtual void GenerateRoom(Vector2Int originGrid, Vector2Int size)
    {
        // create container and room instance
        var room = CreateRoomContainer(originGrid, size, -1);
        room.expectedHeight = Mathf.Max(1, doorHeight); // default; filhos podem alterar se chamarem overloads

        // decide build path
        if (generateGradually) StartCoroutine(PopulateRoomCoroutine(room, room.expectedHeight, maxVoxelsPerFrame));
        else PopulateRoomImmediate(room, room.expectedHeight);
    }

    public virtual void ClearRoom(RoomInstance room)
    {
        if (room == null) return;

        for (int i = room.spawnedVoxels.Count - 1; i >= 0; i--)
            ReturnToPool(room.spawnedVoxels[i]);
        room.spawnedVoxels.Clear();

        for (int i = room.spawnedProps.Count - 1; i >= 0; i--)
            ReturnToPool(room.spawnedProps[i]);
        room.spawnedProps.Clear();

        if (room.container != null)
        {
            if (_roomsByContainer.ContainsKey(room.container)) _roomsByContainer.Remove(room.container);
            debugRoomInstances.Remove(room);

#if UNITY_EDITOR
            if (!Application.isPlaying) DestroyImmediate(room.container.gameObject);
            else Destroy(room.container.gameObject);
#else
            Destroy(room.container.gameObject);
#endif
        }
    }
    #endregion

    #region Pool
    protected void Prewarm(GameObject prefab, int count)
    {
        if (prefab == null || count <= 0) return;
        if (!_pool.TryGetValue(prefab, out var q))
        {
            q = new Queue<GameObject>(count);
            _pool[prefab] = q;
        }

        for (int i = 0; i < count; i++)
        {
            var go = Instantiate(prefab);
            go.SetActive(false);
            q.Enqueue(go);
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
        else go = Instantiate(prefab);

        // Parent & transform
        go.transform.SetParent(parent, true);
        go.transform.position = worldPos;
        go.transform.rotation = rot;
        go.transform.localScale = Vector3.one * voxelSize;
        go.SetActive(true);
        return go;
    }

    protected void ReturnToPool(GameObject go)
    {
        if (go == null) return;
        go.SetActive(false);
        go.transform.SetParent(this.transform, false);

        foreach (var kv in _pool)
        {
            // Heurística simples: compara pelo nome do prefab
            if (kv.Key != null && go.name.StartsWith(kv.Key.name))
            {
                kv.Value.Enqueue(go);
                return;
            }
        }
        // fallback: descarta
#if UNITY_EDITOR
        if (!Application.isPlaying) DestroyImmediate(go);
        else Destroy(go);
#else
        Destroy(go);
#endif
    }
    #endregion

    #region Room build (immediate & coroutine)
    private void PopulateRoomImmediate(RoomInstance room, int height)
    {
        if (room == null) return;

        Vector3 originWorld = GridToWorld(room.originGrid);
        // piso + paredes + teto
        foreach (var cell in ComputeHollowOccupancy(room.size, height, buildCeiling: room.buildCeiling))
        {
            var go = SpawnFromPool(voxelFundamentalPrefab, room.container.position, Quaternion.identity, room.container);
            if (go != null)
            {
                go.transform.localPosition = new Vector3(cell.x * voxelSize, cell.y * voxelSize, cell.z * voxelSize);

                // --- CÓDIGO ALTERADO ---
                LogVoxelAndMicroVoxelPositions(go, "BaseRoomGenerator (Immediate)");
                // --- FIM DO CÓDIGO ---

                InitializeVoxelIfPossible(go, cell.x, cell.y, cell.z, room.size, height);
                room.spawnedVoxels.Add(go);
            }
        }

        // portas (procedurais)
        var doorRects = room.doorRects ?? ChooseProceduralDoors(room.size, null);
        room.doorRects = doorRects;
        BuildDoors(room, originWorld, doorRects);

        // interruptor
        PlaceSwitch(room);

        room.IsPopulated = true;
        try { OnRoomPopulated?.Invoke(room); } catch { }
    }

    private IEnumerator PopulateRoomCoroutine(RoomInstance room, int height, int maxPerFrame)
    {
        var occupancyList = new List<(int x, int y, int z)>(ComputeHollowOccupancy(room.size, height, buildCeiling: room.buildCeiling));
        var doorRects = room.doorRects ?? ChooseProceduralDoors(room.size, occupancyList);
        room.doorRects = doorRects;

        Vector3 originWorld = GridToWorld(room.originGrid);

        int spawnedThisFrame = 0;
        float frameBudgetSec = Mathf.Max(0.0001f, frameBudgetMs / 1000f);
        float frameStart = Time.realtimeSinceStartup;

        foreach (var cell in occupancyList)
        {
            var go = SpawnFromPool(voxelFundamentalPrefab, room.container.position, Quaternion.identity, room.container);
            if (go != null)
            {
                go.transform.localPosition = new Vector3(cell.x * voxelSize, cell.y * voxelSize, cell.z * voxelSize);

                // --- CÓDIGO ALTERADO ---
                LogVoxelAndMicroVoxelPositions(go, "BaseRoomGenerator (Coroutine)");
                // --- FIM DO CÓDIGO ---

                InitializeVoxelIfPossible(go, cell.x, cell.y, cell.z, room.size, height);
                room.spawnedVoxels.Add(go);
            }

            spawnedThisFrame++;

            // Yield if exceeded either maxPerFrame OR time budget
            if (spawnedThisFrame >= maxPerFrame || (Time.realtimeSinceStartup - frameStart) >= frameBudgetSec)
            {
                spawnedThisFrame = 0;
                frameStart = Time.realtimeSinceStartup;
                yield return null;
            }
        }

        // portas
        BuildDoors(room, originWorld, doorRects);

        // interruptor
        PlaceSwitch(room);

        room.IsPopulated = true;
        try { OnRoomPopulated?.Invoke(room); } catch { }
    }
    #endregion

    #region Room container / anchors
    protected RoomInstance CreateRoomContainer(Vector2Int originGrid, Vector2Int size, int index)
    {
        var originWorld = GridToWorld(originGrid);

        GameObject container = new GameObject($"Room_{index:000}");
        container.transform.SetParent(roomsRoot, true);
        container.transform.position = originWorld;

        var room = new RoomInstance
        {
            originGrid = originGrid,
            size = size,
            index = index,
            container = container.transform,
            expectedHeight = doorHeight,
            buildCeiling = true
        };
        _roomsByContainer[room.container] = room;
        debugRoomInstances.Add(room);

        // tenta adotar anchors pre-existentes (hub)
        room.entryAnchor = container.transform.Find("EntryAnchor");
        room.exitAnchor = container.transform.Find("ExitAnchor");

        // cria colliders básicos para trigger da sala
        var box = container.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.size = new Vector3(size.x * voxelSize, Mathf.Max(1, doorHeight) * voxelSize, size.y * voxelSize);
        box.center = new Vector3((size.x * 0.5f - 0.5f) * voxelSize,
                                 (Mathf.Max(1, doorHeight) * 0.5f) * voxelSize,
                                 (size.y * 0.5f - 0.5f) * voxelSize);

        return room;
    }
    #endregion

    #region Doors (procedural + hub)
    private void BuildDoors(RoomInstance room, Vector3 originWorld, List<DoorRect> doorRects)
    {
        if (room == null || room.container == null) return;

        // Procedural: cria containers de porta e voxels de 1 camada
        if (doorRects != null && doorRects.Count > 0)
        {
            Transform firstDoorTransform = null;

            for (int di = 0; di < doorRects.Count; di++)
            {
                bool startActiveThisDoor = (di != 0);
                var dr = doorRects[di];

                // cria container da porta
                var doorGO = new GameObject($"Door_{di}");
                // Define a layer do container da porta para "Interactable"
                doorGO.layer = LayerMask.NameToLayer("Interactable");
                doorGO.transform.SetParent(room.container, false); // localPosition = (0,0,0) -> relativo ao room.container
                doorGO.transform.localRotation = Quaternion.identity;
                doorGO.transform.localPosition = Vector3.zero;

                var createdVoxels = new List<GameObject>();

                // normaliza valores seguros
                int width = Mathf.Max(1, dr.width);
                int height = Mathf.Max(1, dr.height);
                int start = Mathf.Max(0, dr.start);
                bool isHorizontal = (dr.side == WallSide.North || dr.side == WallSide.South);

                // posiciona cada cubo 1x1x1 na parede correta, espessura = 1 voxel
                for (int w = 0; w < width; w++)
                {
                    for (int h = 0; h < height; h++)
                    {
                        int gx = 0, gz = 0;
                        switch (dr.side)
                        {
                            case WallSide.North:
                                gx = start + w;
                                gz = 0;
                                break;
                            case WallSide.South:
                                gx = start + w;
                                gz = Mathf.Max(0, room.size.y - 1);
                                break;
                            case WallSide.East:
                                gx = Mathf.Max(0, room.size.x - 1);
                                gz = start + w;
                                break;
                            case WallSide.West:
                                gx = 0;
                                gz = start + w;
                                break;
                        }

                        // assegura dentro dos limites
                        gx = Mathf.Clamp(gx, 0, Mathf.Max(0, room.size.x - 1));
                        gz = Mathf.Clamp(gz, 0, Mathf.Max(0, room.size.y - 1));
                        int vy = Mathf.Clamp(h, 0, Mathf.Max(0, room.expectedHeight - 1));

                        // =======================================================================
                        // INÍCIO DA ALTERAÇÃO: Uso do novo utilitário SpawnVoxelAtGrid
                        // =======================================================================
                        var cube = SpawnVoxelAtGrid(room.originGrid + new Vector2Int(gx, gz), vy, doorGO.transform);
                        // =======================================================================
                        // FIM DA ALTERAÇÃO
                        // =======================================================================

                        // --- CÓDIGO ALTERADO ---
                        LogVoxelAndMicroVoxelPositions(cube, "BaseRoomGenerator (Procedural Door)");
                        // --- FIM DO CÓDIGO ---

                        if (cube == null) continue;

                        // força escala adequada (1 voxel)
                        cube.transform.localScale = Vector3.one * voxelSize;

                        // aplica máscara de faces para "duas cortinas"
                        TryConfigureDoorFaces(cube, dr.side);

                        // inicializa (BaseVoxel, VoxelCache etc.)
                        InitializeVoxelIfPossible(cube, gx, vy, gz, room.size, room.expectedHeight);

                        if (!startActiveThisDoor && cube != null)
                        {
                            // Entrada começa "aberta" => voxels desativados
                            cube.SetActive(false);
                        }

                        // registra para remoção futura junto com a sala
                        room.spawnedVoxels.Add(cube);
                        createdVoxels.Add(cube);
                    }
                }

                if (createdVoxels.Count > 0)
                {
                    // --- INTEGRAÇÃO DO SEU SNIPPET: controlador por raiz da porta ---
                    EnsureDoorController(doorGO.transform, dr);

                    // Opcional: clipes de áudio, se o controlador os suportar
                    if (doorGO.TryGetComponent<VoxelDoorController>(out var ctrlAudio))
                    {
                        try { ctrlAudio.SetAudioClips(doorOpenSound, doorCloseSound, doorLockedSound); }
                        catch { /* método pode não existir nessa versão */ }
                    }
                }
                else
                {
                    // Se nenhum voxel foi criado, destrói o container para não poluir a hierarquia.
#if UNITY_EDITOR
                    if (!Application.isPlaying) DestroyImmediate(doorGO);
                    else Destroy(doorGO);
#else
                    Destroy(doorGO);
#endif
                }

                if (firstDoorTransform == null)
                    firstDoorTransform = doorGO.transform;

                if (di == 0)
                {
                    room.primaryDoor = doorGO;
                }
            }

            if (firstDoorTransform != null)
                Colorize(firstDoorTransform.gameObject, Color.yellow);
        }

        // Hub (pre-made) – se existir, ajusta a porta pelo ExitAnchor:
        if (initialHubRoom != null)
        {
            InitializePreMadeRoom(initialHubRoom);
        }
    }

    void InitializePreMadeRoom(Transform roomContainer)
    {
        var anchor = roomContainer.Find("ExitAnchor");
        if (anchor == null)
        {
            Debug.LogWarning($"[BaseRoomGenerator] A sala pré-fabricada '{roomContainer.name}' não possui um 'ExitAnchor'. Nenhuma porta será gerada.", roomContainer);
            return;
        }

        // Registra uma RoomInstance para que a sala seja gerenciada (principalmente para limpeza).
        var roomInstance = new RoomInstance
        {
            container = roomContainer,
            originGrid = new Vector2Int(Mathf.RoundToInt(roomContainer.position.x / voxelSize), Mathf.RoundToInt(roomContainer.position.z / voxelSize)),
            size = new Vector2Int(maxRoomSize.x, maxRoomSize.y), // Valor aproximado, não crítico para a porta.
            expectedHeight = this.doorHeight,
            spawnedVoxels = new List<GameObject>()
        };
        debugRoomInstances.Add(roomInstance);
        _roomsByContainer[roomContainer] = roomInstance;

        Debug.Log($"[BaseRoomGenerator] Gerando porta para a sala pré-fabricada '{roomContainer.name}' no anchor '{anchor.name}'...");

        // Cria o container da porta na posição e rotação exatas do anchor.
        var doorGO = new GameObject($"Door_FromAnchor_{roomContainer.name}");
        doorGO.layer = LayerMask.NameToLayer("Interactable");
        doorGO.transform.SetParent(roomContainer, true);
        doorGO.transform.position = anchor.position;
        doorGO.transform.rotation = anchor.rotation;

        // =======================================================================
        // INÍCIO DO BLOCO ALTERADO
        // =======================================================================
        // Valores padrão caso o AnchorInfo não seja encontrado
        int door_width = Rng.Next(doorWidthRange.x, doorWidthRange.y + 1);
        int door_height = this.doorHeight;

        var anchorInfo = anchor.GetComponent<DoorAnchorInfo>();
        if (anchorInfo != null)
        {
            // Usa as dimensões salvas pelo SimpleRoomEditorWindow
            door_width = anchorInfo.doorWidth;
            door_height = anchorInfo.doorHeight;
            Debug.Log($"Dimensões da porta lidas do AnchorInfo: {door_width}x{door_height}.");
        }
        else
        {
            Debug.LogWarning("DoorAnchorInfo não encontrado no ExitAnchor. Usando dimensões padrão.");
        }
        // =======================================================================
        // FIM DO BLOCO ALTERADO
        // =======================================================================

        var sideFromAnchor = DetermineWallSideFrom(anchor);

        var createdVoxels = new List<GameObject>();

        // Gera os voxels da porta, calculando suas posições em relação ao container da porta (o anchor).
        // Isso garante que a porta seja construída corretamente, independentemente da rotação do anchor.
        for (int w = 0; w < door_width; w++)
        {
            for (int h = 0; h < door_height; h++)
            {
                // Calcula a posição local, centralizando a porta
                Vector3 localPos = new Vector3((w - (door_width - 1) * 0.5f) * voxelSize, h * voxelSize, 0);

                // Spawna o voxel na posição do container da porta (temporariamente)
                var cube = SpawnFromPool(voxelFundamentalPrefab, doorGO.transform.position, doorGO.transform.rotation, doorGO.transform);
                if (cube == null) continue;

                // Define a posição LOCAL correta.
                cube.transform.localPosition = localPos;

                // Apply door face mask (two-curtain effect)
                TryConfigureDoorFaces(cube, sideFromAnchor);

                // --- CÓDIGO ALTERADO ---
                LogVoxelAndMicroVoxelPositions(cube, "BaseRoomGenerator (Hub Door)");
                // --- FIM DO CÓDIGO ---

                // --- FIX: garantir escala/rot local para voxels da porta ---
                cube.transform.localScale = Vector3.one * voxelSize;
                cube.transform.localRotation = Quaternion.identity;

                // Inicializa o voxel (BaseVoxel, VoxelCache, etc.)
                InitializeVoxelIfPossible(cube, 0, h, w, new Vector2Int(door_width, 1), door_height);

                createdVoxels.Add(cube);
                roomInstance.spawnedVoxels.Add(cube);
            }
        }

        if (createdVoxels.Count > 0)
        {
            // --- INTEGRAÇÃO DO SEU SNIPPET TAMBÉM NO HUB ---
            var rect = new DoorRect { side = sideFromAnchor, start = 0, width = door_width, height = door_height };
            EnsureDoorController(doorGO.transform, rect);

            // Opcional: áudio
            if (doorGO.TryGetComponent<VoxelDoorController>(out var ctrlAudio))
            {
                try { ctrlAudio.SetAudioClips(doorOpenSound, doorCloseSound, doorLockedSound); }
                catch { /* método pode não existir nessa versão */ }
            }
        }
    }
    #endregion

    #region Switch placement
    private void PlaceSwitch(RoomInstance room)
    {
        if (switchPrefab == null || room == null || room.container == null) return;

        var switchWorld = new Vector3(
            room.container.position.x + (room.size.x * 0.5f - 0.5f) * voxelSize,
            room.container.position.y + Mathf.Max(0.1f, switchWorldHeight),
            room.container.position.z + (room.size.y * 0.5f - 0.5f) * voxelSize);

        var sw = SpawnFromPool(switchPrefab, switchWorld, Quaternion.identity, room.container);
        if (sw != null)
        {
            Vector3 switchLocalPos = new Vector3(
                Mathf.RoundToInt(room.size.x * 0.5f) * voxelSize,
                Mathf.Max(0.1f, switchWorldHeight),
                Mathf.RoundToInt(room.size.y * 0.5f) * voxelSize);
            sw.transform.localPosition = switchLocalPos;
            sw.transform.LookAt(room.container.position);
            room.spawnedProps.Add(sw);
        }
    }
    #endregion

    #region Voxel init helper
    // =======================================================================
    // INÍCIO DA ALTERAÇÃO: MÉTODO InitializeVoxelIfPossible SUBSTITUÍDO
    // =======================================================================
    private void InitializeVoxelIfPossible(GameObject go, int gx, int gy, int gz, Vector2Int roomSize, int height)
    {
        if (go == null) return;

        try { go.transform.localScale = Vector3.one * voxelSize; } catch { }

        var baseVoxel = go.GetComponent<BaseVoxel>();
        if (baseVoxel != null)
        {
            try { baseVoxel.Initialize(0, true); } catch { }
        }

        int w = Mathf.Max(1, roomSize.x);
        int d = Mathf.Max(1, roomSize.y);
        int h = Mathf.Max(1, height);

        VoxelFaceController.Face mask = VoxelFaceController.Face.None;

        // Estrutura oca: pisos/tetos/parede externa
        bool isFloor = (gy == 0);
        bool isCeiling = (gy == h - 1);

        if (w == 1 || d == 1)
        {
            // fallback simplificado para degraus estreitos
            mask = VoxelFaceController.Face.North | VoxelFaceController.Face.South | VoxelFaceController.Face.East | VoxelFaceController.Face.West;
            if (isFloor) mask |= VoxelFaceController.Face.Top;
            if (isCeiling) mask |= VoxelFaceController.Face.Bottom;
        }
        else
        {
            bool interiorX = gx > 0 && gx < w - 1;
            bool interiorZ = gz > 0 && gz < d - 1;

            if (interiorX && interiorZ)
            {
                if (gy == 0) mask = VoxelFaceController.Face.Top;
                else if (gy == h - 1) mask = VoxelFaceController.Face.Bottom;
                else mask = VoxelFaceController.Face.None;
            }
            else
            {
                if (gx == 0) mask |= VoxelFaceController.Face.East;
                if (gx == w - 1) mask |= VoxelFaceController.Face.West;
                if (gz == 0) mask |= VoxelFaceController.Face.North;
                if (gz == d - 1) mask |= VoxelFaceController.Face.South;
                if (gy == 0) mask |= VoxelFaceController.Face.Top;
                if (gy == h - 1) mask |= VoxelFaceController.Face.Bottom;
            }
        }

        bool isDoorVoxel = (go.transform.parent != null && go.transform.parent.name.StartsWith("Door"));
        var faceController = go.GetComponent<VoxelFaceController>();
        if (!isDoorVoxel)
        {
            if (faceController != null)
            {
                try { faceController.ApplyFaceMask(mask, true); }
                catch { try { faceController.ApplyFaceMask(mask); } catch { } }
            }
            else
            {
                var composite = go.GetComponent<CompositeVoxel>();
                if (composite != null)
                {
                    try { composite.ApplyFaceMask((CompositeVoxel.Face)mask); }
                    catch { }
                }
            }
        }

        try
        {
            var voxelCache = VoxelCache.GetOrAdd(go, ensureAutoInit: true);
            voxelCache.SetVisible(true);
            voxelCache.SetColliderEnabled(true);
        }
        catch { }
    }
    // =======================================================================
    // FIM DA ALTERAÇÃO: MÉTODO InitializeVoxelIfPossible SUBSTITUÍDO
    // =======================================================================
    #endregion

    #region Debug helpers
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    private void LogVoxelAndMicroVoxelPositions(GameObject voxelGO, string tag)
    {
#if UNITY_EDITOR
        if (voxelGO == null) return;
        var p = voxelGO.transform.position;
        Debug.Log($"[{tag}] Voxel pos (world): X:{p.x:F2} Y:{p.y:F2} Z:{p.z:F2}", voxelGO);

        // Se existir um VoxelFaceController, loga faces
        var fc = voxelGO.GetComponent<VoxelFaceController>();
        if (fc != null)
        {
            // Supondo que ele tenha um campo público/prop 'CurrentFaceMask'
            try
            {
                var maskField = typeof(VoxelFaceController).GetField("CurrentFaceMask");
                if (maskField != null)
                {
                    var m = maskField.GetValue(fc);
                    Debug.Log($"[{tag}] FaceMask: {m}", voxelGO);
                }
            }
            catch { }
        }
#endif
    }

    private void Colorize(GameObject go, Color color)
    {
        if (go == null) return;

        var renderers = go.GetComponentsInChildren<MeshRenderer>(true);
        foreach (var renderer in renderers)
        {
            try
            {
                if (_mpb == null) _mpb = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(_mpb, 0);

                if (_mpb == null) _mpb = new MaterialPropertyBlock();
                if (_mpb != null)
                {
                    if (_mpb.IsEmpty())
                        _mpb.SetColor("_BaseColor", color);
                    else
                        _mpb.SetColor("_Color", color);

                    renderer.SetPropertyBlock(_mpb, 0);
                }
            }
            catch { }
        }
    }
    #endregion

    #region Helpers / Utilities
    // --- Added helpers for door face masking and side inference ---
    private void TryConfigureDoorFaces(GameObject go, WallSide side)
    {
        if (go == null) return;
        var fc = go.GetComponent<VoxelFaceController>();
        if (fc == null) return;

        // N/S -> ±Z ; E/W -> ±X
        var mask = (side == WallSide.North || side == WallSide.South)
            ? (VoxelFaceController.Face.North | VoxelFaceController.Face.South)
            : (VoxelFaceController.Face.East  | VoxelFaceController.Face.West);

        try { fc.ApplyFaceMask(mask, true); }
        catch { try { fc.ApplyFaceMask(mask); } catch { } }
    }

    private WallSide DetermineWallSideFrom(Transform anchor)
    {
        if (anchor == null) return WallSide.North;
        Vector3 f = anchor.forward;
        float dx = Vector3.Dot(f, Vector3.right);
        float dz = Vector3.Dot(f, Vector3.forward);
        if (Mathf.Abs(dz) >= Mathf.Abs(dx))
            return (dz >= 0f) ? WallSide.North : WallSide.South;
        else
            return (dx >= 0f) ? WallSide.East : WallSide.West;
    }

    // =======================================================================
    // INÍCIO DA CORREÇÃO: MÉTODO GridToWorld SUBSTITUÍDO
    // =======================================================================
    public Vector3 GridToWorld(Vector2Int grid)
    {
        float y = this.transform.position.y; // Posição do gerador como fallback

        // Se uma raiz de salas foi definida, usa sua altura como base inicial.
        if (roomsRoot != null)
        {
            y = roomsRoot.position.y;
        }

        // A CORREÇÃO PRINCIPAL: Se um Hub inicial está definido, procura pelo
        // ExitAnchor dentro dele e usa a altura (Y) exata do Anchor.
        // Isso garante que a nova sala se alinhe perfeitamente com a saída do Hub.
        if (initialHubRoom != null)
        {
            var exitAnchor = initialHubRoom.Find("ExitAnchor");
            if (exitAnchor != null)
            {
                // Usa a altura do Anchor como a referência definitiva.
                y = exitAnchor.position.y;
            }
            else
            {
                // Se não encontrar o Anchor, usa a altura do container do Hub como fallback.
                y = initialHubRoom.position.y;
            }
        }

        return new Vector3(grid.x * voxelSize, y, grid.y * voxelSize);
    }
    // =======================================================================
    // FIM DA CORREÇÃO: MÉTODO GridToWorld SUBSTITUÍDO
    // =======================================================================

    protected IEnumerable<(int x, int y, int z)> ComputeHollowOccupancy(Vector2Int size, int height, bool buildCeiling)
    {
        int w = Mathf.Max(1, size.x);
        int d = Mathf.Max(1, size.y);
        int h = Mathf.Max(1, height);

        for (int y = 0; y < h; y++)
        {
            bool isFloor = (y == 0);
            bool isCeiling = (y == h - 1);

            for (int x = 0; x < w; x++)
            {
                for (int z = 0; z < d; z++)
                {
                    bool isWall = (x == 0 || x == w - 1 || z == 0 || z == d - 1);

                    if (!isWall && !isFloor && !isCeiling)
                        continue; // interior oco

                    if (!buildCeiling && isCeiling)
                        continue;

                    if (isWall || isFloor || isCeiling)
                    {
                        if (h > 1 && isFloor && isCeiling) // Evita sólido em altura 1
                        {
                            if(isWall) yield return (x, y, z);
                        }
                        else if ( (isWall && isFloor) || (isWall && isCeiling) || (isFloor && isCeiling) )
                        {
                            // Apenas um para evitar duplicatas em cantos e bordas
                        }
                        else
                        {
                            yield return (x, y, z);
                        }
                    }
                }
            }
        }
    }

    protected GameObject SpawnVoxelAtGrid(Vector2Int gridXZ, int y, Transform parent)
    {
        // Mundo do container da sala (roomsRoot/hub)
        Vector3 world = new Vector3(gridXZ.x * voxelSize, parent.parent != null ? parent.parent.position.y + (y * voxelSize) : y * voxelSize, gridXZ.y * voxelSize);
        var go = SpawnFromPool(voxelFundamentalPrefab, world, Quaternion.identity, parent);
        if (go != null)
        {
            var pLocal = parent.InverseTransformPoint(world);
            go.transform.localPosition = new Vector3(pLocal.x, pLocal.y, pLocal.z);
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one * voxelSize;
        }
        return go;
    }

    private List<DoorRect> ChooseProceduralDoors(Vector2Int size, List<(int x, int y, int z)> occupancy)
    {
        var list = new List<DoorRect>();
        if (desiredDoorCount <= 0) return list;

        var availableSides = new List<WallSide> { WallSide.North, WallSide.South, WallSide.East, WallSide.West };

        for (int i = 0; i < desiredDoorCount && availableSides.Count > 0; i++)
        {
            var dr = new DoorRect();
            int sideIndex = Rng.Next(0, availableSides.Count);
            dr.side = availableSides[sideIndex];
            availableSides.RemoveAt(sideIndex); // Ensure unique sides

            bool isHorizontal = (dr.side == WallSide.North || dr.side == WallSide.South);
            int wallLength = isHorizontal ? size.x : size.y;

            dr.width = Mathf.Clamp(Rng.Next(doorWidthRange.x, doorWidthRange.y + 1), 1, wallLength - 2); // -2 to avoid corners
            dr.start = Rng.Next(1, wallLength - dr.width); // Start > 0 and < wallLength
            dr.height = Mathf.Max(1, doorHeight);

            list.Add(dr);
        }

        return list;
    }
    #endregion

    #region Snippet (ADD): EnsureDoorController
    /// <summary>
    /// Garante que exista um VoxelDoorController no root da porta, faça a configuração básica
    /// (lado/voxelSize) e varra os filhos para se auto-inicializar.
    /// </summary>
    protected void EnsureDoorController(Transform doorRoot, in DoorRect rect)
    {
        if (!doorRoot) return;

        if (!doorRoot.TryGetComponent<VoxelDoorController>(out var ctrl))
            ctrl = doorRoot.gameObject.AddComponent<VoxelDoorController>();

        // Configura dados mínimos (lado da parede e voxelSize para ordenar em onda)
        try { ctrl.Setup(rect.side, voxelSize); } catch { /* compat */ }

        // Revarre filhos (blocos gerados programaticamente)
        try { ctrl.InitializeFromChildren(); } catch { /* compat */ }
    }
    #endregion
}
