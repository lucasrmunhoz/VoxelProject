// GameFlowManager.cs
// Gerenciador de fluxo de jogo / streaming procedural de salas.
// ATUALIZADO: Gerencia uma lista de geradores especialistas (ex: BedroomGenerator) encontrados em seus filhos.
// NOTA: Nenhuma alteração foi necessária neste script. Seu gerador de números aleatórios interno (_rng) 
// é inicializado e usado de forma segura dentro de seu próprio ciclo de vida, não causando a race condition
// que foi corrigida nos scripts BaseRoomGenerator e BedroomGenerator.
// ALTERADO: Adicionada lógica para impedir o descarregamento de salas por distância antes do jogador entrar na primeira sala procedural.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Profiling;

#if UNITY_EDITOR
using UnityEditor;
#endif

[DisallowMultipleComponent]
public class GameFlowManager : MonoBehaviour
{
    // --- SUGESTÃO DE ALTERAÇÃO (Início) ---
    [Header("Generator Management")]
    [Tooltip("Lista de geradores especialistas (ex: BedroomGenerator) que o GameFlow pode usar. Preenchido automaticamente a partir dos filhos se vazio.")]
    public List<BaseRoomGenerator> specialistGenerators;
    // Cache interno para geradores encontrados nos filhos.
    private List<BaseRoomGenerator> _foundGenerators = new List<BaseRoomGenerator>();
    // --- SUGESTÃO DE ALTERAÇÃO (Fim) ---

    [Header("References")]
    [Tooltip("Parent para todas as salas geradas (facilita busca e organização).")]
    public Transform roomsRoot;

    [Header("Map")]
    public int mapRoomCount = 32;
    public int maxPlacementAttemptsPerRoom = 48;
    public int maxActiveRooms = 8;
    public int startIndex = 0;
    public int seed = 0;

    [Header("Generation")]
    public bool useAsyncGeneration = true;
    public int maxVoxelsPerFrame = 1024;
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
        public int index;
        public Vector2Int originGrid;
        public Vector2Int size;
        // Futuramente, poderia ter um RoomType para escolher o gerador correto.
        // public RoomType roomType; 
    }

    private class ActiveRoom
    {
        public RoomPlan plan;
        public Transform container;
        public DateTime loadedAt;
        public long usageTick;
        public int index => plan.index;
    }

    // ---------- Fields ----------
    private List<RoomPlan> _roomPlans = new List<RoomPlan>();
    private HashSet<Vector2Int> _occupiedTiles = new HashSet<Vector2Int>();
    private LinkedList<ActiveRoom> _activeRooms = new LinkedList<ActiveRoom>();
    private Dictionary<Transform, ActiveRoom> _byContainer = new Dictionary<Transform, ActiveRoom>();
    private Queue<int> _generationQueue = new Queue<int>();
    private bool _isGenerating = false;
    private int _usageCounter = 0;
    private ActiveRoom _currentActiveRoom = null;
    private System.Random _rng;

    // =======================================================================
    // INÍCIO DO BLOCO ADICIONADO
    // =======================================================================
    // Trava para a lógica de descarregamento por distância.
    private bool _proceduralFlowHasStarted = false;
    // =======================================================================
    // FIM DO BLOCO ADICIONADO
    // =======================================================================

    // events
    public event Action OnMapReady;
    public event Action<RoomPlan, Transform> OnRoomLoaded;
    public event Action<RoomPlan, Transform> OnRoomUnloaded;

    // ---------- Unity lifecycle ----------
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

        // --- SUGESTÃO DE ALTERAÇÃO (Início) ---
        // Popula a lista de geradores.
        if (specialistGenerators == null || specialistGenerators.Count == 0)
        {
            // Busca por todos os componentes que herdam de BaseRoomGenerator nos filhos deste objeto.
            GetComponentsInChildren<BaseRoomGenerator>(true, _foundGenerators);
            specialistGenerators = _foundGenerators;
        }

        if (specialistGenerators.Count == 0)
        {
            Debug.LogError("[GameFlowManager] Nenhum gerador especialista (ex: BedroomGenerator) foi encontrado nos filhos ou atribuído no Inspector!", this);
            return; // Impede a execução se nenhum gerador for encontrado.
        }
        // --- SUGESTÃO DE ALTERAÇÃO (Fim) ---

        // Se inscreve nos eventos de cada gerador encontrado.
        foreach (var generator in specialistGenerators)
        {
            // Opcional: Garante que o gerador use uma raiz de salas comum se não tiver uma própria.
            var genRoot = generator.transform.Find("GeneratedRooms");
            if (genRoot != null && roomsRoot == null) roomsRoot = genRoot;

            // Quando o gerador concluir a população de uma sala, registra imediatamente.
            generator.OnRoomPopulated += (BaseRoomGenerator.RoomInstance room) =>
            {
                try
                {
                    int idx = _roomPlans.FindIndex(p => p.originGrid == room.originGrid && p.size == room.size);
                    if (idx >= 0)
                    {
                        var plan = _roomPlans[idx];
                        RegisterActiveRoom(plan, room.container);
                        OnRoomLoaded?.Invoke(plan, room.container);
                    }
                }
                catch (Exception ex) { Debug.LogException(ex); }
            };
        }

        MapAllRoomsLayout();
        OnMapReady?.Invoke();

        startIndex = Mathf.Clamp(startIndex, 0, _roomPlans.Count - 1);
        EnqueueRoomGeneration(startIndex);
    }

    private void Start()
    {
        if (_roomPlans.Count > 0 && !_isGenerating)
            EnqueueRoomGeneration(startIndex);
    }

    private void Update()
    {
        if (_currentActiveRoom != null)
        {
            _currentActiveRoom.usageTick = ++_usageCounter;
        }

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

        if (specialistGenerators == null || specialistGenerators.Count == 0)
        {
            Debug.LogError("[GameFlowManager] Nenhum gerador especialista disponível! Mapeamento cancelado.");
            return;
        }

        // --- SUGESTÃO DE ALTERAÇÃO (INÍCIO) ---
        Vector2Int cursor = Vector2Int.zero;
        // Tenta encontrar o gerador que tem o Hub inicial
        var hubGenerator = specialistGenerators.FirstOrDefault(g => g.initialHubRoom != null);
        if (hubGenerator != null && hubGenerator.initialHubRoom != null)
        {
            // Encontra o ExitAnchor dentro do Hub
            var exitAnchor = hubGenerator.initialHubRoom.Find("ExitAnchor");
            if (exitAnchor != null)
            {
                // Usa a posição do ExitAnchor como ponto de partida para o mapa
                var anchorPos = exitAnchor.position;
                var voxelSize = hubGenerator.voxelSize > 0 ? hubGenerator.voxelSize : 1.0f;
                cursor = new Vector2Int(Mathf.RoundToInt(anchorPos.x / voxelSize), Mathf.RoundToInt(anchorPos.z / voxelSize));

                if(verbose) Debug.Log($"[GameFlowManager] Ponto de partida do mapa definido pelo ExitAnchor do Hub em {cursor}");
            }
        }
        else if (playerTransform != null)
        {
            // Fallback para a posição do jogador se o Hub não for encontrado
            var wp = playerTransform.position;
            cursor = new Vector2Int(Mathf.RoundToInt(wp.x), Mathf.RoundToInt(wp.z));
        }
        // --- SUGESTÃO DE ALTERAÇÃO (FIM) ---

        for (int i = 0; i < mapRoomCount; i++)
        {
            bool placed = false;
            for (int attempt = 0; attempt < maxPlacementAttemptsPerRoom; attempt++)
            {
                // --- SUGESTÃO DE ALTERAÇÃO (Início) ---
                // Escolhe um gerador aleatório da lista para definir o tamanho da sala.
                var randomGenerator = specialistGenerators[_rng.Next(0, specialistGenerators.Count)];
                Vector2Int size = randomGenerator.GetRandomSize(_rng);
                // --- SUGESTÃO DE ALTERAÇÃO (Fim) ---

                Vector2Int origin = FindOriginForIndex(i, cursor, size);
                if (IsAreaFree(origin, size))
                {
                    var rp = new RoomPlan { index = i, originGrid = origin, size = size };
                    _roomPlans.Add(rp);
                    MarkArea(origin, size);
                    placed = true;
                    break;
                }
            }
            if (!placed)
            {
                // fallback: coloca em cursor deslocado
                var randomGenerator = specialistGenerators[_rng.Next(0, specialistGenerators.Count)];
                var fallback = new RoomPlan { index = i, originGrid = cursor + new Vector2Int(i * 2, 0), size = randomGenerator.GetRandomSize(_rng) };
                _roomPlans.Add(fallback);
                MarkArea(fallback.originGrid, fallback.size);
            }
        }
    }

    private Vector2Int FindOriginForIndex(int index, Vector2Int cursor, Vector2Int size)
    {
        int ring = index / 8;
        int offset = index % 8;
        return cursor + new Vector2Int(ring * (size.x + 1), offset * (size.y + 1));
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

    // ---------- Generation queue ----------
    public void EnqueueRoomGeneration(int index)
    {
        if (index < 0 || index >= _roomPlans.Count)
        {
            if (verbose) Debug.LogWarning($"[GameFlowManager] Índice inválido para geração de sala: {index}.");
            return;
        }
        
        if (!_generationQueue.Contains(index))
            _generationQueue.Enqueue(index);

        if (!_isGenerating)
            StartCoroutine(ProcessGenerationQueue());
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
            
            try { InvokeBaseGeneratorForPlan(plan); }
            catch (Exception ex) { invokeException = ex; Debug.LogException(ex); }
            
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
                    Debug.LogError($"[GameFlowManager] Timeout ao tentar localizar container para sala {index} (origin {plan.originGrid}).");
            }

            if (container != null)
            {
                RegisterActiveRoom(plan, container);
                OnRoomLoaded?.Invoke(plan, container);
            }
            
            if (useAsyncGeneration)
                yield return null;
        }
        _isGenerating = false;
    }

    private void InvokeBaseGeneratorForPlan(RoomPlan plan)
    {
        // --- SUGESTÃO DE ALTERAÇÃO (Início) ---
        // TODO: Adicionar lógica para escolher um gerador específico com base no
        // tipo de sala (plan.roomType, por exemplo). Por enquanto, escolhemos um aleatoriamente.
        var generatorToUse = specialistGenerators[_rng.Next(0, specialistGenerators.Count)];

        // O resto do método usa a variável 'generatorToUse' em vez de uma referência única.
        var method = generatorToUse.GetType().GetMethod("GenerateRoom", new Type[] { typeof(Vector2Int), typeof(Vector2Int) });
        if (method != null)
        {
            method.Invoke(generatorToUse, new object[] { plan.originGrid, plan.size });
            return;
        }

        method = generatorToUse.GetType().GetMethod("GenerateRoom", new Type[] { });
        if (method != null)
        {
            method.Invoke(generatorToUse, null);
            return;
        }

        // fallback genérico (vai chamar a versão virtual do BaseRoomGenerator)
        try
        {
            generatorToUse.GenerateRoom(plan.originGrid, plan.size);
            return;
        }
        catch
        {
            // se não der, tentamos reflection genérico lançado (mantive o throw abaixo)
        }
        // --- SUGESTÃO DE ALTERAÇÃO (Fim) ---
        throw new MissingMethodException($"Nenhum método GenerateRoom compatível encontrado em {generatorToUse.GetType().Name}.");
    }

    // ---------- Registration ----------
    private void RegisterActiveRoom(RoomPlan plan, Transform container)
    {
        if (container == null) return;
        
        if (_byContainer.TryGetValue(container, out var existing))
        {
            existing.usageTick = ++_usageCounter;
            existing.loadedAt = DateTime.UtcNow;
            var node = _activeRooms.Find(existing);
            if (node != null) { _activeRooms.Remove(node); _activeRooms.AddFirst(node); }
            return;
        }
        
        var ar = new ActiveRoom { plan = plan, container = container, loadedAt = DateTime.UtcNow, usageTick = ++_usageCounter };
        _activeRooms.AddFirst(ar);
        _byContainer[container] = ar;
        if (verbose) Debug.Log($"[GameFlowManager] Sala registrada: index={plan.index}, container={container.name}");
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
                if (lru == _currentActiveRoom) break;
                if (verbose) Debug.Log($"[GameFlowManager] Removendo sala LRU index={lru.index}");
                UnloadRoom(lru);
            }
            else { _activeRooms.RemoveLast(); }
        }
    }

    private void UnloadRoom(ActiveRoom ar)
    {
        if (ar == null) return;
        if (ar.container != null)
        {
            try
            {
                // Tenta chamar ClearRoom no primeiro gerador disponível (pode ser melhorado no futuro)
                var generator = specialistGenerators.FirstOrDefault();
                var mi = generator?.GetType().GetMethod("ClearRoom", new Type[] { typeof(Transform) });
                if (mi != null) mi.Invoke(generator, new object[] { ar.container });
                else
                {
                    GameObject.Destroy(ar.container.gameObject);
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        if (ar.container != null && _byContainer.ContainsKey(ar.container)) _byContainer.Remove(ar.container);
        _activeRooms.Remove(ar);
        OnRoomUnloaded?.Invoke(ar.plan, ar.container);
    }

    // ---------- Player triggers / room enter ----------
    public void OnPlayerEnterRoom(Transform roomContainer)
    {
        if (roomContainer == null) return;
        if (!_byContainer.TryGetValue(roomContainer, out var enteredRoom)) return;

        // =======================================================================
        // INÍCIO DO BLOCO ALTERADO
        // =======================================================================
        // Ativa a lógica de "procedural flow" e o descarregamento por distância.
        _proceduralFlowHasStarted = true;
        // =======================================================================
        // FIM DO BLOCO ALTERADO
        // =======================================================================

        _currentActiveRoom = enteredRoom;
        enteredRoom.usageTick = ++_usageCounter;
        if (verbose) Debug.Log($"[GameFlowManager] Jogador entrou na sala index={enteredRoom.index}");
    }

    // ---------- Find generated container (optimized) ----------
    private Transform FindGeneratedRoomContainer(RoomPlan plan)
    {
        string namePrefix = $"Room_{plan.originGrid.x}_{plan.originGrid.y}";
        
        if (roomsRoot != null)
        {
            var direct = roomsRoot.Find(namePrefix);
            if (direct != null) return direct;
            foreach (Transform t in roomsRoot)
                if (t != null && t.name.StartsWith(namePrefix)) return t;
        }

        // Procura nas raízes internas de cada gerador
        foreach(var generator in specialistGenerators)
        {
            var genRoot = generator.transform.Find("GeneratedRooms");
            if (genRoot != null)
            {
                var direct = genRoot.Find(namePrefix);
                if (direct != null) return direct;
                foreach (Transform t in genRoot)
                    if (t != null && t.name.StartsWith(namePrefix)) return t;
            }
        }

        var go = GameObject.Find(namePrefix);
        if (go != null) return go.transform;

        return null;
    }

    // ---------- Unload by distance ----------
    private void ManageUnloadByPlayerDistance()
    {
        // =======================================================================
        // INÍCIO DO BLOCO ALTERADO
        // =======================================================================
        // Só começa a descarregar salas por distância depois que o jogador entrar na primeira sala procedural.
        if (!_proceduralFlowHasStarted) return;
        // =======================================================================
        // FIM DO BLOCO ALTERADO
        // =======================================================================

        if (playerTransform == null) return;
        var toUnload = new List<ActiveRoom>();
        float unloadDistanceSq = unloadDistance * unloadDistance;
        for (var node = _activeRooms.Last; node != null; node = node.Previous)
        {
            var r = node.Value;
            if (r.container == null) { toUnload.Add(r); continue; }
            if (r == _currentActiveRoom) continue;
            Vector3 diff = playerTransform.position - r.container.position;
            if (diff.sqrMagnitude > unloadDistanceSq) toUnload.Add(r);
        }
        
        foreach (var r in toUnload)
            if (_activeRooms.Contains(r))
            {
                if (verbose) Debug.Log($"[GameFlowManager] Unloading room index={r.index} due to distance ({unloadDistance}).");
                UnloadRoom(r);
            }
    }

    // ---------- Utilities ----------
    public IReadOnlyList<RoomPlan> GetAllPlans() => _roomPlans.AsReadOnly();
    public int GetActiveRoomCount() => _activeRooms.Count;

    public void ProceedToNext()
    {
        if (_roomPlans.Count == 0) return;
        int nextIndex = 0;
        if (_activeRooms.Count > 0) nextIndex = _activeRooms.First.Value.index + 1;
        EnqueueRoomGeneration(nextIndex);
    }

    private void LogV(string msg) { if (verbose) Debug.Log($"[GameFlowManager] {msg}"); }
}