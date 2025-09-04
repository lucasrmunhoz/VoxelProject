// GameFlowManager.cs
// Gerenciador de fluxo de jogo / streaming procedural de salas.
// (versão ajustada: evita yield dentro de try/catch; corrige manipulação do LinkedList<ActiveRoom>)
// Compatível com BaseRoomGenerator e variações (GenerateRoom overloads, ClearRoom, etc.)
// Recursos principais:
//  - Mapeamento determinístico por seed
//  - Fila assíncrona de geração de salas (evita spikes)
//  - Gestão de salas ativas com limite (LRU) e remoção/reciclagem automática
//  - Robustez via reflection para múltiplas assinaturas de API do generator
//  - Hooks/events para integração (UI, NavMesh, som...)
//  - Fechamento de portas por reflexão quando jogador entra
//  - Logs detalhados opcionais
//
// Nota: adapte nomes de container/padrões se seu BaseRoomGenerator usa outro esquema de nomeação.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

[DisallowMultipleComponent]
public class GameFlowManager : MonoBehaviour
{
    [Header("Referências")]
    [Tooltip("Componente que gera salas. Pode ser BaseRoomGenerator, SimpleRoomGenerator ou outro com métodos compatíveis.")]
    public MonoBehaviour baseRoomGenerator;

    [Tooltip("Parent para todos os containers de sala (opcional). Será criado se nulo).")]
    public Transform roomsRoot;

    [Header("Mapeamento")]
    [Tooltip("Quantas salas mapear (tentativas).")]
    public int mapRoomCount = 100;

    [Tooltip("Seed; 0 = usa hora atual (não determinístico).")]
    public int seed = 0;

    [Tooltip("Tentar posicionar cada sala até N vezes antes de desistir.")]
    public int maxPlacementAttemptsPerRoom = 60;

    [Tooltip("Tamanhos min/max para rooms (em voxels).")]
    public Vector2Int roomSizeMin = new Vector2Int(4, 4);
    public Vector2Int roomSizeMax = new Vector2Int(12, 12);

    [Header("Streaming / Ativação")]
    [Tooltip("Número máximo de salas ativas simultaneamente (LRU). Salas antigas serão descarregadas.")]
    [Min(1)] public int maxActiveRooms = 6;

    [Tooltip("Se true, PopulateRoomAtIndex será enfileirada e executada de forma assíncrona (recomendado).")]
    public bool useAsyncGeneration = true;

    [Tooltip("Timeout em segundos para esperar a geração completar antes de logar erro (apenas diagnósticos).")]
    public float generationTimeout = 12f;

    [Header("Player / Flow")]
    [Tooltip("Transform do jogador para determinar quando trocar salas (opcional).")]
    public Transform playerTransform;

    [Tooltip("Distância em unidades para considerar que o jogador saiu de uma sala (usado para reciclar).")]
    public float unloadDistance = 32f;

    [Header("Tuning / Debug")]
    public bool verbose = false;

    // ---------- Tipos internos ----------
    [Serializable]
    public struct RoomPlan
    {
        public Vector2Int originGrid;
        public Vector2Int size;
        public int index;
    }

    private class ActiveRoom
    {
        public RoomPlan plan;
        public Transform container;
        public int index;
        public DateTime loadedAt;
        public int usageTick; // para LRU
    }

    // ---------- Estado ----------
    private System.Random _rng;
    private List<RoomPlan> _roomPlans = new List<RoomPlan>();
    private HashSet<Vector2Int> _occupiedTiles = new HashSet<Vector2Int>();
    private LinkedList<ActiveRoom> _activeRooms = new LinkedList<ActiveRoom>(); // MRU at front
    private Dictionary<Transform, ActiveRoom> _byContainer = new Dictionary<Transform, ActiveRoom>();

    private Queue<int> _generationQueue = new Queue<int>();
    private bool _isGenerating = false;
    private int _usageCounter = 0;

    // Events para integração externa
    public event Action<RoomPlan, Transform> OnRoomLoaded;
    public event Action<RoomPlan> OnRoomUnloaded;
    public event Action OnMapReady;

    // ---------- Unity ----------
    private void Awake()
    {
        if (seed == 0) seed = Environment.TickCount;
        _rng = new System.Random(seed);

        if (roomsRoot == null)
        {
            var go = GameObject.Find("RoomsRoot");
            if (go == null)
            {
                go = new GameObject("RoomsRoot");
                go.transform.SetParent(null);
            }
            roomsRoot = go.transform;
        }

        MapAllRoomsLayout();
        OnMapReady?.Invoke();
    }

    private void Start()
    {
        // If desired, auto-start first room at index 0
        // StartProceduralFlow() should be called by UI/Hub normally
    }

    private void Update()
    {
        // Optionally manage unloading by distance to player
        if (playerTransform != null)
        {
            ManageUnloadByPlayerDistance();
        }
    }

    // ---------- Map / layout ----------
    public void MapAllRoomsLayout()
    {
        _roomPlans.Clear();
        _occupiedTiles.Clear();

        Vector2Int cursor = Vector2Int.zero;
        // Optionally derive cursor from player's position (if available)
        if (playerTransform != null)
        {
            var wp = playerTransform.position;
            cursor = new Vector2Int(Mathf.RoundToInt(wp.x), Mathf.RoundToInt(wp.z));
        }

        for (int i = 0; i < mapRoomCount; i++)
        {
            bool placed = false;
            for (int attempt = 0; attempt < maxPlacementAttemptsPerRoom; attempt++)
            {
                Vector2Int size = RandomRoomSize();
                Vector2Int origin = FindOriginForIndex(i, cursor, size);
                if (IsAreaFree(origin, size))
                {
                    MarkArea(origin, size);
                    var plan = new RoomPlan { originGrid = origin, size = size, index = i };
                    _roomPlans.Add(plan);
                    // advance cursor heuristically
                    cursor = ChooseRandomEdgeGrid(origin, size);
                    placed = true;
                    break;
                }
            }
            if (!placed)
            {
                if (verbose) Debug.LogWarning($"[GameFlowManager] Não foi possível posicionar sala #{i} após {maxPlacementAttemptsPerRoom} tentativas.");
                break;
            }
        }

        if (verbose) Debug.Log($"[GameFlowManager] Mapeamento completo: {_roomPlans.Count} salas (seed={seed}).");
    }

    private Vector2Int RandomRoomSize()
    {
        int w = _rng.Next(roomSizeMin.x, roomSizeMax.x + 1);
        int d = _rng.Next(roomSizeMin.y, roomSizeMax.y + 1);
        return new Vector2Int(w, d);
    }

    private Vector2Int FindOriginForIndex(int index, Vector2Int cursor, Vector2Int size)
    {
        var dirs = new List<Vector2Int> { new Vector2Int(1, 0), new Vector2Int(-1, 0), new Vector2Int(0, 1), new Vector2Int(0, -1) };
        var dir = dirs[_rng.Next(0, dirs.Count)];
        Vector2Int offset;
        if (dir.x != 0) offset = new Vector2Int((dir.x > 0) ? size.x : -size.x, 0);
        else offset = new Vector2Int(0, (dir.y > 0) ? size.y : -size.y);

        Vector2Int origin = cursor + offset + new Vector2Int(_rng.Next(-2, 3), _rng.Next(-2, 3));
        return origin;
    }

    private Vector2Int ChooseRandomEdgeGrid(Vector2Int origin, Vector2Int size)
    {
        int side = _rng.Next(0, 4);
        switch (side)
        {
            case 0: return new Vector2Int(origin.x + size.x, origin.y + _rng.Next(0, size.y));
            case 1: return new Vector2Int(origin.x - 1, origin.y + _rng.Next(0, size.y));
            case 2: return new Vector2Int(origin.x + _rng.Next(0, size.x), origin.y + size.y);
            default: return new Vector2Int(origin.x + _rng.Next(0, size.x), origin.y - 1);
        }
    }

    private bool IsAreaFree(Vector2Int origin, Vector2Int size)
    {
        for (int x = origin.x; x < origin.x + size.x; x++)
            for (int y = origin.y; y < origin.y + size.y; y++)
                if (_occupiedTiles.Contains(new Vector2Int(x, y))) return false;
        return true;
    }

    private void MarkArea(Vector2Int origin, Vector2Int size)
    {
        for (int x = origin.x; x < origin.x + size.x; x++)
            for (int y = origin.y; y < origin.y + size.y; y++)
                _occupiedTiles.Add(new Vector2Int(x, y));
    }

    // ---------- Flow control ----------
    /// <summary>
    /// Inicia a população a partir de um índice (ex: 0). Enfileira a geração se async configurado.
    /// </summary>
    public void StartProceduralFlow(int startIndex = 0)
    {
        if (_roomPlans == null || _roomPlans.Count == 0)
        {
            MapAllRoomsLayout();
            OnMapReady?.Invoke();
        }

        startIndex = Mathf.Clamp(startIndex, 0, _roomPlans.Count - 1);
        EnqueueRoomGeneration(startIndex);
    }

    public void EnqueueRoomGeneration(int index)
    {
        if (index < 0 || index >= _roomPlans.Count)
        {
            Debug.LogError("[GameFlowManager] Índice inválido para geração de sala.");
            return;
        }

        // avoid duplicates in queue
        if (!_generationQueue.Contains(index))
        {
            _generationQueue.Enqueue(index);
            if (verbose) Debug.Log($"[GameFlowManager] Sala {index} enfileirada para geração.");
        }

        if (useAsyncGeneration && !_isGenerating)
        {
            StartCoroutine(ProcessGenerationQueue());
        }
        else if (!_isGenerating) // support non-async mode
        {
            StartCoroutine(ProcessGenerationQueue());
        }
    }

    private IEnumerator ProcessGenerationQueue()
    {
        _isGenerating = true;
        while (_generationQueue.Count > 0)
        {
            int index = _generationQueue.Dequeue();
            if (index < 0 || index >= _roomPlans.Count) continue;

            var plan = _roomPlans[index];
            if (verbose) Debug.Log($"[GameFlowManager] Gerando sala {index} (origin {plan.originGrid})...");

            Transform container = null;
            Exception invokeException = null;

            // 1) invoke generator (wrapped in try - but NO yield here)
            try
            {
                InvokeBaseGeneratorForPlan(plan);
            }
            catch (Exception ex)
            {
                invokeException = ex;
                Debug.LogException(ex);
            }

            // 2) wait for container detection (this may yield) - executed outside try/catch to avoid CS1626
            if (invokeException == null)
            {
                float elapsed = 0f;
                while (elapsed < generationTimeout)
                {
                    container = FindGeneratedRoomContainer(plan);
                    if (container != null) break;
                    elapsed += 0.05f;
                    yield return new WaitForSecondsRealtime(0.05f);
                }
                if (container == null)
                {
                    Debug.LogError($"[GameFlowManager] Timeout ao localizar container para sala {index} (origin {plan.originGrid}).");
                }
            }

            if (container != null)
            {
                RegisterActiveRoom(plan, container);
                OnRoomLoaded?.Invoke(plan, container);
            }

            // Keep loop breathing
            if (useAsyncGeneration)
                yield return null;
        }

        _isGenerating = false;
    }


    // ---------- Registration / LRU ----------
    private void RegisterActiveRoom(RoomPlan plan, Transform container)
    {
        if (container == null) return;
        
        // If already registered, update usage and move to front
        if (_byContainer.TryGetValue(container, out var existing))
        {
            existing.usageTick = ++_usageCounter;
            existing.loadedAt = DateTime.UtcNow;

            // move its linked-list node to front (MRU)
            var node = _activeRooms.Find(existing);
            if (node != null)
            {
                _activeRooms.Remove(node);
                _activeRooms.AddFirst(node);
            }
            return;
        }
        
        // Register new room
        var ar = new ActiveRoom
        {
            plan = plan,
            container = container,
            index = plan.index,
            loadedAt = DateTime.UtcNow,
            usageTick = ++_usageCounter
        };

        // add to front (MRU)
        _activeRooms.AddFirst(ar);
        _byContainer[container] = ar;

        if (verbose) Debug.Log($"[GameFlowManager] Sala registrada: index={plan.index}, container={container.name}");

        // enforce limit
        EnforceActiveRoomLimit();
    }

    private void EnforceActiveRoomLimit()
    {
        while (_activeRooms.Count > maxActiveRooms)
        {
            var lruNode = _activeRooms.Last;
            if (lruNode == null) break;

            var lru = lruNode.Value;
            if (lru != null)
            {
                if (verbose) Debug.Log($"[GameFlowManager] Removendo sala LRU index={lru.index}");
                UnloadRoom(lru);
            }
            else
            {
                // Should not happen if list is consistent, but as a safeguard:
                _activeRooms.RemoveLast();
            }
        }
    }

    private void UnloadRoom(ActiveRoom toUnload)
    {
        if (toUnload == null) return;

        // try to call ClearRoom on baseRoomGenerator via reflection (prefer)
        bool cleared = TryClearRoomViaGenerator(toUnload.container);

        // fallback: destroy container safely (and fire event)
        if (!cleared)
        {
            if (toUnload.container != null)
            {
#if UNITY_EDITOR
                if (!Application.isPlaying)
                    UnityEngine.Object.DestroyImmediate(toUnload.container.gameObject);
                else
                    Destroy(toUnload.container.gameObject);
#else
                Destroy(toUnload.container.gameObject);
#endif
            }
        }

        // cleanup maps
        if (toUnload.container != null)
        {
            _byContainer.Remove(toUnload.container);
        }
        _activeRooms.Remove(toUnload);

        OnRoomUnloaded?.Invoke(toUnload.plan);
    }

    private bool TryClearRoomViaGenerator(Transform container)
    {
        if (baseRoomGenerator == null || container == null) return false;
        Type genType = baseRoomGenerator.GetType();

        // Try different signatures: ClearRoom(RoomInstance), ClearRoom(Transform), ClearRoom(GameObject)
        MethodInfo m = genType.GetMethod("ClearRoom", new Type[] { typeof(Transform) })
                       ?? genType.GetMethod("ClearRoom", new Type[] { typeof(GameObject) });
        if (m != null)
        {
            try
            {
                object arg = (m.GetParameters()[0].ParameterType == typeof(GameObject)) ? (object)container.gameObject : (object)container;
                m.Invoke(baseRoomGenerator, new object[] { arg });
                return true;
            }
            catch (Exception ex)
            {
                if (verbose) Debug.LogWarning($"[GameFlowManager] ClearRoom via generator falhou: {ex.Message}");
            }
        }
        return false;
    }

    // ---------- Generator invocation (robusto via reflection) ----------
    private void InvokeBaseGeneratorForPlan(RoomPlan plan)
    {
        if (baseRoomGenerator == null) throw new InvalidOperationException("baseRoomGenerator não atribuído.");

        Type genType = baseRoomGenerator.GetType();

        // Try known signatures in order of preference
        // 1) GenerateRoom(Vector2Int origin, Vector2Int size)
        var m_sig1 = genType.GetMethod("GenerateRoom", new Type[] { typeof(Vector2Int), typeof(Vector2Int), typeof(int), typeof(bool) });
        if (m_sig1 != null)
        {
            try
            {
                // signature may be (origin,size,height,gradual)
                int height = TryGetFieldOrPropInt(genType, baseRoomGenerator, "roomHeight", 3);
                bool gradual = TryGetFieldOrPropBool(genType, baseRoomGenerator, "generateGradually", true);
                m_sig1.Invoke(baseRoomGenerator, new object[] { plan.originGrid, plan.size, height, gradual });
                return;
            }
            catch { /* continue fallback */ }
        }

        // 2) GenerateRoom(Vector2Int origin, Vector2Int size)
        var m_sig2 = genType.GetMethod("GenerateRoom", new Type[] { typeof(Vector2Int), typeof(Vector2Int) });
        if (m_sig2 != null)
        {
            try
            {
                m_sig2.Invoke(baseRoomGenerator, new object[] { plan.originGrid, plan.size });
                return;
            }
            catch { /* continue fallback */ }
        }

        // 3) GenerateRoom(Vector2Int size) or GenerateRoom(Vector2Int origin)
        var m_sig3a = genType.GetMethod("GenerateRoom", new Type[] { typeof(Vector2Int) });
        if (m_sig3a != null)
        {
            try
            {
                // prefer calling with origin if name suggests so, otherwise size
                // We'll call with origin then size as fallback
                m_sig3a.Invoke(baseRoomGenerator, new object[] { plan.originGrid });
                return;
            }
            catch
            {
                try
                {
                    m_sig3a.Invoke(baseRoomGenerator, new object[] { plan.size });
                    return;
                }
                catch { }
            }
        }

        // 4) GenerateRoom() with fields set on generator (roomOriginGrid, roomSize, roomHeight)
        var m0 = genType.GetMethod("GenerateRoom", Type.EmptyTypes);
        if (m0 != null)
        {
            // set public fields/properties if available
            FieldOrPropSet(genType, baseRoomGenerator, "roomOriginGrid", plan.originGrid);
            FieldOrPropSet(genType, baseRoomGenerator, "roomSize", plan.size);
            FieldOrPropSet(genType, baseRoomGenerator, "roomHeight", TryGetFieldOrPropInt(genType, baseRoomGenerator, "roomHeight", 3));
            m0.Invoke(baseRoomGenerator, null);
            return;
        }

        // 5) As last resort, try calling a "GenerateRoom(RoomPlan)" if exists with matching type (unlikely)
        var rpType = typeof(RoomPlan);
        var m4 = genType.GetMethod("GenerateRoom", new Type[] { rpType });
        if (m4 != null)
        {
            m4.Invoke(baseRoomGenerator, new object[] { plan });
            return;
        }

        throw new MissingMethodException("Nenhum método GenerateRoom compatível encontrado no BaseRoomGenerator fornecido.");
    }

    private int TryGetFieldOrPropInt(Type t, object instance, string name, int defaultValue)
    {
        var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (f != null && f.FieldType == typeof(int)) return (int)f.GetValue(instance);
        var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (p != null && p.PropertyType == typeof(int)) return (int)p.GetValue(instance, null);
        return defaultValue;
    }

    private bool TryGetFieldOrPropBool(Type t, object instance, string name, bool defaultValue)
    {
        var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (f != null && f.FieldType == typeof(bool)) return (bool)f.GetValue(instance);
        var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (p != null && p.PropertyType == typeof(bool)) return (bool)p.GetValue(instance, null);
        return defaultValue;
    }

    private void FieldOrPropSet(Type type, object instance, string name, object value)
    {
        var f = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (f != null && f.FieldType.IsAssignableFrom(value.GetType()))
        {
            f.SetValue(instance, value);
            return;
        }
        var p = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (p != null && p.CanWrite && p.PropertyType.IsAssignableFrom(value.GetType()))
        {
            p.SetValue(instance, value, null);
            return;
        }
    }

    // ---------- Container discovery ----------
    // Heurística para localizar o container criado pelo generator para um dado plan.
    // Primeiro procura por padrão Room_{x}_{y} sob roomsRoot; depois busca globalmente.
    private Transform FindGeneratedRoomContainer(RoomPlan plan)
    {
        string namePrefix = $"Room_{plan.originGrid.x}_{plan.originGrid.y}";
        // procurar sob roomsRoot
        if (roomsRoot != null)
        {
            var child = roomsRoot.Find(namePrefix);
            if (child != null) return child;
            var all = roomsRoot.GetComponentsInChildren<Transform>(true);
            foreach (var t in all) if (t.name.StartsWith(namePrefix)) return t;
        }

        // busca global
        var global = GameObject.FindObjectsOfType<Transform>().FirstOrDefault(t => t.name.StartsWith("Room_") && t.name.Contains($"{plan.originGrid.x}_{plan.originGrid.y}"));
        if (global != null) return global;

        // fallback: retorna o último criado Room_*
        var allRooms = GameObject.FindObjectsOfType<Transform>().Where(t => t.name.StartsWith("Room_")).ToArray();
        if (allRooms.Length > 0)
        {
            // find one not already registered
            for (int i = allRooms.Length - 1; i >= 0; i--)
            {
                if (!_byContainer.ContainsKey(allRooms[i]))
                    return allRooms[i];
            }
        }

        return null;
    }

    // ---------- Player triggers / room enter ----------
    /// <summary>
    /// Deve ser chamado por um trigger quando o jogador entra na sala (ou você pode ligar a detecção automática via playerTransform).
    /// </summary>
    public void OnPlayerEnterRoom(Transform roomContainer)
    {
        if (roomContainer == null) return;
        if (!_byContainer.TryGetValue(roomContainer, out var active)) return;

        // mark usage for LRU by re-registering
        RegisterActiveRoom(active.plan, roomContainer);

        // Close doors if any via reflection
        TryCloseDoorForRoom(active);
    }

    private void TryCloseDoorForRoom(ActiveRoom room)
    {
        if (room == null || room.container == null) return;
        var comps = room.container.GetComponentsInChildren<MonoBehaviour>(true);
        foreach (var c in comps)
        {
            var t = c.GetType();
            var mClose = t.GetMethod("Close", BindingFlags.Public | BindingFlags.Instance);
            if (mClose != null && mClose.GetParameters().Length == 0)
            {
                try { mClose.Invoke(c, null); return; }
                catch { /* ignore */ }
            }
            var p = t.GetProperty("IsOpen", BindingFlags.Public | BindingFlags.Instance);
            if (p != null && p.CanWrite && p.PropertyType == typeof(bool))
            {
                try { p.SetValue(c, false, null); return; }
                catch { }
            }
        }
    }

    // ---------- Unload by distance ----------
    private void ManageUnloadByPlayerDistance()
    {
        if (playerTransform == null) return;

        var toUnload = new List<ActiveRoom>();
        // Iterate backwards to allow removal
        for (var node = _activeRooms.Last; node != null; node = node.Previous)
        {
            var r = node.Value;
            if (r.container == null) { toUnload.Add(r); continue; }
            float dist = Vector3.Distance(playerTransform.position, r.container.position);
            if (dist > unloadDistance)
            {
                toUnload.Add(r);
            }
        }


        foreach (var r in toUnload)
        {
            if (_activeRooms.Contains(r))
            {
                if (verbose) Debug.Log($"[GameFlowManager] Unloading room index={r.index} due to distance ({unloadDistance}).");
                UnloadRoom(r);
            }
        }
    }

    // ---------- Utilities ----------
    public IReadOnlyList<RoomPlan> GetAllPlans() => _roomPlans.AsReadOnly();

    public int GetActiveRoomCount() => _activeRooms.Count;

    // Request to proceed to next planned room (helper)
    public void ProceedToNext()
    {
        if (_roomPlans.Count == 0) return;
        int nextIndex = 0;
        if (_activeRooms.Count > 0) nextIndex = _activeRooms.First.Value.index + 1;
        EnqueueRoomGeneration(nextIndex);
    }

    // ---------- Debug helpers ----------
    private void LogV(string msg)
    {
        if (verbose) Debug.Log($"[GameFlowManager] {msg}");
    }
}