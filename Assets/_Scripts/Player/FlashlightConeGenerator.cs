// FlashlightConeGenerator.cs
using UnityEngine;

/// <summary>
/// Responsável por gerar e atualizar proceduralmente um MeshCollider em formato de cone
/// correspondente ao SpotLight. 
/// Mantido separado de FlashlightControl para respeitar o SRP (Single Responsibility Principle).
/// 
/// Otimizações aplicadas:
/// - Evita recriar a malha desnecessariamente (reconstrói somente quando range/ângulo/resolução mudam).
/// - Reaproveita buffers de vértices/triângulos para reduzir garbage.
/// - Cache dos últimos parâmetros usados, evitando cálculos redundantes.
/// - Mantém a malha mínima e limpa (sem UVs/cores/normais adicionais).
/// </summary>
[DisallowMultipleComponent]
public class FlashlightConeGenerator : MonoBehaviour
{
    [Header("Referências")]
    [SerializeField] private Light _targetLight;
    [SerializeField] private MeshCollider _meshCollider;

    [Header("Configuração do Cone")]
    [Tooltip("Número de lados do cone. Valores maiores = cone mais suave, porém mais custoso.")]
    [SerializeField, Range(8, 64)] private int _coneResolution = 24;

    private Mesh _coneMesh;
    private Vector3[] _verticesCache;
    private int[] _trianglesCache;

    private float _lastRange;
    private float _lastSpotAngle;
    private int _lastResolution;

    #region Inicialização
    public void Initialize(Light light, MeshCollider meshCollider)
    {
        _targetLight = light;
        _meshCollider = meshCollider;

        if (_targetLight == null || _meshCollider == null)
        {
            Debug.LogError("[FlashlightConeGenerator] Falha na inicialização: referências inválidas.", this);
            enabled = false;
            return;
        }

        if (_targetLight.type != LightType.Spot)
        {
            Debug.LogWarning($"[FlashlightConeGenerator] O Light '{light.name}' não é Spot. ConeCollider desativado.", this);
            enabled = false;
            return;
        }

        // Configura MeshCollider para uso em triggers convexos
        _meshCollider.convex = true;
        _meshCollider.isTrigger = true;

        // Cria e associa a malha do cone
        _coneMesh = new Mesh
        {
            name = $"{gameObject.name}_FlashlightCone"
        };
        _meshCollider.sharedMesh = _coneMesh;

        // Força primeira atualização
        GenerateConeMesh();
    }
    #endregion

    #region Atualização do Cone
    public void UpdateCone()
    {
        if (_targetLight == null || _coneMesh == null) return;

        if (HasConeChanged())
        {
            GenerateConeMesh();
        }
    }

    private bool HasConeChanged()
    {
        return !Mathf.Approximately(_lastRange, _targetLight.range) ||
               !Mathf.Approximately(_lastSpotAngle, _targetLight.spotAngle) ||
               _lastResolution != _coneResolution;
    }

    private void GenerateConeMesh()
    {
        float range = _targetLight.range;
        float spotAngle = _targetLight.spotAngle;
        int resolution = _coneResolution;

        float radiusAtBase = Mathf.Tan(spotAngle * 0.5f * Mathf.Deg2Rad) * range;

        // --- Cache de arrays (evita GC) ---
        int vertexCount = resolution + 2; // ápice + base + centro da base
        if (_verticesCache == null || _verticesCache.Length != vertexCount)
            _verticesCache = new Vector3[vertexCount];

        int triCount = resolution * 2 * 3; // laterais + base
        if (_trianglesCache == null || _trianglesCache.Length != triCount)
            _trianglesCache = new int[triCount];

        // --- Vértices ---
        _verticesCache[0] = Vector3.zero; // ápice
        float angleStep = 2f * Mathf.PI / resolution;
        for (int i = 0; i < resolution; i++)
        {
            float angle = i * angleStep;
            _verticesCache[i + 1] = new Vector3(Mathf.Sin(angle) * radiusAtBase,
                                                Mathf.Cos(angle) * radiusAtBase,
                                                range);
        }
        _verticesCache[resolution + 1] = new Vector3(0f, 0f, range); // centro da base

        // --- Triângulos ---
        int triIndex = 0;
        for (int i = 0; i < resolution; i++)
        {
            int current = i + 1;
            int next = (i + 1) % resolution + 1;

            // lateral
            _trianglesCache[triIndex++] = 0;
            _trianglesCache[triIndex++] = next;
            _trianglesCache[triIndex++] = current;

            // base
            _trianglesCache[triIndex++] = resolution + 1;
            _trianglesCache[triIndex++] = current;
            _trianglesCache[triIndex++] = next;
        }

        // --- Atualiza malha ---
        _coneMesh.Clear();
        _coneMesh.SetVertices(_verticesCache);
        _coneMesh.SetTriangles(_trianglesCache, 0, true);
        _coneMesh.RecalculateNormals(); // barato, necessário pro Collider

        _meshCollider.sharedMesh = _coneMesh;

        // --- Atualiza cache ---
        _lastRange = range;
        _lastSpotAngle = spotAngle;
        _lastResolution = resolution;
    }
    #endregion

    private void OnDestroy()
    {
        if (_coneMesh != null)
        {
            Destroy(_coneMesh);
        }
    }
}
