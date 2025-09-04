// BedroomGenerator.cs
// Autor: ChatGPT — gerador especializado "filho" de BaseRoomGenerator.
// Versão: 2025-08-20 (Revisada para corrigir CS1061 e alinhar com padrão de BaseRoomGenerator)
//
// Propósito:
//  - Corrigido o erro CS1061 ao adotar o padrão de geração correto.
//  - Chamar CreateRoomContainer da classe base para criar a instância da sala.
//  - Implementar a construção de chão e paredes, tornando o gerador autônomo.
//  - Popular a sala com uma lista customizável de "props" (objetos).
//  - Usar heurísticas rápidas e eficientes para escolher posições seguras para os props.
//
// Observações:
//  - Este script herda de BaseRoomGenerator e estende sua funcionalidade.
//  - Usa os métodos protegidos como SpawnFromPool e GridToWorld corretamente.

using System;
using System.Collections.Generic;
using UnityEngine;

// Struct para definir as propriedades de um objeto a ser colocado na sala.
[System.Serializable]
public struct RoomProp
{
    public GameObject prefab;
    public Vector2Int size;         // Tamanho em grid (ex: 2x3 para cama)
    [Tooltip("Se verdadeiro, tenta posicionar adjacente a uma parede.")]
    public bool placeOnWall;
    [Tooltip("Se verdadeiro e não for de parede, aplica uma rotação aleatória em Y.")]
    public bool randomRotation;
    [Range(0f, 1f)] public float placementChance; // Probabilidade de este prop ser colocado
}

public class BedroomGenerator : BaseRoomGenerator
{
    [Header("Seed / Random")]
    [Tooltip("Seed determinística. 0 = aleatória por hora (não-determinística).")]
    public int randomSeed = 0;

    [Header("Room Structure")]
    [Tooltip("Altura da sala em voxels.")]
    [Min(1)] public int roomHeight = 3; // Campo adicionado para definir a altura das paredes.

    [Header("Bedroom Prop List")]
    [SerializeField] private List<RoomProp> roomProps = new List<RoomProp>();

    // RNG local para decisões de props, separado do RNG base.
    private System.Random _localRng;

    private void Awake()
    {
        int seed = (randomSeed == 0) ? (Environment.TickCount ^ GetInstanceID()) : (randomSeed ^ GetInstanceID());
        _localRng = new System.Random(seed);
    }

    #region GenerateRoom Overloads (Compatibilidade com GameFlowManager)

    public new RoomInstance GenerateRoom()
    {
        return GenerateRoom(roomOriginGrid, roomSize);
    }

    public new RoomInstance GenerateRoom(Vector2Int originGrid)
    {
        Vector2Int randomSize = new Vector2Int(
            _localRng.Next(minRoomSize.x, maxRoomSize.x + 1),
            _localRng.Next(minRoomSize.y, maxRoomSize.y + 1)
        );
        return GenerateRoom(originGrid, randomSize);
    }

    /// <summary>
    /// Método principal que gera um quarto, construindo a base e depois populando com props.
    /// </summary>
    public new RoomInstance GenerateRoom(Vector2Int originGrid, Vector2Int size)
    {
        // 1) Gerar a sala base usando o método de fábrica da classe pai para criar o container.
        var room = CreateRoomContainer(originGrid, size, -1);
        if (room == null || room.container == null)
        {
            Debug.LogWarning("[BedroomGenerator] A criação do container da sala base falhou.");
            return null;
        }

        // 2) Construir a estrutura básica (chão e paredes).
        var occupancy = new HashSet<Vector3Int>();
        // Chão
        for (int x = 0; x < size.x; x++)
        for (int z = 0; z < size.y; z++)
            occupancy.Add(new Vector3Int(x, 0, z));
        
        // Paredes
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
        
        // Instanciar os voxels com base no mapa de ocupação
        foreach (var pos in occupancy)
        {
            Vector3 worldPos = GridToWorld(originGrid) + new Vector3(pos.x * voxelSize, pos.y * voxelSize, pos.z * voxelSize);
            var go = SpawnFromPool(voxelFundamentalPrefab, worldPos, Quaternion.identity, room.container);
            if (go != null)
            {
                room.spawnedVoxels.Add(go);
            }
        }

        // 3) Chamar a lógica de população de props.
        PopulateRoomWithProps(room);

        // 4) Retornar a RoomInstance já populada.
        return room;
    }

    #endregion

    /// <summary>
    /// Lógica central para popular uma RoomInstance existente com os props definidos.
    /// </summary>
    private void PopulateRoomWithProps(RoomInstance room)
    {
        Vector3 baseWorldPos = GridToWorld(room.originGrid); 
        int w = room.size.x;
        int h = room.size.y;

        bool[,] occupied = new bool[w, h];
        bool[,] isWall = new bool[w, h];
        bool[,] isDoor = new bool[w, h];

        // Mapas de ocupação (apenas no nível do chão para props)
        var floorVoxels = new Dictionary<Vector2Int, GameObject>();
        foreach (var go in room.spawnedVoxels)
        {
            if (go == null) continue;

            Vector3 localPos = go.transform.position - baseWorldPos;
            int gx = Mathf.RoundToInt(localPos.x / voxelSize);
            int gy = Mathf.RoundToInt(localPos.z / voxelSize);
            int g_y = Mathf.RoundToInt(localPos.y / voxelSize); // Coordenada Y (altura)

            if (gx < 0 || gx >= w || gy < 0 || gy >= h) continue;

            // Marcar como ocupado para props apenas se for um voxel no chão (ou algo sobre ele)
            if (g_y == 0)
            {
                occupied[gx, gy] = true;
                floorVoxels[new Vector2Int(gx, gy)] = go;
            }

            // Heurística para identificar paredes no nível do chão
            if (g_y > 0 && (gx == 0 || gx == w - 1 || gy == 0 || gy == h - 1))
            {
                isWall[gx, gy] = true;
            }
        }
        
        // --- Funções Helper Locais para Posicionamento ---

        bool IsInBounds(int x, int y) => x >= 0 && x < w && y >= 0 && y < h;

        bool IsNearbyDoor(int x, int y, int radius)
        {
            int startX = Math.Max(0, x - radius), endX = Math.Min(w - 1, x + radius);
            int startY = Math.Max(0, y - radius), endY = Math.Min(h - 1, y + radius);
            for (int ix = startX; ix <= endX; ix++)
                for (int iy = startY; iy <= endY; iy++)
                    if (isDoor[ix, iy]) return true;
            return false;
        }

        bool IsAreaFree(int startX, int startY, int areaW, int areaH)
        {
            if (!IsInBounds(startX, startY) || !IsInBounds(startX + areaW - 1, startY + areaH - 1)) return false;
            for (int ix = startX; ix < startX + areaW; ix++)
                for (int iy = startY; iy < startY + areaH; iy++)
                    if (occupied[ix, iy]) return false;
            return true;
        }

        bool TryFindPlacementArea(int areaW, int areaH, bool mustBeNextToWall, out Vector2Int foundPos, out Quaternion wallRotation)
        {
            const int maxAttempts = 50;
            wallRotation = Quaternion.identity;

            for (int i = 0; i < maxAttempts; i++)
            {
                int gx = _localRng.Next(1, w - areaW);
                int gy = _localRng.Next(1, h - areaH);

                if (!IsAreaFree(gx, gy, areaW, areaH) || IsNearbyDoor(gx + areaW / 2, gy + areaH / 2, 2)) continue;

                if (mustBeNextToWall)
                {
                    if (IsInBounds(gx - 1, gy) && isWall[gx - 1, gy]) wallRotation = Quaternion.LookRotation(Vector3.right);
                    else if (IsInBounds(gx + areaW, gy) && isWall[gx + areaW, gy]) wallRotation = Quaternion.LookRotation(Vector3.left);
                    else if (IsInBounds(gx, gy - 1) && isWall[gx, gy - 1]) wallRotation = Quaternion.LookRotation(Vector3.forward);
                    else if (IsInBounds(gx, gy + areaH) && isWall[gx, gy + areaH]) wallRotation = Quaternion.LookRotation(Vector3.back);
                    else continue;
                }

                foundPos = new Vector2Int(gx, gy);
                return true;
            }

            foundPos = Vector2Int.zero;
            return false;
        }
        
        // --- Sequência de Posicionamento de Props ---
        
        foreach (var prop in roomProps)
        {
            if (prop.prefab == null || _localRng.NextDouble() > prop.placementChance) continue;

            int propW = prop.size.x > 0 ? prop.size.x : 1;
            int propH = prop.size.y > 0 ? prop.size.y : 1;

            if (TryFindPlacementArea(propW, propH, prop.placeOnWall, out var foundPos, out var wallRot))
            {
                Quaternion finalRotation = wallRot;
                if (!prop.placeOnWall && prop.randomRotation)
                {
                    finalRotation = Quaternion.Euler(0, 90f * _localRng.Next(0, 4), 0);
                }
                
                Vector3 centerPos = baseWorldPos + new Vector3(
                    (foundPos.x + (propW - 1) * 0.5f) * voxelSize,
                    0f,
                    (foundPos.y + (propH - 1) * 0.5f) * voxelSize
                );

                var spawnedProp = SpawnFromPool(prop.prefab, centerPos, finalRotation, room.container);
                if (spawnedProp == null) spawnedProp = Instantiate(prop.prefab, centerPos, finalRotation, room.container);

                if (spawnedProp.GetComponent<PoolableObject>() == null)
                {
                    var tag = spawnedProp.AddComponent<PoolableObject>();
                    tag.OriginalPrefab = prop.prefab;
                }

                room.spawnedProps.Add(spawnedProp);
                
                // Marca a área como ocupada para os próximos props
                for (int ix = foundPos.x; ix < foundPos.x + propW; ix++)
                    // >>> CORREÇÃO APLICADA NA LINHA ABAIXO <<<
                    for (int iy = foundPos.y; iy < foundPos.y + propH; iy++)
                        if (IsInBounds(ix, iy)) occupied[ix, iy] = true;
            }
        }
        
        // Otimização: Desativa sombras nos props gerados para melhorar performance
        foreach (var p in room.spawnedProps)
        {
            if (p == null) continue;
            var renderers = p.GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
        }
    }
}