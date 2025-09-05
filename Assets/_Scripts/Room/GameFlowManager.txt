// GameFlowManager.cs
// Gerenciador do fluxo/streaming de salas.
// PR-01: usa exclusivamente RoomsData.RoomPlan como contrato (remove duplicidade de tipos).
// PR-02: a porta de SAÍDA da sala atual só abre DEPOIS que a próxima sala estiver pronta.
//        -> Centraliza a emissão em GameFlowManager logo após registrar a próxima sala.
//        -> Mantém o RoomTriggerController apenas como listener do sinal (sem decidir abertura).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

// Alias para consumir o contrato único de RoomsData
using RoomPlan = RoomsData.RoomPlan;
using RoomInstance = RoomsData.RoomInstance;

[DisallowMultipleComponent]
public class GameFlowManager : MonoBehaviour
{
    [Header("Referências")]
    [Tooltip("Componente que gera salas (ex.: BaseRoomGenerator, BedroomGenerator, etc.).")]
    public MonoBehaviour baseRoomGenerator;

    [Tooltip("Parent para todos os containers de sala (opcional). Será criado se nulo.")]
    public Transform roomsRoot;

    [Header("Mapeamento")]
    [Tooltip("Quantas salas tentar mapear.")]
    public int mapRoomCount = 100;

    [Tooltip("Seed; 0 = usa hora atual (não determinístico).")]
    public int seed = 0;

    [Tooltip("Tentativas por sala para encontrar posição livre.")]
    public int maxPlacementAttemptsPerRoom = 60;

    [Tooltip("Tamanhos min/max para salas (em voxels).")]
    public Vector2Int roomSizeMin = new Vector2Int(4, 4);
    public Vector2Int roomSizeMax = new Vector2Int(12, 12);

    [Header("Streaming / Ativação")]
    [Tooltip("Máximo de salas ativas simultaneamente (LRU).")]
    [Min(1)] public int maxActiveRooms = 6;

    [Tooltip("Se true, a geração roda em fila assíncrona.")]
    public bool useAsyncGeneration = true;

    [Tooltip("Timeout (s) para localizar o container após invocar o gerador.")]
    public float generationTimeout = 12f;

    [Header("Player / Flow")]
    [Tooltip("Transform do jogador para descarregar salas por distância (opcional).")]
    public Transform playerTransform;

    [Tooltip("Distância para descarregar salas fora de uso.")]
    public float unloadDistance = 32f;

    [Header("Tuning / Debug")]
    public bool verbose = false;

    // -------------------- Estado / tipos auxiliares --------------------
    private System.Random _rng;

    // Layout planejado do mapa (contrato único RoomsData.RoomPlan)
    private readonly List<RoomPlan> _roomPlans = new List<RoomPlan>();

    // Tiles ocupados do grid de planejamento
    private readonly HashSet<Vector2Int> _occupiedTiles = new HashSet<Vector2Int>();

    // Sala ativa com dados para LRU
    private sealed class ActiveRoom
    {
        public RoomPlan plan;
        public Transform container;
        public int index;              // mantido por compat (usa plan.id)
        public DateTime loadedAt;
        public int usageTick;          // para LRU
    }

    // MRU na frente
    private readonly LinkedList<ActiveRoom> _activeRooms = new LinkedList<ActiveRoom>();

    // Índice rápido por container e por índice
    private readonly Dictionary<Transform, ActiveRoom> _byContainer = new Dictionary<Transform, ActiveRoom>();
    private readonly Dictionary<int, ActiveRoom> _byIndex = new Dictionary<int, ActiveRoom>();

    // Fila de índices de salas a gerar
    private readonly Queue<int> _generationQueue = new Queue<int>();

    private bool _isGenerating = false;
    private int _usageCounter = 0;

    // -------------------- PR-02: coordenação de abertura de SAÍDA --------------------
    // Quando o jogador entra na sala i (lockdown), aguardamos construir a sala i+1.
    // Assim que i+1 for registrada, emitimos "RoomShouldOpenExit" para i.
    private int? _pendingExitOpenForIndex = null;
    private readonly HashSet<int> _exitOpenedOnce = new HashSet<int>();

    // -------------------- Eventos públicos (mantém assinaturas usadas no código) --------------------
    public event Action OnMapReady;
    public event Action<RoomPlan, Transform> OnRoomLoaded;
    public event Action<RoomPlan> OnRoomUnloaded;

    // -------------------- Unity --------------------
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

        if (verbose) Debug.Log($"{Ts()} [GameFlowManager] Awake → layout mapeado com {_roomPlans.Count} salas (seed={seed}).");
    }

    private void Update()
    {
        if (playerTransform != null) ManageUnloadByPlayerDistance();
    }

    // -------------------- Mapeamento / layout --------------------
    public void MapAllRoomsLayout()
    {
        _roomPlans.Clear();
        _occupiedTiles.Clear();

        Vector2Int cursor = Vector2Int.zero;

        // Heurística opcional: iniciar próximo do player, se existir
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

                    // PR-01: construir RoomsData.RoomPlan (id, gridOrigin, size, height, doors, generator, seed)
                    var plan = new RoomPlan(
                        id: i,
                        gridOrigin: origin,
                        size: size,
                        height: 3, // altura padrão; geradores especializados podem sobrescrever
                        entry: default,
                        exit: default,
                        generatorIndex: 0,
                        randomSeed: seed
                    );

                    _roomPlans.Add(plan);

                    // avança cursor heurístico para borda aleatória da sala atual
                    cursor = ChooseRandomEdgeGrid(origin, size);
                    placed = true;
                    break;
                }
            }

            if (!placed)
            {
                if (verbose) Debug.LogWarning($"{Ts()} [GameFlowManager] Não foi possível posicionar a sala #{i} após {maxPlacementAttemptsPerRoom} tentativas.");
                break;
            }
        }

        if (verbose) Debug.Log($"{Ts()} [GameFlowManager] Mapeamento completo: {_roomPlans.Count} salas (seed={seed}).");
    }

    private Vector2Int RandomRoomSize()
    {
        int w = _rng.Next(roomSizeMin.x, roomSizeMax.x + 1);
        int d = _rng.Next(roomSizeMin.y, roomSizeMax.y + 1);
        return new Vector2Int(w, d);
    }

    private Vector2Int FindOriginForIndex(int index, Vector2Int cursor, Vector2Int size)
    {
        var dirs = new List<Vector2Int> {
            new Vector2Int(1, 0), new Vector2Int(-1, 0),
            new Vector2Int(0, 1), new Vector2Int(0, -1)
        };

        var dir = dirs[_rng.Next(0, dirs.Count)];
        Vector2Int offset = (dir.x != 0)
            ? new Vector2Int((dir.x > 0) ? size.x : -size.x, 0)
            : new Vector2Int(0, (dir.y > 0) ? size.y : -size.y);

        Vector2Int origin = cursor + offset + new Vector2Int(_rng.Next(-2, 3), _rng.Next(-2, 3));
        return origin;
    }

    private Vector2Int ChooseRandomEdgeGrid(Vector2Int origin, Vector2Int size)
    {
        int side = _rng.Next(0, 4);
        switch (side)
        {
            case 0: return new Vector2Int(origin.x + size.x, origin.y + _rng.Next(0, size.y));
            case 1: return new Vector2Int(origin.x - 1,       origin.y + _rng.Next(0, size.y));
            case 2: return new Vector2Int(origin.x + _rng.Next(0, size.x), origin.y + size.y);
            default:return new Vector2Int(origin.x + _rng.Next(0, size.x), origin.y - 1);
        }
    }

    private bool IsAreaFree(Vector2Int origin, Vector2Int size)
    {
        for (int x = origin.x; x < origin.x + size.x; x++)
        for (int y = origin.y; y < origin.y + size.y; y++)
            if (_occupiedTiles.Contains(new Vector2Int(x, y)))
                return false;
        return true;
    }

    private void MarkArea(Vector2Int origin, Vector2Int size)
    {
        for (int x = origin.x; x < origin.x + size.x; x++)
        for (int y = origin.y; y < origin.y + size.y; y++)
            _occupiedTiles.Add(new Vector2Int(x, y));
    }

    // -------------------- Controle de fluxo --------------------
    /// Inicia o fluxo procedural a partir de um índice (ex.: 0).
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

        // evita duplicatas na fila
        if (!_generationQueue.Contains(index))
        {
            _generationQueue.Enqueue(index);
            if (verbose) Debug.Log($"{Ts()} [GameFlowManager] Sala {index} enfileirada para geração.");
        }

        if (!_isGenerating) StartCoroutine(ProcessGenerationQueue());
    }

    private IEnumerator ProcessGenerationQueue()
    {
        _isGenerating = true;

        while (_generationQueue.Count > 0)
        {
            int index = _generationQueue.Dequeue();
            if (index < 0 || index >= _roomPlans.Count) continue;

            var plan = _roomPlans[index];

            if (verbose) Debug.Log($"{Ts()} [GameFlowManager] Gerando sala {index} (origin {plan.gridOrigin})...");

            Transform container = null;
            Exception invokeException = null;

            // 1) Invoca o gerador (sem yield dentro do try)
            try
            {
                InvokeBaseGeneratorForPlan(plan);
            }
            catch (Exception ex)
            {
                invokeException = ex;
                Debug.LogException(ex);
            }

            // 2) Procura o container gerado (pode fazer yield) — fora do try
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
                    Debug.LogError($"{Ts()} [GameFlowManager] Timeout ao localizar container para sala {index} (origin {plan.gridOrigin}).");
            }

            if (container != null)
            {
                RegisterActiveRoom(plan, container);
                OnRoomLoaded?.Invoke(plan, container);

                // -------------------- PR-02 (núcleo) --------------------
                // Se acabamos de registrar a sala (index) e existe um "pending" para (index-1),
                // emitimos a abertura da SAÍDA para a sala anterior.
                if (_pendingExitOpenForIndex.HasValue &&
                    _pendingExitOpenForIndex.Value == (index - 1) &&
                    !_exitOpenedOnce.Contains(_pendingExitOpenForIndex.Value))
                {
                    int prevIndex = _pendingExitOpenForIndex.Value;
                    if (_byIndex.TryGetValue(prevIndex, out var prevActive) && prevActive?.container)
                    {
                        // Monta uma RoomInstance mínima para o sinal (listeners checam root).
                        var inst = new RoomInstance
                        {
                            plan = prevActive.plan,
                            root = prevActive.container
                        };

                        if (verbose) Debug.Log($"{Ts()} [GameFlowManager] Próxima sala #{index} pronta → EmitRoomShouldOpenExit para sala #{prevIndex}.");
                        GameSignals.EmitRoomShouldOpenExit(inst);

                        _exitOpenedOnce.Add(prevIndex);
                    }

                    // Limpa pendência após emitir
                    _pendingExitOpenForIndex = null;
                }
                // ---------------------------------------------------------
            }

            if (useAsyncGeneration)
                yield return null;
        }

        _isGenerating = false;
    }

    // -------------------- Registro / LRU --------------------
    private void RegisterActiveRoom(RoomPlan plan, Transform container)
    {
        if (container == null) return;

        // Já registrada? Atualiza uso e move para frente (MRU)
        if (_byContainer.TryGetValue(container, out var existing))
        {
            existing.usageTick = ++_usageCounter;
            existing.loadedAt = DateTime.UtcNow;
            var node = _activeRooms.Find(existing);
            if (node != null)
            {
                _activeRooms.Remove(node);
                _activeRooms.AddFirst(node);
            }
            _byIndex[existing.index] = existing;
            return;
        }

        // Nova sala
        var ar = new ActiveRoom
        {
            plan = plan,
            container = container,
            index = plan.id,
            loadedAt = DateTime.UtcNow,
            usageTick = ++_usageCounter
        };

        _activeRooms.AddFirst(ar);
        _byContainer[container] = ar;
        _byIndex[ar.index] = ar;

        if (verbose) Debug.Log($"{Ts()} [GameFlowManager] Sala registrada: id={plan.id}, container={container.name}");

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
                if (verbose) Debug.Log($"{Ts()} [GameFlowManager] Removendo sala LRU id={lru.index}");
                UnloadRoom(lru);
            }
            else
            {
                _activeRooms.RemoveLast();
            }
        }
    }

    private void UnloadRoom(ActiveRoom toUnload)
    {
        if (toUnload == null) return;

        // Preferencial: pedir para o gerador limpar (ClearRoom)
        bool cleared = TryClearRoomViaGenerator(toUnload.container);

        // Fallback: destruir o container
        if (!cleared && toUnload.container != null)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying) DestroyImmediate(toUnload.container.gameObject);
            else Destroy(toUnload.container.gameObject);
#else
            Destroy(toUnload.container.gameObject);
#endif
        }

        if (toUnload.container != null) _byContainer.Remove(toUnload.container);
        _byIndex.Remove(toUnload.index);

        _activeRooms.Remove(toUnload);

        OnRoomUnloaded?.Invoke(toUnload.plan);
    }

    private bool TryClearRoomViaGenerator(Transform container)
    {
        if (baseRoomGenerator == null || container == null) return false;

        Type genType = baseRoomGenerator.GetType();

        // Tenta assinaturas conhecidas na ordem: 1) ClearRoom(Transform) 2) ClearRoom(GameObject)
        MethodInfo m =
            genType.GetMethod("ClearRoom", new[] { typeof(Transform) }) ??
            genType.GetMethod("ClearRoom", new[] { typeof(GameObject) });

        if (m != null)
        {
            try
            {
                object arg = (m.GetParameters()[0].ParameterType == typeof(GameObject))
                    ? (object)container.gameObject
                    : (object)container;

                m.Invoke(baseRoomGenerator, new object[] { arg });
                return true;
            }
            catch (Exception ex)
            {
                if (verbose) Debug.LogWarning($"{Ts()} [GameFlowManager] ClearRoom via generator falhou: {ex.Message}");
            }
        }

        return false;
    }

    // -------------------- Invocação do gerador (reflexão robusta) --------------------
    private void InvokeBaseGeneratorForPlan(RoomPlan plan)
    {
        if (baseRoomGenerator == null) throw new InvalidOperationException("baseRoomGenerator não atribuído.");

        Type genType = baseRoomGenerator.GetType();

        // 1) GenerateRoom(Vector2Int origin, Vector2Int size, int height, bool gradual)
        var m_sig1 = genType.GetMethod("GenerateRoom", new[] { typeof(Vector2Int), typeof(Vector2Int), typeof(int), typeof(bool) });
        if (m_sig1 != null)
        {
            try
            {
                int height = TryGetFieldOrPropInt(genType, baseRoomGenerator, "roomHeight", 3);
                bool gradual = TryGetFieldOrPropBool(genType, baseRoomGenerator, "generateGradually", true);
                m_sig1.Invoke(baseRoomGenerator, new object[] { plan.gridOrigin, plan.size, height, gradual });
                return;
            }
            catch { /* fallback */ }
        }

        // 2) GenerateRoom(Vector2Int origin, Vector2Int size)
        var m_sig2 = genType.GetMethod("GenerateRoom", new[] { typeof(Vector2Int), typeof(Vector2Int) });
        if (m_sig2 != null)
        {
            try
            {
                m_sig2.Invoke(baseRoomGenerator, new object[] { plan.gridOrigin, plan.size });
                return;
            }
            catch { /* fallback */ }
        }

        // 3) GenerateRoom(Vector2Int v) — tentar com origin, depois size
        var m_sig3a = genType.GetMethod("GenerateRoom", new[] { typeof(Vector2Int) });
        if (m_sig3a != null)
        {
            try { m_sig3a.Invoke(baseRoomGenerator, new object[] { plan.gridOrigin }); return; }
            catch
            {
                try { m_sig3a.Invoke(baseRoomGenerator, new object[] { plan.size }); return; }
                catch { /* fallback */ }
            }
        }

        // 4) GenerateRoom() com campos públicos setados
        var m0 = genType.GetMethod("GenerateRoom", Type.EmptyTypes);
        if (m0 != null)
        {
            FieldOrPropSet(genType, baseRoomGenerator, "roomOriginGrid", plan.gridOrigin);
            FieldOrPropSet(genType, baseRoomGenerator, "roomSize",       plan.size);
            FieldOrPropSet(genType, baseRoomGenerator, "roomHeight",     TryGetFieldOrPropInt(genType, baseRoomGenerator, "roomHeight", 3));
            m0.Invoke(baseRoomGenerator, null);
            return;
        }

        // 5) (raro) GenerateRoom(RoomPlan)
        var rpType = typeof(RoomPlan);
        var m4 = genType.GetMethod("GenerateRoom", new[] { rpType });
        if (m4 != null)
        {
            m4.Invoke(baseRoomGenerator, new object[] { plan });
            return;
        }

        throw new MissingMethodException("Nenhum método GenerateRoom compatível encontrado no gerador informado.");
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

    // -------------------- Descoberta do container --------------------
    // Heurística: procura por "Room_{x}_{y}" primeiro sob roomsRoot, depois globalmente.
    private Transform FindGeneratedRoomContainer(RoomPlan plan)
    {
        string namePrefix = $"Room_{plan.gridOrigin.x}_{plan.gridOrigin.y}";

        // procurar sob roomsRoot
        if (roomsRoot != null)
        {
            var direct = roomsRoot.Find(namePrefix);
            if (direct != null) return direct;

            var all = roomsRoot.GetComponentsInChildren<Transform>(true);
            foreach (var t in all)
                if (t.name.StartsWith(namePrefix, StringComparison.Ordinal))
                    return t;
        }

        // busca global
        var global = GameObject.FindObjectsOfType<Transform>()
            .FirstOrDefault(t => t.name.StartsWith("Room_", StringComparison.Ordinal) &&
                                 t.name.Contains($"{plan.gridOrigin.x}_{plan.gridOrigin.y}"));
        if (global != null) return global;

        // fallback: último Room_* não registrado
        var allRooms = GameObject.FindObjectsOfType<Transform>()
            .Where(t => t.name.StartsWith("Room_", StringComparison.Ordinal))
            .ToArray();

        for (int i = allRooms.Length - 1; i >= 0; i--)
            if (!_byContainer.ContainsKey(allRooms[i]))
                return allRooms[i];

        return null;
    }

    // -------------------- Triggers do jogador --------------------
    /// Chame quando o jogador entrar no container da sala (fallback genérico).
    public void OnPlayerEnterRoom(Transform roomContainer)
    {
        if (roomContainer == null) return;
        if (!_byContainer.TryGetValue(roomContainer, out var active)) return;

        // Marca uso (MRU) ao re-registrar
        RegisterActiveRoom(active.plan, roomContainer);

        // Fechar porta de ENTRADA (se existir) via reflexão simples (Close() ou IsOpen=false)
        TryCloseDoorForRoom(active);
    }

    /// Handler recomendado pelo RoomTriggerController (via UnityEvent/SendMessageUpwards).
    /// Aqui executamos a lógica PR-02: pedir a próxima sala e marcar pendência para abrir a SAÍDA
    /// apenas quando a próxima sala terminar de ser construída/registrada.
    public void OnRoomLockdownRequested(int roomIndex)
    {
        if (roomIndex < 0 || roomIndex >= _roomPlans.Count) return;

        if (verbose) Debug.Log($"{Ts()} [GameFlowManager] LOCKDOWN recebido para sala #{roomIndex} → preparando próxima sala e aguardando confirmação de build.");

        // Enfileira a próxima sala
        int nextIndex = roomIndex + 1;
        if (nextIndex < _roomPlans.Count)
        {
            EnqueueRoomGeneration(nextIndex);
            _pendingExitOpenForIndex = roomIndex; // aguardaremos o registro de nextIndex para então abrir a saída de roomIndex
        }
        else
        {
            if (verbose) Debug.Log($"{Ts()} [GameFlowManager] Sala #{roomIndex} é a última planejada — nenhuma próxima sala para construir.");
        }
    }

    private void TryCloseDoorForRoom(ActiveRoom room)
    {
        if (room == null || room.container == null) return;

        var comps = room.container.GetComponentsInChildren<Component>(true);
        foreach (var c in comps)
        {
            if (c == null) continue;
            var t = c.GetType();

            var mClose = t.GetMethod("Close", BindingFlags.Public | BindingFlags.Instance);
            if (mClose != null && mClose.GetParameters().Length == 0)
            {
                try { mClose.Invoke(c, null); return; } catch { }
            }

            var pIsOpen = t.GetProperty("IsOpen", BindingFlags.Public | BindingFlags.Instance);
            if (pIsOpen != null && pIsOpen.CanWrite && pIsOpen.PropertyType == typeof(bool))
            {
                try { pIsOpen.SetValue(c, false, null); return; } catch { }
            }
        }
    }

    // -------------------- Descarregamento por distância --------------------
    private void ManageUnloadByPlayerDistance()
    {
        if (playerTransform == null) return;

        var toUnload = new List<ActiveRoom>();

        for (var node = _activeRooms.Last; node != null; node = node.Previous)
        {
            var r = node.Value;
            if (r.container == null)
            {
                toUnload.Add(r);
                continue;
            }

            float dist = Vector3.Distance(playerTransform.position, r.container.position);
            if (dist > unloadDistance) toUnload.Add(r);
        }

        foreach (var r in toUnload)
        {
            if (_activeRooms.Contains(r))
            {
                if (verbose) Debug.Log($"{Ts()} [GameFlowManager] Unloading room id={r.index} (dist>{unloadDistance}).");
                UnloadRoom(r);
            }
        }
    }

    // -------------------- Utilidades --------------------
    public IReadOnlyList<RoomPlan> GetAllPlans() => _roomPlans.AsReadOnly();
    public int GetActiveRoomCount() => _activeRooms.Count;

    public void ProceedToNext()
    {
        if (_roomPlans.Count == 0) return;

        int nextIndex = 0;
        if (_activeRooms.Count > 0) nextIndex = _activeRooms.First.Value.index + 1;

        EnqueueRoomGeneration(nextIndex);
    }

    private static string Ts() => DateTime.UtcNow.ToString("HH:mm:ss.fff");
}
