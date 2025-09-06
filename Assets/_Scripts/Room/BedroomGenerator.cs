// BedroomGenerator.cs
// Gera uma sala “Bedroom” simples: estrutura oca (chão + paredes) e popula com props básicos.
// Mudança mínima p/ PR-02: apenas aliases para tipos de RoomsData.
// Mantido o restante da lógica exatamente como no original.

using System;
using System.Collections.Generic;
using UnityEngine;

// Aliases para o contrato único em RoomsData (evita CS0246/CS0576)
using RoomPlan     = RoomsData.RoomPlan;
using RoomInstance = RoomsData.RoomInstance;

[DisallowMultipleComponent]
public class BedroomGenerator : BaseRoomGenerator
{
    #region Inspector
    [Header("Seed / Random")]
    [Tooltip("0 = aleatória por hora (não determinística).")]
    public int randomSeed = 0;

    [Header("Room Structure")]
    [Tooltip("Altura da sala (em voxels).")]
    [Min(1)] public int roomHeight = 3;

    [Serializable]
    public struct RoomProp
    {
        public GameObject prefab;
        public Vector2Int size;         // tamanho em grid (X,Z)

        [Tooltip("Se verdadeiro, tenta encostar na parede.")]
        public bool placeOnWall;

        [Tooltip("Se verdadeiro e não for de parede, gira aleatoriamente em Y.")]
        public bool randomRotation;

        [Range(0f, 1f)]
        public float placementChance;
    }

    [Header("Bedroom Prop List")]
    [SerializeField] private List<RoomProp> roomProps = new List<RoomProp>();
    #endregion

    // RNG local para props (separado do Base)
    private System.Random _localRng;

    private void Awake()
    {
        // Seed local: mistura do randomSeed com o instanceID (igual ao base da versão RAW)
        int seed = (randomSeed == 0) ? (Environment.TickCount ^ GetInstanceID()) : (randomSeed ^ GetInstanceID());
        _localRng = new System.Random(seed);
    }

    #region GenerateRoom Overloads (Compatíveis)
    // Mantém assinaturas convenientes e retorna RoomInstance (tipo RoomsData)
    public new RoomInstance GenerateRoom() => GenerateRoom(roomOriginGrid, roomSize);

    public new RoomInstance GenerateRoom(Vector2Int originGrid)
    {
        var randomSize = new Vector2Int(
            _localRng.Next(minRoomSize.x, maxRoomSize.x + 1),
            _localRng.Next(minRoomSize.y, maxRoomSize.y + 1)
        );
        return GenerateRoom(originGrid, randomSize);
    }

    /// <summary>
    /// Método principal: constrói a casca da sala e popula com props.
    /// </summary>
    public new RoomInstance GenerateRoom(Vector2Int originGrid, Vector2Int size)
    {
        // 1) Cria container básico via Base (já registra e fornece roots de porta).
        var room = CreateRoomContainer(originGrid, size, -1);
        if (room == null || room.root == null)
        {
            Debug.LogWarning("[BedroomGenerator] Falha ao criar container da sala.");
            return null;
        }

        // 1.1) Preenche metadados mínimos no plano (útil para consumidores)
        // Usa DoorRect padrão; geradores mais avançados podem definir entry/exit reais depois.
        room.plan = new RoomPlan(
            id: -1,
            gridOrigin: originGrid,
            size: new Vector2Int(Mathf.Max(1, size.x), Mathf.Max(1, size.y)),
            height: Mathf.Max(1, roomHeight),
            entry: default,
            exit: default,
            generatorIndex: 0,
            randomSeed: randomSeed
        );

        // Informações auxiliares (streaming/ordenamento)
        room.builder   = this;
        room.voxelSize = voxelSize;

        // 2) Construir estrutura básica: chão + paredes (sem arestas/quinas internas)
        var occupancy = new HashSet<Vector3Int>();

        // chão
        for (int x = 0; x < size.x; x++)
            for (int z = 0; z < size.y; z++)
                occupancy.Add(new Vector3Int(x, 0, z));

        // paredes (perímetro) até roomHeight
        for (int y = 0; y < roomHeight; y++)
        {
            for (int x = 0; x < size.x; x++)
            {
                occupancy.Add(new Vector3Int(x, y, 0));
                occupancy.Add(new Vector3Int(x, y, size.y - 1));
            }
            for (int z = 0; z < size.y; z++)
            {
                occupancy.Add(new Vector3Int(0, y, z));
                occupancy.Add(new Vector3Int(size.x - 1, y, z));
            }
        }

        // 2.1) Instanciar voxels fundamentais conforme o mapa de ocupação
        Vector3 baseWorld = GridToWorld(originGrid);
        foreach (var pos in occupancy)
        {
            Vector3 worldPos = baseWorld + new Vector3(pos.x * voxelSize, pos.y * voxelSize, pos.z * voxelSize);
            var go = SpawnFromPool(voxelFundamentalPrefab, worldPos, Quaternion.identity, room.root);
            if (go != null) room.voxels.Add(go);
        }

        room.built = true;

        // 3) Popular com props
        PopulateRoomWithProps(room);
        room.populated = true;

        return room;
    }
    #endregion

    /// <summary>
    /// Popula a RoomInstance com props respeitando ocupação e paredes.
    /// </summary>
    private void PopulateRoomWithProps(RoomInstance room)
    {
        if (room == null || room.root == null) return;

        // Dimensões do plano (X,Z) vêm do plan.size (definido acima)
        int w = Mathf.Max(1, room.plan.size.x);
        int d = Mathf.Max(1, room.plan.size.y);

        // Mapa de ocupação no nível do chão para props
        bool[,] occupied = new bool[w, d];
        bool[,] isWall   = new bool[w, d];
        bool[,] isDoor   = new bool[w, d]; // reservado para futuras integrações com portas reais

        // Indexa voxels já gerados para derivar ocupação e paredes (nível Y=0)
        Vector3 baseWorldPos = GridToWorld(room.plan.gridOrigin);
        foreach (var go in room.voxels)
        {
            if (go == null) continue;

            Vector3 local = go.transform.position - baseWorldPos;
            int gx = Mathf.RoundToInt(local.x / voxelSize);
            int gz = Mathf.RoundToInt(local.z / voxelSize);
            int gy = Mathf.RoundToInt(local.y / voxelSize);

            if (gx < 0 || gx >= w || gz < 0 || gz >= d) continue;

            if (gy == 0) occupied[gx, gz] = true;
            if (gy > 0 && (gx == 0 || gx == w - 1 || gz == 0 || gz == d - 1))
                isWall[gx, gz] = true;
        }

        // Helpers locais
        bool InBounds(int x, int z) => (uint)x < (uint)w && (uint)z < (uint)d;

        bool IsNearbyDoor(int x, int z, int radius)
        {
            int sx = Mathf.Max(0, x - radius), ex = Mathf.Min(w - 1, x + radius);
            int sz = Mathf.Max(0, z - radius), ez = Mathf.Min(d - 1, z + radius);
            for (int ix = sx; ix <= ex; ix++)
                for (int iz = sz; iz <= ez; iz++)
                    if (isDoor[ix, iz]) return true;
            return false;
        }

        bool IsAreaFree(int x, int z, int aw, int ad)
        {
            if (!InBounds(x, z) || !InBounds(x + aw - 1, z + ad - 1)) return false;
            for (int ix = x; ix < x + aw; ix++)
                for (int iz = z; iz < z + ad; iz++)
                    if (occupied[ix, iz]) return false;
            return true;
        }

        bool TryFindPlacementArea(int aw, int ad, bool mustBeOnWall, out Vector2Int pos, out Quaternion wallRot)
        {
            const int MAX_ATTEMPTS = 50;
            wallRot = Quaternion.identity;

            for (int i = 0; i < MAX_ATTEMPTS; i++)
            {
                int gx = _localRng.Next(1, Math.Max(1, w - aw));
                int gz = _localRng.Next(1, Math.Max(1, d - ad));

                if (!IsAreaFree(gx, gz, aw, ad)) continue;
                if (IsNearbyDoor(gx + aw / 2, gz + ad / 2, 2)) continue;

                if (mustBeOnWall)
                {
                    if (InBounds(gx - 1, gz) && isWall[gx - 1, gz])
                        wallRot = Quaternion.LookRotation(Vector3.right);
                    else if (InBounds(gx + aw, gz) && isWall[gx + aw, gz])
                        wallRot = Quaternion.LookRotation(Vector3.left);
                    else if (InBounds(gx, gz - 1) && isWall[gx, gz - 1])
                        wallRot = Quaternion.LookRotation(Vector3.forward);
                    else if (InBounds(gx, gz + ad) && isWall[gx, gz + ad])
                        wallRot = Quaternion.LookRotation(Vector3.back);
                    else
                        continue; // não encostou em parede
                }

                pos = new Vector2Int(gx, gz);
                return true;
            }

            pos = default;
            return false;
        }

        // Sequência de posicionamento
        foreach (var prop in roomProps)
        {
            if (prop.prefab == null || _localRng.NextDouble() > prop.placementChance)
                continue;

            int aw = Math.Max(1, prop.size.x);
            int ad = Math.Max(1, prop.size.y);

            if (TryFindPlacementArea(aw, ad, prop.placeOnWall, out var found, out var wallRot))
            {
                Quaternion rot = wallRot;
                if (!prop.placeOnWall && prop.randomRotation)
                    rot = Quaternion.Euler(0, 90f * _localRng.Next(0, 4), 0);

                // Centro do prop na área encontrada (em nível do chão)
                Vector3 center = baseWorldPos + new Vector3(
                    (found.x + (aw - 1) * 0.5f) * voxelSize,
                    0f,
                    (found.y + (ad - 1) * 0.5f) * voxelSize
                );

                var spawned = SpawnFromPool(prop.prefab, center, rot, room.root);
                if (spawned == null)
                    spawned = Instantiate(prop.prefab, center, rot, room.root);

                // Garantir tag de pool para retorno correto
                var tag = spawned.GetComponent<PoolableObject>();
                if (tag == null) tag = spawned.AddComponent<PoolableObject>();
                tag.OriginalPrefab = prop.prefab;

                room.props.Add(spawned);

                // Marca área ocupada (correção de Z conforme base RAW)
                for (int ix = found.x; ix < found.x + aw; ix++)
                    for (int iz = found.y; iz < found.y + ad; iz++)
                        if (InBounds(ix, iz)) occupied[ix, iz] = true;
            }
        }

        // Otimização simples: desativa sombras dos props para performance
        foreach (var p in room.props)
        {
            if (!p) continue;
            var rends = p.GetComponentsInChildren<Renderer>(true);
            foreach (var r in rends)
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }
    }
}
