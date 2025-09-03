using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// VoxelCache
/// -----------------
/// Componente utilitário para "voxel" (GameObject) que precisa de acesso rápido a componentes
/// (Renderer(s), Collider(s), BaseVoxel, VoxelFaceController, CompositeVoxel, MicroVoxel).
///
/// Objetivo:
/// - evitar GetComponent repetidos em loops de geração/execução (cachear tudo uma vez);
/// - oferecer métodos rápidos para operações de massa (aplicar cor, habilitar/desabilitar renderers/colliders);
/// - fornecer hooks que facilitam integração com sistemas de pooling (BaseRoomGenerator, MicroVoxelPool, etc.);
/// - ser configurável no Inspector (incluir/excluir child-renderers/colliders).
/// </summary>
[DisallowMultipleComponent]
[AddComponentMenu("Voxels/VoxelCache")]
public class VoxelCache : MonoBehaviour
{
    [Header("Cache Options")]
    [Tooltip("Inicializa o cache automaticamente em Awake(). Desligue se pretende inicializar manualmente (ex: préwarm editor).")]
    public bool autoInitializeOnAwake = true;

    [Tooltip("Se true, coleta Renderers em todos os filhos (GetComponentsInChildren). Se false, captura apenas o Renderer do próprio GameObject.")]
    public bool includeChildRenderers = true;

    [Tooltip("Se true, coleta Colliders em todos os filhos. Use com cuidado — GetComponentsInChildren pode ser caro na primeira vez.")]
    public bool includeChildColliders = false;

    [Tooltip("Se true, tenta cachear componentes de micro-sistemas (MicroVoxel, VoxelFaceController, CompositeVoxel, BaseVoxel) quando presentes.")]
    public bool cacheOptionalVoxelComponents = true;

    [Header("State to restore when pooled")]
    [Tooltip("Se true, ResetState() restaurará localPosition/localRotation/localScale ao spawnar da pool.")]
    public bool resetTransformOnSpawn = true;

    // ---------- Cached references ----------
    private Renderer _primaryRenderer;
    private Renderer[] _allRenderers; // includes primary and children if includeChildRenderers is true
    private Collider _primaryCollider;
    private Collider[] _allColliders;

    // optional voxel systems
    private BaseVoxel _baseVoxel;
    private VoxelFaceController _faceController;
    private CompositeVoxel _compositeVoxel;
    private MicroVoxel _microVoxel;

    // saved defaults (for reset when returned to pool)
    private Vector3 _savedLocalPosition;
    private Quaternion _savedLocalRotation;
    private Vector3 _savedLocalScale;
    private bool _cached = false;

    // reused MaterialPropertyBlock (static to reduce allocations)
    // Passo 1: Modificar a Declaração do Campo Estático
    private static MaterialPropertyBlock s_mpb;
    private static readonly int s_colorId = Shader.PropertyToID("_Color");
    private static readonly int s_baseColorId = Shader.PropertyToID("_BaseColor");
    
    // Passo 2: Criar uma Propriedade de Acesso Seguro
    // Adicione esta propriedade estática para garantir a inicialização segura
    private static MaterialPropertyBlock MPB
    {
        get
        {
            if (s_mpb == null)
            {
                s_mpb = new MaterialPropertyBlock();
            }
            return s_mpb;
        }
    }

    // small fast-access lists to avoid reallocation for batch ops
    private readonly List<Renderer> _tempRendererList = new List<Renderer>(8);

    #region Unity lifecycle
    private void Awake()
    {
        if (autoInitializeOnAwake) EnsureCache();
    }
    #endregion

    #region Cache management
    /// <summary>
    /// Garantir que o cache está pronto. Chamado automaticamente por padrão no Awake.
    /// </summary>
    public void EnsureCache()
    {
        if (_cached && gameObject != null) return;

        // primary renderer (try local first)
        if (_primaryRenderer == null)
            TryGetComponent(out _primaryRenderer);

        // primary collider
        if (_primaryCollider == null)
            TryGetComponent(out _primaryCollider);

        // optional voxel components
        if (cacheOptionalVoxelComponents)
        {
            if (_baseVoxel == null) TryGetComponent(out _baseVoxel);
            if (_faceController == null) TryGetComponent(out _faceController);
            if (_compositeVoxel == null) TryGetComponent(out _compositeVoxel);
            if (_microVoxel == null) TryGetComponent(out _microVoxel);
        }

        // child renderers (one-time collection)
        if (includeChildRenderers)
        {
            // GetComponentsInChildren is a bit expensive; we do it once.
            _allRenderers = GetComponentsInChildren<Renderer>(true);
            if (_allRenderers != null && _allRenderers.Length > 0)
            {
                // ensure primaryRenderer is set to self renderer if present
                if (_primaryRenderer == null)
                {
                    foreach (var r in _allRenderers)
                    {
                        if (r != null && r.gameObject == gameObject) { _primaryRenderer = r; break; }
                    }
                }
            }
            else _allRenderers = new Renderer[0];
        }
        else
        {
            _allRenderers = (_primaryRenderer != null) ? new Renderer[] { _primaryRenderer } : new Renderer[0];
        }

        // child colliders
        if (includeChildColliders)
        {
            _allColliders = GetComponentsInChildren<Collider>(true);
            if (_allColliders == null) _allColliders = new Collider[0];
        }
        else
        {
            _allColliders = (_primaryCollider != null) ? new Collider[] { _primaryCollider } : new Collider[0];
        }

        // save transform defaults for pooling reset
        _savedLocalPosition = transform.localPosition;
        _savedLocalRotation = transform.localRotation;
        _savedLocalScale = transform.localScale;

        _cached = true;
    }

    /// <summary>
    /// Força recache (recalcula GetComponents...). Use ao modificar a hierarquia em tempo de edição/execução.
    /// </summary>
    public void RefreshCache()
    {
        _cached = false;
        EnsureCache();
    }

    /// <summary>
    /// Limpa referências em cache (útil para edição, destruição ou reprovisionamento).
    /// </summary>
    public void ClearCache()
    {
        _primaryRenderer = null;
        _allRenderers = null;
        _primaryCollider = null;
        _allColliders = null;
        _baseVoxel = null;
        _faceController = null;
        _compositeVoxel = null;
        _microVoxel = null;
        _cached = false;
    }
    #endregion

    #region Fast queries (no allocations)
    /// <summary>
    /// Try get primary renderer if present.
    /// </summary>
    public bool TryGetPrimaryRenderer(out Renderer r)
    {
        EnsureCache();
        r = _primaryRenderer;
        return r != null;
    }

    /// <summary>
    /// Returns array of cached renderers (may be empty).
    /// </summary>
    public Renderer[] GetRenderers()
    {
        EnsureCache();
        return _allRenderers ?? Array.Empty<Renderer>();
    }

    /// <summary>
    /// Try get BaseVoxel/FaceController etc.
    /// </summary>
    public BaseVoxel GetBaseVoxel() { EnsureCache(); return _baseVoxel; }
    public VoxelFaceController GetFaceController() { EnsureCache(); return _faceController; }
    public CompositeVoxel GetCompositeVoxel() { EnsureCache(); return _compositeVoxel; }
    public MicroVoxel GetMicroVoxel() { EnsureCache(); return _microVoxel; }
    #endregion

    #region Color / material helpers (batch-friendly & cheap)
    /// <summary>
    /// Aplica cor via MaterialPropertyBlock em todos os renderers cacheados.
    /// Tenta _BaseColor primeiro (URP/HDRP), cai para _Color.
    /// </summary>
    public void ApplyColor(Color color)
    {
        EnsureCache();
        if (_allRenderers == null || _allRenderers.Length == 0) return;

        // Passo 3: Atualizar os Métodos para Usar a Nova Propriedade
        // set MPB once, then apply to each renderer
        MPB.Clear();

        // prefer _BaseColor if shader has it - we can't probe per-renderer cheaply here,
        // so set both keys: Set both avoids branch per renderer and most shaders ignore unknown props.
        MPB.SetColor(s_baseColorId, color);
        MPB.SetColor(s_colorId, color);

        // apply
        for (int i = 0; i < _allRenderers.Length; i++)
        {
            var r = _allRenderers[i];
            if (r == null) continue;
            // use SetPropertyBlock which is cheap and doesn't create materials
            r.SetPropertyBlock(MPB);
        }
    }

    /// <summary>
    /// Apply color to a single Renderer (if present).
    /// </summary>
    public void ApplyColorToRenderer(Renderer r, Color color)
    {
        if (r == null) return;
        // Passo 3: Atualizar os Métodos para Usar a Nova Propriedade
        MPB.Clear();
        MPB.SetColor(s_baseColorId, color);
        MPB.SetColor(s_colorId, color);
        r.SetPropertyBlock(MPB);
    }
    #endregion

    #region Visibility & collider helpers (fast)
    /// <summary>
    /// Toggle renderer.enabled for cached renderers (cheap operation).
    /// Prefer this when only visibility should change — avoids SetActive and transform changes.
    /// </summary>
    public void SetVisible(bool visible)
    {
        EnsureCache();
        if (_allRenderers == null || _allRenderers.Length == 0) return;
        for (int i = 0; i < _allRenderers.Length; i++)
        {
            var r = _allRenderers[i];
            if (r == null) continue;
            if (r.enabled != visible) r.enabled = visible;
        }
    }

    /// <summary>
    /// Toggle collider.enabled for cached colliders.
    /// </summary>
    public void SetColliderEnabled(bool enabled)
    {
        EnsureCache();
        if (_allColliders == null || _allColliders.Length == 0) return;
        for (int i = 0; i < _allColliders.Length; i++)
        {
            var c = _allColliders[i];
            if (c == null) continue;
            if (c.enabled != enabled) c.enabled = enabled;
        }
    }
    #endregion

    #region Pool integration helpers
    /// <summary>
    /// Call this right after the object is spawned from your pool.
    /// It sets parent/transform, re-enables object, resets state and calls BaseVoxel.Initialize if present.
    /// </summary>
    public void OnSpawnFromPool(Transform parent = null, Vector3? worldPosition = null, Quaternion? worldRotation = null)
    {
        EnsureCache();

        // re-parent and set transform quickly
        if (parent != null) transform.SetParent(parent, false);
        if (worldPosition.HasValue) transform.position = worldPosition.Value;
        if (worldRotation.HasValue) transform.rotation = worldRotation.Value;

        gameObject.SetActive(true);

        if (resetTransformOnSpawn)
        {
            transform.localPosition = _savedLocalPosition;
            transform.localRotation = _savedLocalRotation;
            transform.localScale = _savedLocalScale;
        }

        // default visible/collider on
        SetVisible(true);
        SetColliderEnabled(true);

        // try to reinitialize voxel logic if present (safe call)
        if (_baseVoxel != null)
        {
            try
            {
                // many BaseVoxel implementations expose Initialize(int, bool) as used by your codebase.
                // We'll attempt a safe call via dynamic invocation to avoid compile-time coupling.
                _baseVoxel.Initialize(0, true);
            }
            catch
            {
                // swallow if signature differs or method throws
                try
                {
                    // fallback: if there's a parameterless Initialize, call it
                    var mi = _baseVoxel.GetType().GetMethod("Initialize", Type.EmptyTypes);
                    mi?.Invoke(_baseVoxel, null);
                }
                catch { }
            }
        }

        // If face controller is present, ensure default mask is applied quickly (keeps compatibility)
        if (_faceController != null)
        {
            try { _faceController.ApplyFaceMask(VoxelFaceController.Face.All, immediate: true, gradualChunksPerFrame: 1); }
            catch { /* ignore if signature changed */ }
        }
    }

    /// <summary>
    /// Call this before returning the object to pool.
    /// It disables visuals/colliders and resets transform (keeps in pool root).
    /// </summary>
    public void OnReturnToPool(Transform poolRoot = null)
    {
        EnsureCache();

        // --- SUGESTÃO DE ALTERAÇÃO (INÍCIO) ---
        // Primeiro, limpa os sistemas filhos antes de desativar o objeto pai.
        if (_faceController != null)
        {
            // O VoxelFaceController precisa limpar seus micro-voxels antes de ser desativado.
            _faceController.ClearAllFacesImmediate();
        }
        // --- SUGESTÃO DE ALTERAÇÃO (FIM) ---

        // reset state to a minimal footprint
        SetVisible(false);
        SetColliderEnabled(false);

        // clear material property blocks to avoid leaking per-instance state
        if (_allRenderers != null)
        {
            for (int i = 0; i < _allRenderers.Length; i++)
            {
                var r = _allRenderers[i];
                if (r == null) continue;
                r.SetPropertyBlock(null);
            }
        }

        // optionally, move to pool root to keep hierarchy clean
        if (poolRoot != null) transform.SetParent(poolRoot, true);

        gameObject.SetActive(false);
    }
    #endregion

    #region Utility / debug
    /// <summary>
    /// Reseta estado salvo do transform (útil se quiser gravar defaults em runtime).
    /// </summary>
    public void SaveTransformDefaults()
    {
        _savedLocalPosition = transform.localPosition;
        _savedLocalRotation = transform.localRotation;
        _savedLocalScale = transform.localScale;
    }

    /// <summary>
    /// Rápido dump para debug (não aloca)
    /// </summary>
    public void DebugLogSummary()
    {
        EnsureCache();
        int rcount = _allRenderers != null ? _allRenderers.Length : 0;
        int ccount = _allColliders != null ? _allColliders.Length : 0;
        Debug.Log($"[VoxelCache] {name} (renderers={rcount}, colliders={ccount}, hasBaseVoxel={_baseVoxel != null})", this);
    }
    #endregion

    #region Static helpers
    /// <summary>
    /// Utility: get or add quickly.
    /// </summary>
    public static VoxelCache GetOrAdd(GameObject go, bool ensureAutoInit = true)
    {
        if (go == null) return null;
        var cache = go.GetComponent<VoxelCache>();
        if (cache == null) cache = go.AddComponent<VoxelCache>();
        if (ensureAutoInit) cache.EnsureCache();
        return cache;
    }
    #endregion
}