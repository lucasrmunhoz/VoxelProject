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
// ALTERADO (HOJE): O método GridToWorld agora preserva a altura Y do roomsRoot ou do hub para posicionamento correto das salas.
// =======================================================================
// FIM DAS ALTERAÇÕES APLICADAS
// =======================================================================
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text; // ADICIONADO para usar StringBuilder
using UnityEngine;

[DisallowMultipleComponent]
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
    public Vector2Int doorWidthRange = new Vector2Int(1, 3);
    [Min(1)] public int doorHeight = 3;
    public int desiredDoorCount = 1;

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
    [Tooltip("Budget em milissegundos por frame para geração gradual. Use ~8-12 ms para manter a taxa de frames estável.")]
    public int frameBudgetMilliseconds = 8;
    
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
    // internal fallback root (kept for backward compat)
    private Transform _internalRoomsRoot;
    private Dictionary<Transform, RoomInstance> _roomsByContainer = new Dictionary<Transform, RoomInstance>();
    private Dictionary<GameObject, Queue<GameObject>> _pool = new Dictionary<GameObject, Queue<GameObject>>();
    public List<RoomInstance> debugRoomInstances = new List<RoomInstance>();
    #endregion

    #region Events
    /// <summary>
    /// Disparado quando a população de uma sala é concluída (voxels, portas e switch criados).
    /// </summary>
    public event Action<RoomInstance> OnRoomPopulated;
    #endregion

    #region MaterialPropertyBlock cache
    // MaterialPropertyBlock para aplicar cores sem duplicar materiais.
    private static MaterialPropertyBlock _mpb;
    #endregion

    #region Unity lifecycle
    private void Awake()
    {
        // REMOVIDO: A inicialização do _rng foi movida para a propriedade Rng para evitar race conditions.
        // _rng = new System.Random(Environment.TickCount ^ GetInstanceID());

        // Se o roomsRoot foi setado via inspector ou pelo GameFlowManager, respeita-o.
        if (roomsRoot != null)
        {
            // não precisa criar nada aqui.
            _internalRoomsRoot = roomsRoot;
        }
        else
        {
            // tenta encontrar um container filho já existente
            var existing = transform.Find("GeneratedRooms");
            if (existing != null) _internalRoomsRoot = existing;
            else
            {
                var go = new GameObject("GeneratedRooms");
                go.transform.SetParent(this.transform, false);
                _internalRoomsRoot = go.transform;
            }
            // manter roomsRoot nulo para indicar que gerenciador externo não injetou; mas use _internalRoomsRoot internamente.
        }

        // Prewarm pools
        if (voxelFundamentalPrefab != null && initialPoolPerPrefab > 0)
            PrewarmPool(voxelFundamentalPrefab, Mathf.Min(initialPoolPerPrefab, 512));
        if (doorPrefab != null && initialPoolPerPrefab > 0)
            PrewarmPool(doorPrefab, Mathf.Min(initialPoolPerPrefab / 4, 128));
        if (switchPrefab != null && initialPoolPerPrefab > 0)
            PrewarmPool(switchPrefab, Mathf.Min(initialPoolPerPrefab / 8, 64));

        if (_mpb == null) _mpb = new MaterialPropertyBlock();
    }

    private void Start()
    {
        // =======================================================================
        // INÍCIO DA ALTERAÇÃO: Inicializa portas em salas pré-fabricadas
        // =======================================================================
        if (initialHubRoom != null)
        {
            InitializePreMadeRoom(initialHubRoom);
        }
        // =======================================================================
        // FIM DA ALTERAÇÃO
        // =======================================================================
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
        room.buildCeiling = false;

        // choose doors early so they are known (and stored on room)
        var occupancyListForDoors = new List<(int x, int y, int z)>(ComputeHollowOccupancy(room.size, room.expectedHeight));
        var doorRects = ChooseProceduralDoors(room.size, occupancyListForDoors);
        room.doorRects = doorRects;

        if (generateGradually)
            StartCoroutine(PopulateRoomCoroutine(room, room.expectedHeight, maxVoxelsPerFrame));
        else
            PopulateRoomImmediate(room, room.expectedHeight);
    }

    /// <summary>
    /// Retorna um tamanho aleatório para uma sala, usando as configurações base.
    /// Este método pode ser sobrescrito por geradores específicos para usar suas próprias regras de tamanho.
    /// </summary>
    public virtual Vector2Int GetRandomSize(System.Random rng)
    {
        int w = rng.Next(minRoomSize.x, maxRoomSize.x + 1);
        int d = rng.Next(minRoomSize.y, maxRoomSize.y + 1);
        return new Vector2Int(w, d);
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

    public virtual void ClearRoom(Transform container)
    {
        if (container == null) return;
        if (_roomsByContainer.TryGetValue(container, out var r)) { ClearRoom(r); return; }
        var match = debugRoomInstances.Find(x => x.container == container);
        if (match != null) ClearRoom(match);
        else
        {
#if UNITY_EDITOR
            if (!Application.isPlaying) DestroyImmediate(container.gameObject);
            else Destroy(container.gameObject);
#else
            Destroy(container.gameObject);
#endif
        }
    }

    public virtual void ClearRoom(GameObject container) => ClearRoom(container?.transform);
    #endregion

    #region Pooling
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
            var parent = (roomsRoot != null) ? roomsRoot : _internalRoomsRoot;
            if (parent != null) go.transform.SetParent(parent, true);
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
        go.transform.SetParent(parent, false);
        go.transform.position = worldPos;
        go.transform.rotation = rot;

        // Activate before initializing cache so Awake/OnEnable run
        go.SetActive(true);

        // Ensure PoolableObject tag is present
        var tag = go.GetComponent<PoolableObject>() ?? go.AddComponent<PoolableObject>();
        tag.OriginalPrefab = prefab;

        // --- INTEGRAÇÃO COM VoxelCache: chama OnSpawnFromPool para preparar o cache e BaseVoxel ---
        try
        {
            var cache = VoxelCache.GetOrAdd(go, ensureAutoInit: true);
            // OnSpawnFromPool aceita parent/pos/rot para reparenting or init semantics
            cache.OnSpawnFromPool(parent: parent, worldPosition: worldPos, worldRotation: rot);
        }
        catch (Exception)
        {
            // degrade silently para manter robustez
        }

        // --- SAFE DEFAULT SCALE (evita objetos do pool com escala indevida) ---
        go.transform.localScale = Vector3.one * voxelSize;

        return go;
    }

    protected void ReturnToPool(GameObject go)
    {
        if (go == null) return;
        var tag = go.GetComponent<PoolableObject>();
        if (tag == null || tag.OriginalPrefab == null) { Destroy(go); return; }

        // Ensure cache exists (safe) and let it prepare object for pooling (disable visuals, clear MPB, etc.)
        Transform poolRoot = (roomsRoot != null) ? roomsRoot : _internalRoomsRoot;
        try
        {
            var cache = VoxelCache.GetOrAdd(go, ensureAutoInit: true);
            cache.OnReturnToPool(poolRoot);
        }
        catch (Exception)
        {
            // fallback behavior: minimal cleanup
            try
            {
                // disable visuals/colliders if possible
                var rends = go.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < rends.Length; i++) rends[i].enabled = false;
                var cols = go.GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < cols.Length; i++) cols[i].enabled = false;
            }
            catch { }
        }

        // ensure parented to pool root and inactive
        if (poolRoot != null) go.transform.SetParent(poolRoot, true);
        go.SetActive(false);

        if (!_pool.ContainsKey(tag.OriginalPrefab)) _pool[tag.OriginalPrefab] = new Queue<GameObject>();
        _pool[tag.OriginalPrefab].Enqueue(go);
    }
    #endregion

    #region Container creation
    // =======================================================================
    // INÍCIO DA ALTERAÇÃO: MÉTODO CreateRoomContainer SUBSTITUÍDO
    // =======================================================================
    protected RoomInstance CreateRoomContainer(Vector2Int originGrid, Vector2Int size, int index = -1)
    {
        string name = $"Room_{originGrid.x}_{originGrid.y}";
        var go = new GameObject(name);
        var parentRoot = (roomsRoot != null) ? roomsRoot : _internalRoomsRoot;
        if (parentRoot != null) go.transform.SetParent(parentRoot, false);
        else go.transform.SetParent(this.transform, false);

        // =======================================================================
        // INÍCIO DA CORREÇÃO DE ALINHAMENTO
        // =======================================================================
        // Calcula a posição mundial correta, incluindo a altura Y do ExitAnchor.
        Vector3 worldPosition = GridToWorld(originGrid);
        go.transform.position = worldPosition;
        
        // Adiciona um log para depuração. Verifique este valor no console do Unity.
        Debug.Log($"[BaseRoomGenerator] Posição final do container da sala '{name}' definida como: {worldPosition.ToString("F3")}");
        // =======================================================================
        // FIM DA CORREÇÃO DE ALINHAMENTO
        // =======================================================================

        var t = go.transform;

        var room = new RoomInstance
        {
            originGrid = originGrid,
            size = size,
            index = index,
            container = t
        };

        var entryGO = new GameObject("EntryAnchor");
        entryGO.transform.SetParent(t, false);
        entryGO.transform.localPosition = Vector3.zero;
        room.entryAnchor = entryGO.transform;

        var exitGO = new GameObject("ExitAnchor");
        exitGO.transform.SetParent(t, false);
        exitGO.transform.localPosition = new Vector3(size.x * voxelSize, 0f, size.y * voxelSize);
        room.exitAnchor = exitGO.transform;

        var roomCollider = go.AddComponent<BoxCollider>();
        roomCollider.isTrigger = true;
        roomCollider.center = new Vector3(
            (size.x - 1) * voxelSize * 0.5f,
            (Mathf.Max(1, doorHeight) - 1) * voxelSize * 0.5f,
            (size.y - 1) * voxelSize * 0.5f
        );
        roomCollider.size = new Vector3(
            size.x * voxelSize,
            Mathf.Max(1, doorHeight) * voxelSize,
            size.y * voxelSize
        );

        _roomsByContainer[t] = room;
        debugRoomInstances.Add(room);

        return room;
    }
    // =======================================================================
    // FIM DA ALTERAÇÃO
    // =======================================================================
    #endregion

    #region Room population core (immediate & gradual)
    private IEnumerable<(int x, int y, int z)> ComputeHollowOccupancy(Vector2Int size, int height, bool buildFloor = true, bool buildWalls = true, bool buildCeiling = false)
    {
        int w = size.x;
        int d = size.y;
        int h = Mathf.Max(1, height);

        for (int y = 0; y < h; y++)
        {
            for (int z = 0; z < d; z++)
            {
                for (int x = 0; x < w; x++)
                {
                    bool isWall = (x == 0 || x == w - 1 || z == 0 || z == d - 1);
                    bool isFloor = (y == 0);
                    bool isCeiling = (y == h - 1);

                    if ((isWall && buildWalls) || (isFloor && buildFloor) || (isCeiling && buildCeiling))
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

    private void PopulateRoomImmediate(RoomInstance room, int height)
    {
        var occupancyList = new List<(int x, int y, int z)>(ComputeHollowOccupancy(room.size, height, buildCeiling: room.buildCeiling));
        var doorRects = room.doorRects ?? ChooseProceduralDoors(room.size, occupancyList);
        room.doorRects = doorRects;

        Vector3 originWorld = GridToWorld(room.originGrid);

        // --- CRIA CUBOS 1x1 PARA AS PORTAS E ANEXA VoxelDoorController ---
        Transform primaryDoorContainer = CreateDoorObjects(room, originWorld, doorRects);
        if (primaryDoorContainer != null)
        {
            room.primaryDoor = primaryDoorContainer.gameObject;
        }

        foreach (var cell in occupancyList)
        {
            if (IsCellInAnyDoor(cell.x, cell.y, cell.z, room.size, doorRects)) continue;
            
            // Spawna o objeto na origem do container para evitar problemas de posicionamento inicial
            var go = SpawnFromPool(voxelFundamentalPrefab, room.container.position, Quaternion.identity, room.container);
            if (go != null)
            {
                // Define a posição LOCAL do voxel dentro do container da sala. Esta é a correção.
                go.transform.localPosition = new Vector3(cell.x * voxelSize, cell.y * voxelSize, cell.z * voxelSize);

                // --- CÓDIGO ALTERADO ---
                LogVoxelAndMicroVoxelPositions(go, "BaseRoomGenerator (Immediate)");
                // --- FIM DO CÓDIGO ---

                InitializeVoxelIfPossible(go, cell.x, cell.y, cell.z, room.size, height);
                room.spawnedVoxels.Add(go);
            }
        }

        // place switch
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

        // --- CRIA CUBOS 1x1 PARA AS PORTAS E ANEXA VoxelDoorController ---
        Transform primaryDoorContainer = CreateDoorObjects(room, originWorld, doorRects);
        if (primaryDoorContainer != null)
        {
            room.primaryDoor = primaryDoorContainer.gameObject;
        }

        int spawnedThisFrame = 0;
        float frameBudgetSec = Mathf.Max(1, frameBudgetMilliseconds) / 1000f; // e.g., 0.008s
        float frameStart = Time.realtimeSinceStartup;

        for (int i = 0; i < occupancyList.Count; i++)
        {
            var cell = occupancyList[i];

            if (IsCellInAnyDoor(cell.x, cell.y, cell.z, room.size, doorRects)) continue;

            // Spawna o objeto na origem do container para evitar problemas de posicionamento inicial
            var go = SpawnFromPool(voxelFundamentalPrefab, room.container.position, Quaternion.identity, room.container);
            if (go != null)
            {
                // Define a posição LOCAL do voxel dentro do container da sala. Esta é a correção.
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
                // devolve um frame — mantém jogo responsivo
                yield return null;
            }
        }

        PlaceSwitch(room);

        room.IsPopulated = true;
        try { OnRoomPopulated?.Invoke(room); } catch { }

        yield break;
    }
    #endregion
    
    #region Pre-made Room Handling
    // =======================================================================
    // INÍCIO DA ALTERAÇÃO: Novo método para "adotar" salas do editor
    // =======================================================================
    /// <summary>
    /// Procura por um ExitAnchor em uma sala pré-fabricada e gera uma porta de voxels no local.
    /// Esta versão corrigida utiliza a posição e rotação do Anchor para posicionar a porta corretamente.
    /// </summary>
    /// <param name="roomContainer">O Transform do container da sala.</param>
    protected virtual void InitializePreMadeRoom(Transform roomContainer)
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
            Debug.Log($"Dimensões da porta lidas do Anchor: {door_width}x{door_height}");
        }
        else
        {
            Debug.LogWarning($"'ExitAnchor' não possui o componente 'DoorAnchorInfo'. A porta será gerada com um tamanho aleatório.");
        }
        // =======================================================================
        // FIM DO BLOCO ALTERADO
        // =======================================================================
        
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
                
                // --- CÓDIGO ALTERADO ---
                LogVoxelAndMicroVoxelPositions(cube, "BaseRoomGenerator (Hub Door)");
                // --- FIM DO CÓDIGO ---

                // --- FIX: garantir escala/rot local para voxels da porta ---
                cube.transform.localScale = Vector3.one * voxelSize;
                cube.transform.localRotation = Quaternion.identity; // garante orientação local limpa
                // Em seguida inicialize o voxel como já estava:
                InitializeVoxelIfPossible(cube, 0, h, w, new Vector2Int(door_width, 1), door_height);
                
                // Adiciona aos registros para gerenciamento.
                roomInstance.spawnedVoxels.Add(cube);
                createdVoxels.Add(cube);
            }
        }

        // Adiciona e configura o VoxelDoorController se algum voxel foi criado.
        if (createdVoxels.Count > 0)
        {
            var doorController = doorGO.AddComponent<VoxelDoorController>();
            try
            {
                // Configura o VoxelDoorController com os clipes de áudio e inicializa.
                doorController.SetAudioClips(doorOpenSound, doorCloseSound, doorLockedSound);
                doorController.Initialize(createdVoxels, animateOnSetup: false);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, this); // Loga a exceção para facilitar a depuração.
            }
        }
        else
        {
            // Se nenhum voxel foi criado, destrói o container para não poluir a hierarquia.
#if UNITY_EDITOR
            if (!Application.isPlaying) DestroyImmediate(doorGO);
            else
#endif
                Destroy(doorGO);
        }
    }
    // =======================================================================
    // FIM DA ALTERAÇÃO
    // =======================================================================
    #endregion

    #region Door placement helpers
    /// <summary>
    /// Cria os cubos 1x1 por porta (preenche o "buraco" da porta) e anexa um VoxelDoorController.
    /// Retorna o Transform do primeiro door container criado (ou null se nenhum criado).
    /// </summary>
    protected Transform CreateDoorObjects(RoomInstance room, Vector3 originWorld, List<DoorRect> doorRects)
    {
        if (room == null || doorRects == null || doorRects.Count == 0) return null;

        Transform firstDoorTransform = null;

        for (int di = 0; di < doorRects.Count; di++)
        {
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

                    // inicializa (BaseVoxel, VoxelCache etc.)
                    InitializeVoxelIfPossible(cube, gx, vy, gz, room.size, room.expectedHeight);

                    // registra para remoção futura junto com a sala
                    room.spawnedVoxels.Add(cube);
                    createdVoxels.Add(cube);
                }
            }

            if (createdVoxels.Count > 0)
            {
                var doorController = doorGO.AddComponent<VoxelDoorController>();
                try
                {
                    // =======================================================================
                    // INÍCIO DA ALTERAÇÃO SUGERIDA: Uso do método SetAudioClips
                    // =======================================================================
                    // Configura o VoxelDoorController com os clipes de áudio e inicializa.
                    doorController.SetAudioClips(doorOpenSound, doorCloseSound, doorLockedSound);
                    doorController.Initialize(createdVoxels, animateOnSetup: false);
                    // =======================================================================
                    // FIM DA ALTERAÇÃO SUGERIDA
                    // =======================================================================
                }
                catch (Exception)
                {
                    // degrade gracefully se assinatura mudar; não quebre geração
                }

                if (firstDoorTransform == null) firstDoorTransform = doorGO.transform;
            }
            else
            {
                // se nenhum voxel foi criado, destrói o container para não poluir hierarquia
#if UNITY_EDITOR
                if (!Application.isPlaying) DestroyImmediate(doorGO);
                else
#endif
                    Destroy(doorGO);
            }
        }

        return firstDoorTransform;
    }

    private bool IsCellInAnyDoor(int x, int y, int z, Vector2Int roomSize, List<DoorRect> doorRects)
    {
        if (doorRects == null) return false;
        foreach (var dr in doorRects)
        {
            if (y < 0 || y >= dr.height) continue;
            switch (dr.side)
            {
                case WallSide.North: // z == 0
                    if (z == 0 && x >= dr.start && x < dr.start + dr.width) return true;
                    break;
                case WallSide.South: // z == roomSize.y - 1
                    if (z == roomSize.y - 1 && x >= dr.start && x < dr.start + dr.width) return true;
                    break;
                case WallSide.West:  // x == 0
                    if (x == 0 && z >= dr.start && z < dr.start + dr.width) return true;
                    break;
                case WallSide.East: // x == roomSize.x - 1
                    if (x == roomSize.x - 1 && z >= dr.start && z < dr.start + dr.width) return true;
                    break;
            }
        }
        return false;
    }

    // =======================================================================
    // ALTERAÇÃO SUGERIDA: Método PlacePrimaryDoor removido por ser obsoleto.
    // =======================================================================
    #endregion

    #region Switch placement
    // =======================================================================
    // INÍCIO DA ALTERAÇÃO SUGERIDA (PlaceSwitch)
    // =======================================================================
    // Substitua o método PlaceSwitch inteiro
    protected void PlaceSwitch(RoomInstance room)
    {
        if (room == null || switchPrefab == null) return;

        var candidates = new List<Vector3>();
        int w = room.size.x;
        int d = room.size.y;

        // Adiciona posições LOCAIS na parede como candidatas
        for (int x = 1; x < w - 1; x++) {
            candidates.Add(new Vector3(x * voxelSize, switchWorldHeight, 0)); // Parede Sul (local)
            candidates.Add(new Vector3(x * voxelSize, switchWorldHeight, (d - 1) * voxelSize)); // Parede Norte (local)
        }
        for (int z = 1; z < d - 1; z++) {
            candidates.Add(new Vector3(0, switchWorldHeight, z * voxelSize)); // Parede Oeste (local)
            candidates.Add(new Vector3((w - 1) * voxelSize, switchWorldHeight, z * voxelSize)); // Parede Leste (local)
        }

        if (candidates.Count == 0) return;

        // A lógica original para remover candidatos perto da porta era falha.
        // A correção adequada exigiria calcular a posição local central de cada porta.
        // Por simplicidade e para seguir a sugestão, essa verificação foi omitida.
        // Se necessário, pode ser reimplementada de forma mais robusta aqui.
        
        // Pega uma posição LOCAL aleatória
        Vector3 switchLocalPos = candidates[Rng.Next(0, candidates.Count)];

        // Spawna o interruptor e define sua posição LOCAL
        var sw = SpawnFromPool(switchPrefab, room.container.position, Quaternion.identity, room.container);
        if (sw != null)
        {
            sw.transform.localPosition = switchLocalPos;
            // Opcional: rotaciona o interruptor para "olhar" para o centro da sala (usa a posição do container no mundo)
            sw.transform.LookAt(room.container.position);

            room.spawnedProps.Add(sw);
        }
    }
    // =======================================================================
    // FIM DA ALTERAÇÃO SUGERIDA
    // =======================================================================
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

        // =======================================================================
        // INÍCIO DA CORREÇÃO DAS FRESTAS NA PORTA
        // =======================================================================
        // Verifica se o voxel pertence a um container de porta.
        var parent = go.transform.parent;
        bool isDoorVoxel = parent != null && parent.name.StartsWith("Door", StringComparison.OrdinalIgnoreCase);

        // Se NÃO for um voxel de porta, calcula a máscara para criar uma sala oca.
        if (!isDoorVoxel)
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
        // Se FOR um voxel de porta, a máscara permanece Face.None por padrão,
        // garantindo que ele fique completamente invisível até que a porta seja animada.
        // O VoxelDoorController irá gerenciar sua visibilidade.
        // =======================================================================
        // FIM DA CORREÇÃO DAS FRESTAS NA PORTA
        // =======================================================================

        var faceController = go.GetComponent<VoxelFaceController>();
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

        try
        {
            var voxelCache = VoxelCache.GetOrAdd(go, ensureAutoInit: true);
            voxelCache.SetVisible(true);
            voxelCache.SetColliderEnabled(true);
        }
        catch { }
    }
    // =======================================================================
    // FIM DA ALTERAÇÃO
    // =======================================================================
    #endregion

    #region Coloring utility (MaterialPropertyBlock)
    protected void TryApplyColorToVoxel(GameObject voxelGO, Color color)
    {
        if (voxelGO == null) return;
        
        var cache = VoxelCache.GetOrAdd(voxelGO);
        if(cache != null)
        {
            cache.ApplyColor(color);
            return;
        }

        // Fallback if no cache
        var renderer = voxelGO.GetComponent<Renderer>();
        if (renderer != null)
        {
            try
            {
                _mpb.Clear();
                if (renderer.sharedMaterial != null && renderer.sharedMaterial.HasProperty("_BaseColor"))
                    _mpb.SetColor("_BaseColor", color);
                else
                    _mpb.SetColor("_Color", color);

                renderer.SetPropertyBlock(_mpb, 0);
            }
            catch { }
        }
    }
    #endregion

    #region Helpers / Utilities
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
    // FIM DA CORREÇÃO
    // =======================================================================
    #endregion

    #region Debug / Helpers
    private RoomInstance FindMatchingRoom(Vector2Int origin, Vector2Int size)
    {
        for (int i = debugRoomInstances.Count - 1; i >= 0; i--)
        {
            var r = debugRoomInstances[i];
            if (r == null) continue;
            if (r.originGrid == origin && r.size == size)
                return r;
        }
        if (debugRoomInstances.Count > 0) return debugRoomInstances[debugRoomInstances.Count - 1];
        return null;
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
            dr.start = Rng.Next(1, wallLength - dr.width); // Start from 1 to avoid corners
            dr.height = Mathf.Max(1, doorHeight);
            list.Add(dr);
        }
        return list;
    }
    
    // =======================================================================
    // INÍCIO DA ALTERAÇÃO: Novo método auxiliar para Log de Posições
    // =======================================================================
    /// <summary>
    /// Imprime no console a posição mundial do Voxel Fundamental e de todos os seus microvoxels (filhos).
    /// </summary>
    /// <param name="fundamentalPrefab">A instância do Voxel Fundamental a ser inspecionada.</param>
    /// <param name="context">Uma string para identificar a origem da chamada (ex: "Immediate", "Coroutine").</param>
    private void LogVoxelAndMicroVoxelPositions(GameObject fundamentalPrefab, string context)
    {
        if (fundamentalPrefab == null) return;

        // Usa StringBuilder para concatenação de strings eficiente
        var logMessage = new StringBuilder();

        // Adiciona o contexto e a posição do pai
        logMessage.AppendLine($"[{context}] VoxelFundamentalPrefab '{fundamentalPrefab.name}' posicionado no mundo em: {fundamentalPrefab.transform.position.ToString("F3")}");
        logMessage.AppendLine("  Microvoxels populados:");

        // Pega todas as transforms filhas. GetComponentsInChildren inclui o pai, então precisamos pulá-lo.
        var childTransforms = fundamentalPrefab.GetComponentsInChildren<Transform>();
        
        bool hasMicrovoxels = false;
        foreach (var child in childTransforms)
        {
            // Pula a transform do próprio pai
            if (child == fundamentalPrefab.transform)
            {
                continue;
            }

            hasMicrovoxels = true;
            // Adiciona o nome e a posição do filho
            logMessage.AppendLine($"    - Microvoxel '{child.name}' na posição mundial: {child.position.ToString("F3")}");
        }

        if (!hasMicrovoxels)
        {
            logMessage.AppendLine("    - (Nenhum microvoxel/objeto filho encontrado)");
        }

        // Imprime o bloco completo no console
        Debug.Log(logMessage.ToString());
    }
    // =======================================================================
    // FIM DA ALTERAÇÃO
    // =======================================================================
    #endregion

    // =======================================================================
    // INÍCIO DA ALTERAÇÃO: Adição do novo utilitário SpawnVoxelAtGrid
    // =======================================================================
    // utilitário: instancia/obtém voxel e posiciona exatamente no GRID (sem cálculos ad-hoc)
    private GameObject SpawnVoxelAtGrid(Vector2Int gridPos, int yLayer, Transform parentContainer, bool log = false)
    {
        // world position baseado no grid
        Vector3 world = GridToWorld(gridPos);
        // acrescenta o layer vertical (cada layer = voxelSize)
        world.y += yLayer * voxelSize;

        // instancia / pega do pool, aproveitando o método existente que já lida com parenting e pooling
        GameObject go = SpawnFromPool(voxelFundamentalPrefab, world, Quaternion.identity, parentContainer);
        if (go == null) return null;

        // SpawnFromPool já lida com escala e rotação, mas podemos garantir aqui por segurança
        go.transform.localScale = Vector3.one * voxelSize;
        go.transform.rotation = Quaternion.identity;

        if (log)
            Debug.Log($"[SpawnVoxelAtGrid] pos grid=({gridPos.x},{yLayer},{gridPos.y}) -> world={world}, parent={parentContainer?.name}");

        return go;
    }
    // =======================================================================
    // FIM DA ALTERAÇÃO
    // =======================================================================
}