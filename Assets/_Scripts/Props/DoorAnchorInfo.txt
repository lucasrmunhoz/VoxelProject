// DoorAnchorInfo.cs
// Guarda dimensões "assadas" de uma abertura de porta em VOXELS para alinhar
// a próxima sala procedural a partir de uma sala estática criada no Editor.

using System;
using UnityEngine;

// === Correção mínima PR-02: aliases para os tipos do RoomsData ===
using WallSide = RoomsData.WallSide;
using DoorRect = RoomsData.DoorRect;

[DisallowMultipleComponent]
[AddComponentMenu("Voxel Nightmare/Door Anchor Info")]
public sealed class DoorAnchorInfo : MonoBehaviour
{
    [Header("Frame de Referência")]
    [Tooltip("Transform que define o espaço local da sala estática (origem/rotação). Se vazio, usa o pai deste objeto.")]
    [SerializeField] private Transform roomRoot;

    [Tooltip("Tamanho mundial de 1 voxel (tipicamente 1.0).")]
    [SerializeField, Min(0.0001f)] private float voxelSize = 1f;

    [Header("Especificação da Porta (em VOXELS)")]
    [Tooltip("Parede onde a porta existe no espaço LOCAL de RoomRoot.")]
    [SerializeField] private WallSide side = WallSide.North;

    [Tooltip("Deslocamento ao longo da parede (em voxels). 0 = canto esquerdo dessa parede.")]
    [SerializeField, Min(0)] private int offsetX = 0;

    [Tooltip("Largura da abertura (em voxels).")]
    [SerializeField, Min(1)] private int width = 1;

    [Tooltip("Altura mínima (em voxels, inclusive).")]
    [SerializeField] private int yMin = 0;

    [Tooltip("Altura máxima (em voxels, inclusive). Deve ser >= yMin.")]
    [SerializeField] private int yMax = 2;

    /// <summary>Altura da abertura em voxels.</summary>
    public int Height => (yMax - yMin) + 1;

    /// <summary>Transform que define o frame local da sala.</summary>
    public Transform RoomRoot => roomRoot != null ? roomRoot : transform.parent;

    /// <summary>Tamanho mundial de 1 voxel.</summary>
    public float VoxelSize => voxelSize;

    /// <summary>Parede local (N/E/S/W) onde a abertura está.</summary>
    public WallSide Side => side;

    /// <summary>Deslocamento ao longo da parede (em voxels).</summary>
    public int OffsetX => offsetX;

    /// <summary>Largura da abertura (em voxels).</summary>
    public int Width => width;

    /// <summary>Altura mínima (inclusive, em voxels).</summary>
    public int YMin => yMin;

    /// <summary>Altura máxima (inclusive, em voxels).</summary>
    public int YMax => yMax;

    /// <summary>
    /// Constrói um DoorRect com os mesmos parâmetros (em VOXELS).
    /// Use isto para repassar ao gerador procedural.
    /// </summary>
    public DoorRect ToDoorRect() => new DoorRect(side, offsetX, width, yMin, yMax);

    /// <summary>
    /// Retorna (tangente, normal) para a parede no ESPAÇO LOCAL de RoomRoot.
    /// </summary>
    public static void GetWallBasisLocal(WallSide s, out Vector3 tangent, out Vector3 normal)
    {
        switch (s)
        {
            case WallSide.North: tangent = Vector3.right;  normal = Vector3.forward; break; // +Z
            case WallSide.East:  tangent = Vector3.forward;normal = Vector3.right;   break; // +X
            case WallSide.South: tangent = Vector3.left;   normal = Vector3.back;    break; // -Z
            case WallSide.West:  tangent = Vector3.back;   normal = Vector3.left;    break; // -X
            default:             tangent = Vector3.right;  normal = Vector3.forward; break;
        }
    }

    /// <summary>
    /// Converte a base local (tangente/normal) para o ESPAÇO MUNDIAL, usando RoomRoot.
    /// </summary>
    public void GetWallBasisWorld(out Vector3 tangentW, out Vector3 normalW)
    {
        GetWallBasisLocal(side, out var t, out var n);
        var root = RoomRoot != null ? RoomRoot : transform;
        tangentW = root.TransformDirection(t);
        normalW  = root.TransformDirection(n);
    }

    /// <summary>Retorna o vetor "up" mundial coerente com RoomRoot.</summary>
    public Vector3 GetUpWorld()
    {
        var root = RoomRoot;
        return root ? root.TransformDirection(Vector3.up) : Vector3.up;
    }

    private void OnValidate()
    {
        if (voxelSize <= 0f) voxelSize = 1f;
        if (width < 1) width = 1;
        if (offsetX < 0) offsetX = 0;
        if (yMax < yMin) yMax = yMin;
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        // Desenha um quadro da abertura (debug visual no SceneView)
        var center = transform.position;
        GetWallBasisWorld(out var tangentW, out var normalW);
        var upW = GetUpWorld();

        float w = width * voxelSize;
        float h = Height * voxelSize;
        float thickness = Mathf.Min(0.1f, voxelSize * 0.2f); // leve afastamento para evitar z-fighting

        Vector3 right = tangentW.normalized;
        Vector3 up = upW.normalized;
        Vector3 n = normalW.normalized;

        Vector3 halfW = right * (w * 0.5f);
        Vector3 halfH = up * (h * 0.5f);
        Vector3 slightOffset = n * (thickness * 0.5f);

        Vector3 p0 = center + (-halfW - halfH) + slightOffset;
        Vector3 p1 = center + ( halfW - halfH) + slightOffset;
        Vector3 p2 = center + ( halfW + halfH) + slightOffset;
        Vector3 p3 = center + (-halfW + halfH) + slightOffset;

        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(p0, p1);
        Gizmos.DrawLine(p1, p2);
        Gizmos.DrawLine(p2, p3);
        Gizmos.DrawLine(p3, p0);

        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(center, center + n * (voxelSize * 1.0f));

        // Label informativo
        UnityEditor.Handles.color = new Color(0f, 1f, 1f, 0.8f);
        var label = $"DoorAnchorInfo\nSide: {side}\nOffsetX: {offsetX} Width: {width}\nY: {yMin}..{yMax} (H={Height})";
        UnityEditor.Handles.Label(center + up * (h * 0.6f), label);
    }
#endif
}
