// RoomsData.cs
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Lado da parede onde uma porta pode existir.
/// </summary>
public enum WallSide
{
    North = 0,
    East  = 1,
    South = 2,
    West  = 3
}

/// <summary>
/// Retângulo de porta em coordenadas de grade locais à sala.
/// x: deslocamento horizontal ao longo da parede (em voxels)
/// width: largura da abertura (em voxels 1x1x1)
/// yMin/yMax: faixa vertical da abertura (em voxels)
/// </summary>
[Serializable]
public struct DoorRect
{
    [Tooltip("Parede onde a porta está embutida (North/East/South/West).")]
    public WallSide side;

    [Tooltip("Deslocamento ao longo da parede (em voxels).")]
    public int x;

    [Tooltip("Largura da porta (em voxels).")]
    public int width;

    [Tooltip("Altura mínima (inclusive, em voxels).")]
    public int yMin;

    [Tooltip("Altura máxima (inclusive, em voxels).")]
    public int yMax;

    public int Height => (yMax - yMin) + 1;

    public DoorRect(WallSide side, int x, int width, int yMin, int yMax)
    {
        this.side  = side;
        this.x     = x;
        this.width = Mathf.Max(1, width);
        this.yMin  = yMin;
        this.yMax  = Mathf.Max(yMin, yMax);
    }

    public override string ToString()
    {
        return $"DoorRect[{side}] x={x}, w={width}, y=[{yMin},{yMax}] (h={Height})";
    }
}

/// <summary>
/// Plano estático de uma sala gerada no "Plan-then-Populate".
/// Usado pelo GameFlowManager para mapear o nível inteiro antes da construção.
/// </summary>
[Serializable]
public struct RoomPlan
{
    [Tooltip("Identificador único do plano (índice no layout).")]
    public int id;

    [Tooltip("Origem da sala no grid global de planejamento.")]
    public Vector2Int gridOrigin;

    [Tooltip("Tamanho (largura x profundidade) em voxels.")]
    public Vector2Int size;

    [Tooltip("Altura da sala (em voxels).")]
    public int height;

    [Tooltip("Porta de entrada planejada.")]
    public DoorRect entry;

    [Tooltip("Porta de saída planejada.")]
    public DoorRect exit;

    [Tooltip("Índice do gerador especialista que irá construir/popular esta sala.")]
    public int generatorIndex;

    [Tooltip("Seed específico do plano para variação determinística.")]
    public int randomSeed;

    public RoomPlan(
        int id,
        Vector2Int gridOrigin,
        Vector2Int size,
        int height,
        DoorRect entry,
        DoorRect exit,
        int generatorIndex,
        int randomSeed)
    {
        this.id             = id;
        this.gridOrigin     = gridOrigin;
        this.size           = new Vector2Int(Mathf.Max(1, size.x), Mathf.Max(1, size.y));
        this.height         = Mathf.Max(1, height);
        this.entry          = entry;
        this.exit           = exit;
        this.generatorIndex = Mathf.Max(0, generatorIndex);
        this.randomSeed     = randomSeed;
    }

    public override string ToString()
    {
        return $"RoomPlan#{id} origin={gridOrigin} size={size} h={height} gen={generatorIndex} seed={randomSeed} entry={entry} exit={exit}";
    }
}

/// <summary>
/// Instância viva da sala no runtime. Mantém referências para limpeza (pool) e debug.
/// É serializável para inspeção no Inspector (quando usado em listas).
/// </summary>
[Serializable]
public class RoomInstance
{
    [Tooltip("Plano que originou esta instância.")]
    public RoomPlan plan;

    [Tooltip("Root/Container da sala no runtime (pai de voxels/props/portas).")]
    public Transform root;

    [Tooltip("Voxels principais (1x1x1) instanciados para a estrutura oca).")]
    public List<GameObject> voxels = new List<GameObject>();

    [Tooltip("Objetos decorativos/props da sala.")]
    public List<GameObject> props = new List<GameObject>();

    [Tooltip("Root da porta de ENTRADA (container com cubos 1x1x1).")]
    public Transform entryDoorRoot;

    [Tooltip("Root da porta de SAÍDA (container com cubos 1x1x1).")]
    public Transform exitDoorRoot;

    [Tooltip("Componente de estado da sala (RoomStateManager). Armazenado como MonoBehaviour para evitar dependência rígida.")]
    public MonoBehaviour roomState;

    [Tooltip("Flag auxiliar para depuração (foi construída?).")]
    public bool built;

    [Tooltip("Flag auxiliar para depuração (foi pós-populada?).")]
    public bool populated;

    // -------------------- ADIÇÕES (streaming de portas) --------------------
    [Tooltip("Gerador que construiu esta sala (para utilidades de streaming).")]
    public BaseRoomGenerator builder;

    [Tooltip("Tamanho mundial de 1 voxel usado nesta sala.")]
    public float voxelSize = 1f;
    // ----------------------------------------------------------------------

    public void ResetCollections()
    {
        voxels ??= new List<GameObject>();
        props  ??= new List<GameObject>();
        voxels.Clear();
        props.Clear();
    }

    public override string ToString()
    {
        string rootName = root ? root.name : "null";
        return $"RoomInstance(plan#{plan.id}, root={rootName}, voxels={voxels?.Count ?? 0}, props={props?.Count ?? 0})";
        }
}
