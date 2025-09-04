// VoxelFaceController.cs
// Substitui/estende a ideia do CompositeVoxel para suportar geração dinâmica de faces
// via MicroVoxels, pooling interno, e compatibilidade com "grupos de face" preexistentes.
//
// Principais características:
// - Compatível com API antiga: expõe enum Face e ApplyFaceMask(Face).
// - Dois modos automáticos: RendererGroups (se houver GameObject groups atribuídos) ou MicroVoxels.
// - MicroVoxelPool interno (prewarm opcional) evitando Instantiate/Destroy massivos.
// - Geração gradual (coroutine) para evitar spikes; opção de "immediate" também disponível.
// - Métodos para liberar/reusar microvoxels, limpar faces, obter máscara atual.
// - Integração com BaseVoxel (override OnInitialize) e com o sistema de colisores/isSolid.
//
// Requisitos:
// - MicroVoxel prefab (campo microVoxelPrefab) — idealmente o prefab do seu MicroVoxel.cs.
// - BaseVoxel deve existir no projeto (já presente nos outros scripts).
//
// Uso:
// - Substitua o CompositeVoxel no prefab do VoxelFundamental por este componente.
// - Configure inspector: microVoxelPrefab, gridResolution, generateMode, generationPerFrame, etc.
// - O BaseRoomGenerator deve chamar OnInitialize (herdado de BaseVoxel) ou ApplyFaceMask diretamente.
// 
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public class VoxelFaceController : BaseVoxel
{
    #region Face enum & compatibility (matches CompositeVoxel)
    [Flags]
    public enum Face : byte
    {
        None   = 0,
        Top    = 1 << 0,
        Bottom = 1 << 1,
        North  = 1 << 2,
        South  = 1 << 3,
        East   = 1 << 4,
        West   = 1 << 5,
        All    = Top | Bottom | North | South | East | West
    }
    #endregion

    #region Inspector fields - configuration
    public enum GenerationMode : byte
    {
        AutoDetect = 0,     // usa grupos de renderers se atribuídos, senão usa MicroVoxels
        RendererGroups = 1, // usa GameObject groups (compatibilidade com CompositeVoxel)
        MicroVoxels = 2     // gera microvoxels dinamicamente
    }

    [Header("Mode")]
    [Tooltip("Modo de geração. AutoDetect prefere grupos de face se estiverem setados.")]
    public GenerationMode generationMode = GenerationMode.AutoDetect;

    [Header("Renderer Groups (compatibility)")]
    [Tooltip("Se você migrar de CompositeVoxel e usar grupos já prontos, atribua-os aqui.")]
    public GameObject topFaceGroup;
    public GameObject bottomFaceGroup;
    public GameObject northFaceGroup;
    public GameObject southFaceGroup;
    public GameObject eastFaceGroup;
    public GameObject westFaceGroup;

    [Header("MicroVoxel Generation")]
    [Tooltip("Prefab do MicroVoxel (recomendado: o MicroVoxel.cs que você já tem).")]
    public GameObject microVoxelPrefab;

    [Tooltip("Número de micro-voxels por eixo (ex: 4 => 4x4 por face).")]
    [Min(1)] public int microGridResolution = 4;

    [Tooltip("Tamanho local de cada microvoxel (relativo ao voxel).")]
    public float microVoxelLocalSize = 0.25f;

    [Tooltip("Se true, a geração será distribuída ao longo de vários frames para suavizar o custo.")]
    public bool generateGradually = true;

    [Tooltip("Quantos microvoxels ativar por frame quando generateGradually=true.")]
    [Min(1)] public int generationPerFrame = 64;

    [Tooltip("Tempo (segundos) de escurecimento / efeito de aparição quando microvoxels aparecem. 0 = sem efeito.")]
    public float appearAnimationDuration = 0.12f;

    [Header("Pooling (microvoxels)")]
    [Tooltip("Pre-warm pool com essa quantidade por face quando Awake. 0 = não pré-aquecer.")]
    public int prewarmPoolPerFace = 0;

    [Header("Debug / Perf")]
    [Tooltip("Se true, logs verbosos para debug.")]
    public bool verboseDebug = false;
    #endregion

    #region Internals & caches
    // Flag to ensure initialization runs only once
    private bool _isInitialized = false;

    // Renderer groups cache (compatibility)
    private Renderer[][] _renderersByGroup = new Renderer[6][];
    private bool[] _groupEnabledCache = new bool[6];

    // active mask
    private Face _currentMask = Face.None;

    // microvoxel data structures: per face list of active instances (pool instances)
    private readonly List<GameObject>[] _activeMicroVoxelsByFace = new List<GameObject>[6];

    // coroutine handle to avoid multi-start
    private Coroutine _generationCoroutine;

    // Mapping order same as CompositeVoxel: Top, Bottom, North, South, East, West
    private static readonly Face[] _faceOrder = new[] {
        Face.Top, Face.Bottom, Face.North, Face.South, Face.East, Face.West
    };

    // accessors for face groups (inspector order)
    private GameObject[] _faceGroupGameObjects => new[] {
        topFaceGroup, bottomFaceGroup, northFaceGroup, southFaceGroup, eastFaceGroup, westFaceGroup
    };

    // MicroVoxelPool singleton (internal)
    private static class MicroVoxelPool
    {
        private static readonly Dictionary<int, Queue<GameObject>> _pool = new Dictionary<int, Queue<GameObject>>();
        private static Transform _root;

        private static Transform Root
        {
            get
            {
                if (_root == null)
                {
                    var go = new GameObject("MicroVoxelPool");
                    go.hideFlags = HideFlags.DontSaveInBuild | HideFlags.HideInHierarchy;
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    _root = go.transform;
                }
                return _root;
            }
        }

        public static GameObject Acquire(GameObject prefab)
        {
            if (prefab == null) return null;
            int id = prefab.GetInstanceID();
            if (_pool.TryGetValue(id, out var q) && q.Count > 0)
            {
                var go = q.Dequeue();
                if (go == null) return UnityEngine.Object.Instantiate(prefab);
                return go;
            }
            return UnityEngine.Object.Instantiate(prefab);
        }

        public static void Release(GameObject prefab, GameObject instance)
        {
            if (prefab == null || instance == null) { if (instance != null) UnityEngine.Object.Destroy(instance); return; }
            instance.SetActive(false);
            instance.transform.SetParent(Root, true);
            int id = prefab.GetInstanceID();
            if (!_pool.TryGetValue(id, out var q))
            {
                q = new Queue<GameObject>();
                _pool[id] = q;
            }
            q.Enqueue(instance);
        }

        public static void Prewarm(GameObject prefab, int count)
        {
            if (prefab == null || count <= 0) return;
            int id = prefab.GetInstanceID();
            if (!_pool.TryGetValue(id, out var q))
            {
                q = new Queue<GameObject>();
                _pool[id] = q;
            }
            for (int i = 0; i < count; i++)
            {
                var go = UnityEngine.Object.Instantiate(prefab);
                go.SetActive(false);
                go.transform.SetParent(Root, true);
                q.Enqueue(go);
            }
        }
    }
    #endregion

    #region Unity lifecycle
    private void OnEnable()
    {
        // nothing special; microvoxels are activated when faces are requested
    }

    private void OnDisable()
    {
        EnsureInitialized();
        // ensure all microvoxels are returned if object disabled
        ClearAllFacesImmediate();
    }
    #endregion
    
    #region Initialization
    /// <summary>
    /// Ensures all internal lists and caches are ready. Called lazily before use.
    /// </summary>
    private void EnsureInitialized()
    {
        if (_isInitialized) return;

        // A CORREÇÃO ESTÁ AQUI: Garantimos que o loop vai de 0 a 5 (i < 6).
        // Adicionamos também uma verificação extra de segurança.
        for (int i = 0; i < 6; i++) 
        {
            // Se a lista naquela posição ainda não existir, crie uma nova.
            if (_activeMicroVoxelsByFace[i] == null)
            {
                _activeMicroVoxelsByFace[i] = new List<GameObject>();
            }
        }

        // build renderer cache if groups present
        BuildRendererCache();

        // optionally prewarm microvoxel pool (per-face)
        if (microVoxelPrefab != null && prewarmPoolPerFace > 0)
        {
            int perPrefab = prewarmPoolPerFace;
            MicroVoxelPool.Prewarm(microVoxelPrefab, perPrefab * 6);
            if (verboseDebug) Debug.Log($"[VoxelFaceController] Prewarmed microvoxel pool: {perPrefab * 6} instances.");
        }

        _isInitialized = true;
    }
    #endregion

    #region Renderer group compatibility
    private void BuildRendererCache()
    {
        // if groups not assigned, arrays stay empty
        var groups = _faceGroupGameObjects;
        for (int i = 0; i < 6; i++)
        {
            var go = groups[i];
            if (go != null)
                _renderersByGroup[i] = go.GetComponentsInChildren<Renderer>(true);
            else
                _renderersByGroup[i] = new Renderer[0];

            _groupEnabledCache[i] = false;
        }
    }
    #endregion

    #region Public API (face mask)
    /// <summary>
    /// Aplica máscara de faces. Mantive a assinatura compatível para facilitar substituição de CompositeVoxel.
    /// </summary>
    public void ApplyFaceMask(Face mask)
    {
        ApplyFaceMask(mask, immediate: !generateGradually, gradualChunksPerFrame: generationPerFrame);
    }

    /// <summary>
    /// Aplica máscara com controle fino.
    /// - immediate: se true, gera todas as microvoxels imediatamente (pode causar spike).
    /// - gradualChunksPerFrame: quantos microvoxels processar por frame quando gerar gradualmente.
    /// </summary>
    public void ApplyFaceMask(Face mask, bool immediate, int gradualChunksPerFrame = 64)
    {
        EnsureInitialized();
        if (_currentMask == mask) return;

        // Decide qual modo usar
        var mode = DetermineMode();

        // If using renderer groups (compat mode)
        if (mode == GenerationMode.RendererGroups)
        {
            ApplyMaskToRendererGroups(mask);
            // release any microvoxels if present (we're switching to renderer groups)
            ClearAllMicroVoxelsImmediate();
            _currentMask = mask;
            return;
        }

        // Mode MicroVoxels
        // compute which faces should be enabled / disabled
        for (int i = 0; i < _faceOrder.Length; i++)
        {
            var face = _faceOrder[i];
            bool shouldEnable = (mask & face) != 0;
            bool wasEnabled = (_currentMask & face) != 0;

            if (shouldEnable && !wasEnabled)
            {
                // need to generate face
                if (_generationCoroutine != null) StopCoroutine(_generationCoroutine);
                if (immediate || !generateGradually)
                    GenerateFaceImmediate(i);
                else
                    _generationCoroutine = StartCoroutine(GenerateFaceGradual(i, gradualChunksPerFrame));
            }
            else if (!shouldEnable && wasEnabled)
            {
                // need to remove face
                if (_generationCoroutine != null) StopCoroutine(_generationCoroutine);
                // immediate removal is cheap (return to pool)
                RemoveFaceImmediate(i);
            }
            // if no change skip
        }

        _currentMask = mask;
    }

    public Face GetCurrentMask() => _currentMask;
    #endregion

    #region Generation helpers (RendererGroups)
    private void ApplyMaskToRendererGroups(Face mask)
    {
        for (int i = 0; i < _renderersByGroup.Length; i++)
        {
            var renderers = _renderersByGroup[i];
            if (renderers == null || renderers.Length == 0) continue;
            bool shouldEnable = (_faceOrder[i] & mask) != 0;
            if (_groupEnabledCache[i] == shouldEnable) continue;
            for (int j = 0; j < renderers.Length; j++)
            {
                var r = renderers[j];
                if (r == null) continue;
                r.enabled = shouldEnable;
            }
            _groupEnabledCache[i] = shouldEnable;
        }
    }
    #endregion

    #region Generation helpers (MicroVoxels)
    // immediate generation: spawn every microvoxel synchronously
    private void GenerateFaceImmediate(int faceIndex)
    {
        if (microVoxelPrefab == null)
        {
            Debug.LogWarning($"[VoxelFaceController] microVoxelPrefab not set; cannot generate face {faceIndex} as microvoxels.", this);
            return;
        }

        if (_activeMicroVoxelsByFace[faceIndex].Count > 0) return;

        // Build grid coordinates for that face
        var grid = BuildFaceGrid(faceIndex);
        for (int i = 0; i < grid.Count; i++)
        {
            var info = grid[i];
            var mv = MicroVoxelPool.Acquire(microVoxelPrefab);

            mv.transform.SetParent(this.transform, true);
            mv.transform.localPosition = info.localPos;
            mv.transform.localRotation = info.localRot;
            mv.transform.localScale = Vector3.one * microVoxelLocalSize;
            mv.SetActive(true);

            // try to configure MicroVoxel if present
            var mComp = mv.GetComponent<MicroVoxel>();
            if (mComp != null)
            {
                // ensure initialization and reset to default (calls OnInitialize indirectly)
                mComp.ForceSetColor(mComp != null ? mCompForceColorSafe(mComp) : Color.white);
            }

            // optional appear animation: scale from 0 to target
            if (appearAnimationDuration > 0f)
            {
                mv.transform.localScale = Vector3.zero;
                StartCoroutine(ScaleOverTime(mv.transform, Vector3.one * microVoxelLocalSize, appearAnimationDuration));
            }

            _activeMicroVoxelsByFace[faceIndex].Add(mv);
        }
    }

    // safe fallback color for MicroVoxel.Configure/ForceSetColor usage avoidance of nulls
    private Color mCompForceColorSafe(MicroVoxel comp)
    {
        try
        {
            // try to access baseColor via reflection? but MicroVoxel doesn't expose baseColor public.
            // Keep safe: return white (caller may later call Configure via MicroVoxelManager).
            return Color.white;
        }
        catch
        {
            return Color.white;
        }
    }

    // gradual generation using coroutine to avoid spikes
    private IEnumerator GenerateFaceGradual(int faceIndex, int perFrame)
    {
        if (microVoxelPrefab == null)
        {
            Debug.LogWarning($"[VoxelFaceController] microVoxelPrefab not set; cannot generate face {faceIndex} as microvoxels.", this);
            yield break;
        }

        if (_activeMicroVoxelsByFace[faceIndex].Count > 0) yield break;

        var grid = BuildFaceGrid(faceIndex);
        int processed = 0;
        for (int i = 0; i < grid.Count; i++)
        {
            var info = grid[i];

            var mv = MicroVoxelPool.Acquire(microVoxelPrefab);
            mv.transform.SetParent(this.transform, true);
            mv.transform.localPosition = info.localPos;
            mv.transform.localRotation = info.localRot;
            mv.transform.localScale = Vector3.one * microVoxelLocalSize;
            mv.SetActive(true);

            var mComp = mv.GetComponent<MicroVoxel>();
            if (mComp != null)
            {
                mComp.ForceSetColor(mCompForceColorSafe(mComp));
            }

            if (appearAnimationDuration > 0f)
            {
                mv.transform.localScale = Vector3.zero;
                StartCoroutine(ScaleOverTime(mv.transform, Vector3.one * microVoxelLocalSize, appearAnimationDuration));
            }

            _activeMicroVoxelsByFace[faceIndex].Add(mv);

            processed++;
            if (processed >= perFrame)
            {
                processed = 0;
                yield return null;
            }
        }

        _generationCoroutine = null;
    }

    // build list of positions & rotations for microvoxels for a face
    private struct MicroPosInfo { public Vector3 localPos; public Quaternion localRot; }

    private List<MicroPosInfo> BuildFaceGrid(int faceIndex)
    {
        var list = new List<MicroPosInfo>(microGridResolution * microGridResolution);

        // face plane extents in local space (voxel local extent assumed 1 unit cube centered at origin)
        // we'll position microvoxels offset to be on the face plane (outer face).
        // Assumptions: the voxel's size in world is handled by the transform scale of the VoxelFundamental (BaseRoomGenerator)
        // local coordinate conventions: cube centered at (0,0,0). For face alignment we offset by 0.5 along axis.

        float half = 0.5f;
        float cell = 1f / microGridResolution;
        float microHalf = cell * 0.5f;

        // sample positions in face local 2D coordinates (u,v) from -0.5+microHalf .. +0.5-microHalf
        float start = -half + microHalf;
        for (int iy = 0; iy < microGridResolution; iy++)
        {
            for (int ix = 0; ix < microGridResolution; ix++)
            {
                float u = start + ix * cell;
                float v = start + iy * cell;
                Vector3 pos = Vector3.zero;
                Quaternion rot = Quaternion.identity;

                switch (faceIndex)
                {
                    case 0: // Top (+Y) : plane facing up; microvoxels should face upward, sit at y = +0.5
                        pos = new Vector3(u, +half, v);
                        rot = Quaternion.Euler(0f, 0f, 0f);
                        break;
                    case 1: // Bottom (-Y)
                        pos = new Vector3(u, -half, -v); // flip v so normals face down
                        rot = Quaternion.Euler(180f, 0f, 0f);
                        break;
                    case 2: // North (+Z) (depends on your world; this matches CompositeVoxel mapping)
                        pos = new Vector3(u, v, +half);
                        rot = Quaternion.Euler(90f, 0f, 0f);
                        break;
                    case 3: // South (-Z)
                        pos = new Vector3(-u, v, -half);
                        rot = Quaternion.Euler(-90f, 0f, 0f);
                        break;
                    case 4: // East (+X)
                        pos = new Vector3(+half, v, -u);
                        rot = Quaternion.Euler(0f, 0f, -90f);
                        break;
                    case 5: // West (-X)
                        pos = new Vector3(-half, v, u);
                        rot = Quaternion.Euler(0f, 0f, 90f);
                        break;
                }

                // scale to microVoxelLocalSize later (we set localScale of instance)
                list.Add(new MicroPosInfo { localPos = pos, localRot = rot });
            }
        }

        return list;
    }

    // Removes microvoxels for a face immediately (returns to pool)
    private void RemoveFaceImmediate(int faceIndex)
    {
        var list = _activeMicroVoxelsByFace[faceIndex];
        if (list == null || list.Count == 0) return;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var inst = list[i];
            if (inst == null) continue;
            MicroVoxelPool.Release(microVoxelPrefab, inst);
        }
        list.Clear();
    }

    private void ClearAllMicroVoxelsImmediate()
    {
        for (int i = 0; i < 6; i++) RemoveFaceImmediate(i);
    }

    // helper coroutine for scale animation
    private IEnumerator ScaleOverTime(Transform t, Vector3 target, float dur)
    {
        if (t == null) yield break;
        float elapsed = 0f;
        var start = t.localScale;
        while (elapsed < dur)
        {
            elapsed += Time.deltaTime;
            float k = Mathf.Clamp01(elapsed / dur);
            t.localScale = Vector3.Lerp(start, target, k);
            yield return null;
        }
        t.localScale = target;
    }

    // Immediately clear faces and microvoxels (used on disable)
    private void ClearAllFacesImmediate()
    {
        // disable renderer groups
        for (int i = 0; i < _groupEnabledCache.Length; i++)
        {
            if (_renderersByGroup[i] == null) continue;
            if (_groupEnabledCache[i])
            {
                var renderers = _renderersByGroup[i];
                for (int j = 0; j < renderers.Length; j++)
                    if (renderers[j] != null) renderers[j].enabled = false;
                _groupEnabledCache[i] = false;
            }
        }

        // release microvoxels
        ClearAllMicroVoxelsImmediate();
        _currentMask = Face.None;
    }
    #endregion

    #region Mode detection utility
    private GenerationMode DetermineMode()
    {
        if (generationMode == GenerationMode.AutoDetect)
        {
            // prefer renderer groups if any assigned
            foreach (var g in _faceGroupGameObjects)
                if (g != null) return GenerationMode.RendererGroups;
            return GenerationMode.MicroVoxels;
        }
        return generationMode;
    }
    #endregion

    #region BaseVoxel integration
    // Called by BaseRoomGenerator (or external systems) to initialize voxel type / solidity
    protected override void OnInitialize(VoxelType type, bool isSolid)
    {
        // apply defaults similar to CompositeVoxel
        Face defaultMask = GetDefaultMaskForType(type);
        // apply mask; immediate and no gradual for initialization to keep semantics
        ApplyFaceMask(defaultMask, immediate: true);

        // set collider state if present
        var col = GetComponent<Collider>();
        if (col != null && col.enabled != isSolid) col.enabled = isSolid;
    }

    private Face GetDefaultMaskForType(VoxelType type)
    {
        switch (type)
        {
            case VoxelType.Floor:      return Face.Top;
            case VoxelType.Ceiling:    return Face.Bottom;
            case VoxelType.Wall_North: return Face.North;
            case VoxelType.Wall_South: return Face.South;
            case VoxelType.Wall_East:  return Face.East;
            case VoxelType.Wall_West:  return Face.West;
            case VoxelType.Pillar:     return Face.All;
            case VoxelType.ClosedBox:  return Face.All;
            case VoxelType.Empty:      return Face.None;
            default:                   return Face.Top;
        }
    }
    #endregion

    #region Debug helpers
    private void LogV(string msg)
    {
        if (verboseDebug) Debug.Log($"[VoxelFaceController] {msg}", this);
    }
    #endregion
}
