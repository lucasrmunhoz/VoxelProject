using System.Runtime.CompilerServices;
using UnityEngine;

/// <summary>
/// SimpleVoxel (versão otimizada)
/// - Voxel simples com um único Renderer + Collider opcional.
/// - Evita SetActive, usa MaterialPropertyBlock, e faz fades sem coroutines (menos GC).
/// - Compatível com BaseVoxel (implementa OnInitialize).
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Renderer))]
public class SimpleVoxel : BaseVoxel
{
    [Header("Configuração (opcional)")]
    [SerializeField] private Renderer _renderer;        // pode ser atribuído no editor
    [SerializeField] private Collider _collider;        // opcional
    [SerializeField] private bool _defaultVisible = true;
    [SerializeField] private Color _defaultColor = Color.white;
    [SerializeField, Range(0.01f, 10f)] private float _defaultFadeSpeed = 1f; // segundos padrão

    // MaterialPropertyBlock para evitar instanciar materiais
    private MaterialPropertyBlock _mpb;

    // Caches de estado para evitar reatribuições
    private bool _visibleCache;
    private Color _colorCache;

    // Fade (update-driven para evitar alocação de coroutines)
    private bool _isFading;
    private Color _fadeStart;
    private Color _fadeTarget;
    private float _fadeDuration;
    private float _fadeElapsed;

    #region Unity lifecycle
    private void Awake()
    {
        // Cache de componentes (GetComponent uma vez)
        if (_renderer == null)
            TryGetComponent<Renderer>(out _renderer); // RequireComponent garante que haja um renderer

        if (_collider == null)
            TryGetComponent<Collider>(out _collider); // pode ser null

        _mpb = new MaterialPropertyBlock();

        // Inicializa caches com valores sensíveis
        _visibleCache = _renderer ? _renderer.enabled : false;
        _colorCache = _defaultColor;

        // Garanta que a cor inicial seja aplicada ao material (sem instanciar)
        ApplyColorToRenderer(_colorCache);
    }

    private void Update()
    {
        // Processa fade de forma eficiente (somente quando ativo)
        if (_isFading)
        {
            _fadeElapsed += Time.deltaTime;
            float t = (_fadeDuration <= 0f) ? 1f : Mathf.Clamp01(_fadeElapsed / _fadeDuration);
            Color cur = Color.Lerp(_fadeStart, _fadeTarget, t);

            // Atualiza somente se cor mudou (pequena margem para evitar chamadas redundantes)
            if (!ColorsApproximatelyEqual(cur, _colorCache))
            {
                ApplyColorToRenderer(cur);
                _colorCache = cur;
            }

            if (t >= 1f)
            {
                _isFading = false;
            }
        }
    }
    #endregion

    #region BaseVoxel implementation
    /// <summary>
    /// OnInitialize é chamado pela BaseVoxel.Initialize(...)
    /// Aqui aplicamos visibilidade e estado de colisão baseado no tipo e isSolid.
    /// </summary>
    protected override void OnInitialize(VoxelType type, bool isSolid)
    {
        // Regra simples: se for Empty -> invisível; caso contrário visível por padrão.
        // Você pode adaptar essa lógica conforme sua convenção.
        bool shouldBeVisible = type != VoxelType.Empty && _defaultVisible;

        // Aplicar visibilidade (somente se mudar)
        SetVisible(shouldBeVisible);

        // Collider: habilita se isSolid for true
        if (_collider != null && _collider.enabled != isSolid)
            _collider.enabled = isSolid;

        // Cor padrão - deixa a cor atual ou aplica a default imediatamente
        if (!ColorsApproximatelyEqual(_colorCache, _defaultColor))
        {
            ApplyColorToRenderer(_defaultColor);
            _colorCache = _defaultColor;
        }

        // Cancelar fades pendentes ao reconfigurar (evita comportamentos estranhos)
        _isFading = false;
    }
    #endregion

    #region Public API (rápida e sem alocação)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetVisible(bool visible)
    {
        if (_renderer == null) return;
        if (_visibleCache == visible) return;

        _renderer.enabled = visible;
        _visibleCache = visible;
    }

    /// <summary>
    /// Aplica cor imediatamente (MPB, sem instanciar material).
    /// </summary>
    public void SetColor(Color color)
    {
        if (_renderer == null) return;
        if (ColorsApproximatelyEqual(_colorCache, color)) return;

        ApplyColorToRenderer(color);
        _colorCache = color;
    }

    /// <summary>
    /// Inicia um fade de cor (sem coroutines). duration em segundos.
    /// </summary>
    public void BeginFadeToColor(Color target, float duration = -1f)
    {
        if (_renderer == null) return;

        if (duration <= 0f)
            duration = Mathf.Max(0.0001f, _defaultFadeSpeed);

        _fadeStart = _colorCache;
        _fadeTarget = target;
        _fadeDuration = duration;
        _fadeElapsed = 0f;

        // Se o fade target é praticamente igual ao atual, aplica direto.
        if (ColorsApproximatelyEqual(_fadeStart, _fadeTarget))
        {
            SetColor(_fadeTarget);
            _isFading = false;
            return;
        }

        _isFading = true;
    }

    /// <summary>
    /// Cancela qualquer fade em andamento.
    /// </summary>
    public void ForceStopFade()
    {
        _isFading = false;
    }
    #endregion

    #region Helpers (internos e performáticos)
    // Aplica cor via MaterialPropertyBlock para todos os materiais do renderer
    private void ApplyColorToRenderer(Color color)
    {
        if (_renderer == null) return;

        _mpb.Clear();
        _mpb.SetColor("_Color", color);

        // Pode haver múltiplos materiais; SetPropertyBlock aplica ao renderer inteiro sem instanciar materiais.
        _renderer.SetPropertyBlock(_mpb);
    }

    // Comparação rápida de cores — evita chamadas Mathf.Approximately repetidas.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ColorsApproximatelyEqual(Color a, Color b, float eps = 0.003f)
    {
        // Usa diferença euclidiana quadrada (mais rápida que 4 comparações)
        float dx = a.r - b.r;
        float dy = a.g - b.g;
        float dz = a.b - b.b;
        float dw = a.a - b.a;
        return (dx * dx + dy * dy + dz * dz + dw * dw) <= (eps * eps);
    }
    #endregion
}
