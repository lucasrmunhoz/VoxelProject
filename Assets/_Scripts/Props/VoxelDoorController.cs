using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// VoxelDoorController (UPDATED)
/// - Compatível com os novos utilitários do projeto (VoxelCache, PoolableObject, BaseRoomGenerator).
/// - Porta construída a partir de voxels 1x1x1 (o BaseRoomGenerator deve instanciar cubos 1x1x1 nas posições de porta
///   e depois chamar Initialize(voxels, animateOnSetup:false)).
/// - Quando a porta abre/fecha via interação, os voxels executam UMA animação combinada de rotação (eixos) + encolhimento
///   (quando desaparecem) e o inverso quando aparecem. **Importante:** durante a geração inicial da sala (BaseRoomGenerator)
///   os voxels NÃO devem animar — passe animateOnSetup=false para Initialize para garantir isso.
/// - Implementação otimizada: apenas UMA coroutine por porta gerencia todas as animações (não inicia N coroutines por voxel).
/// - Usa VoxelCache para operações de render/collider/color para evitar GetComponent repetidos.
/// 
/// OBS: Para integrar corretamente, o BaseRoomGenerator deve:
///  1) Ao preencher "buracos de portas", instanciar voxels como cubos 1x1x1 (p.ex. usando voxelFundamentalPrefab com escala 1).
///  2) Agrupar esses GameObjects e chamar `voxelDoorController.Initialize(listOfVoxels, animateOnSetup:false)`
///     imediatamente após a criação (sem animações de aparecimento).
///  3) Se o BaseRoomGenerator usar pooling, garanta que ele chame VoxelCache.OnSpawnFromPool (ou use VoxelCache.GetOrAdd) no spawn.
/// 
/// Se quiser, eu posso gerar o diff do BaseRoomGenerator para preencher automaticamente as portas com cubos 1x1x1 e chamar Initialize(...).
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class VoxelDoorController : MonoBehaviour, IInteractable
{
    // Representa um voxel "com cache" e estado imutável salvo para animação.
    private class DoorVoxelInfo
    {
        public Transform transform;
        public VoxelCache cache; // cached helper
        public Vector3 originalLocalPos;
        public Quaternion originalLocalRot;
        public Vector3 originalLocalScale;
        public bool wasActiveAtInit;
    }

    [Header("Animation")]
    [SerializeField, Min(0.05f)] private float _animationDuration = 0.9f;
    [SerializeField, Min(0f)] private float _staggerDelay = 0.03f; // delay between voxel starts (wave)
    [SerializeField] private AnimationCurve _animCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);
    [SerializeField, Range(0f, 360f)] private float _rotationAmount = 180f; // degrees max for random axis rotation

    [Header("Interaction")]
    [SerializeField] private bool _startLocked = true;

    [Header("Audio")]
    [SerializeField] private AudioClip _openSound;
    [SerializeField] private AudioClip _closeSound;
    [SerializeField] private AudioClip _lockedSound;
    [SerializeField] private AudioClip _unlockSound;

    [Header("Focus colors (uses VoxelCache.ApplyColor)")]
    [SerializeField] private Color _defaultColor = Color.gray;
    [SerializeField] private Color _focusColor = Color.yellow;
    [SerializeField] private Color _lockedColor = new Color(0.8f, 0.2f, 0.2f);

    // internal state
    private readonly List<DoorVoxelInfo> _voxels = new List<DoorVoxelInfo>(32);
    private bool _isOpen = false;
    private bool _isLocked = false;
    private bool _isAnimating = false;
    private AudioSource _audio;
    private Coroutine _animationCoroutine;

    // small optimization: precompute random rotation targets per voxel when an animation starts
    private readonly List<Quaternion> _targetRotations = new List<Quaternion>(32);
    private readonly List<Vector3> _startScales = new List<Vector3>(32);
    private readonly List<Vector3> _endScales = new List<Vector3>(32);
    private readonly List<float> _voxelStartTimes = new List<float>(32);

    #region Unity lifecycle
    private void Awake()
    {
        _audio = GetComponent<AudioSource>();
        _isLocked = _startLocked;

        // ensure container has a trigger collider for interaction focus (cheap)
        if (GetComponent<Collider>() == null)
        {
            var col = gameObject.AddComponent<BoxCollider>();
            col.isTrigger = true;
        }
    }

    private void OnEnable()
    {
        // reset to closed/initial visual state
        if (_animationCoroutine != null)
        {
            StopCoroutine(_animationCoroutine);
            _animationCoroutine = null;
        }
        _isAnimating = false;
        _isOpen = false; // by default door starts closed; BaseRoomGenerator may call SetOpen later if desired
        RestoreAllVoxelsImmediate();
    }
    #endregion

    #region Public API (called by BaseRoomGenerator or GameFlowManager)
    /// <summary>
    /// Initialize the door with the list of voxel GameObjects that compose it.
    /// IMPORTANT: voxels should already be positioned (localPosition/localRotation/localScale) relative to this container.
    /// animateOnSetup=false => do not animate on initial setup (recommended when BaseRoomGenerator places them).
    /// </summary>
    public void Initialize(List<GameObject> voxelGOs, bool animateOnSetup = false)
    {
        _voxels.Clear();
        _targetRotations.Clear();
        _startScales.Clear();
        _endScales.Clear();
        _voxelStartTimes.Clear();

        if (voxelGOs == null || voxelGOs.Count == 0) return;

        // gather voxel infos (use VoxelCache to avoid expensive GetComponent later)
        for (int i = 0; i < voxelGOs.Count; i++)
        {
            var go = voxelGOs[i];
            if (go == null) continue;

            // Ensure VoxelCache exists and is initialized (safe call)
            var cache = VoxelCache.GetOrAdd(go, ensureAutoInit: true);

            var info = new DoorVoxelInfo
            {
                transform = go.transform,
                cache = cache,
                originalLocalPos = go.transform.localPosition,
                originalLocalRot = go.transform.localRotation,
                originalLocalScale = go.transform.localScale,
                wasActiveAtInit = go.activeSelf
            };

            // ensure consistent initial state: closed => voxels active and original transforms
            go.SetActive(info.wasActiveAtInit);

            _voxels.Add(info);
        }

        // update collider bounds to encompass voxels (if BoxCollider exists on container)
        UpdateContainerCollider();

        // If animateOnSetup == false we leave voxels exactly as they are (no animation).
        // If animateOnSetup == true, we perform an appear animation (grow+rotate) once.
        if (animateOnSetup && !_isAnimating)
        {
            // Spawn an appear animation but do not mark door as "closed/open" incorrectly.
            // treat as "close" (voxels appear)
            if (_animationCoroutine != null) StopCoroutine(_animationCoroutine);
            _animationCoroutine = StartCoroutine(AnimateDoor_internal(opening: false, triggeredFromInit: true));
        }
    }

    // =======================================================================
    // INÍCIO DA ALTERAÇÃO SUGERIDA
    // =======================================================================
    /// <summary>
    /// Define os clipes de áudio programaticamente. Ideal para ser chamado pelo BaseRoomGenerator
    /// após adicionar este componente, evitando o uso de Reflection.
    /// </summary>
    public void SetAudioClips(AudioClip open, AudioClip close, AudioClip locked)
    {
        this._openSound = open;
        this._closeSound = close;
        this._lockedSound = locked;
    }
    // =======================================================================
    // FIM DA ALTERAÇÃO SUGERIDA
    // =======================================================================

    /// <summary>
    /// Opens the door (voxels disappear with rotation+shrink animation).
    /// </summary>
    public void Open()
    {
        SetOpen(true);
    }

    /// <summary>
    /// Closes the door (voxels appear with rotation+grow animation).
    /// </summary>
    public void Close()
    {
        SetOpen(false);
    }

    /// <summary>
    /// Lock/unlock
    /// </summary>
    public void Unlock()
    {
        if (!_isLocked) return;
        _isLocked = false;
        if (_audio != null && _unlockSound != null) _audio.PlayOneShot(_unlockSound);
    }

    public void SetOpen(bool open)
    {
        if (_isAnimating) return; // ignore while animating
        if (_isOpen == open) return;

        if (_isLocked)
        {
            if (_audio != null && _lockedSound != null) _audio.PlayOneShot(_lockedSound);
            return;
        }

        if (_animationCoroutine != null) StopCoroutine(_animationCoroutine);
        _animationCoroutine = StartCoroutine(AnimateDoor_internal(opening: open, triggeredFromInit: false));
    }
    #endregion

    #region IInteractable
    public string InteractionPrompt => _isLocked ? "Trancada" : (_isOpen ? "Fechar Porta" : "Abrir Porta");

    public bool Interact(GameObject interactor)
    {
        if (_isAnimating) return false;
        if (_isLocked)
        {
            if (_audio != null && _lockedSound != null) _audio.PlayOneShot(_lockedSound);
            return false;
        }
        SetOpen(!_isOpen);
        return true;
    }

    public void OnFocusEnter()
    {
        ApplyColorToAll(_isLocked ? _lockedColor : _focusColor);
    }

    public void OnFocusExit()
    {
        ApplyColorToAll(_defaultColor);
    }
    #endregion

    #region Animation core (single coroutine managing all voxels)
    // opening == true => voxels will disappear (shrink to zero + rotate)
    // opening == false => voxels will appear (grow from zero + rotate to original)
    // triggeredFromInit==true => this was invoked from Initialize(animateOnSetup:true) and should not play audio
    private IEnumerator AnimateDoor_internal(bool opening, bool triggeredFromInit)
    {
        if (_voxels.Count == 0) yield break;

        _isAnimating = true;

        // prepare audio
        if (_audio != null && !triggeredFromInit)
        {
            var clip = opening ? _openSound : _closeSound;
            if (clip != null) _audio.PlayOneShot(clip);
        }

        // Ensure voxels are active when starting an appear animation
        if (!opening)
        {
            // for appear animation, ensure voxels start inactive (scale=0) and then appear
            for (int i = 0; i < _voxels.Count; i++)
            {
                var v = _voxels[i];
                if (v == null || v.transform == null) continue;
                v.transform.gameObject.SetActive(true);
                // set to zero scale start for appear effect
                v.transform.localScale = Vector3.zero;
            }
        }

        // Precompute per-voxel timing and rotation targets to avoid random calls during the tight loop.
        _targetRotations.Clear();
        _startScales.Clear();
        _endScales.Clear();
        _voxelStartTimes.Clear();

        float now = Time.time;
        for (int i = 0; i < _voxels.Count; i++)
        {
            // stagger: when opening we may want to go in reverse order for natural wave
            int index = opening ? i : i; // order will be managed below by start time formula if needed
            // compute start time offset so voxels are staggered
            float startOffset = i * _staggerDelay;
            _voxelStartTimes.Add(now + startOffset);

            var v = _voxels[i];
            // compute rotation target: random axis aligned-ish rotation but deterministic-ish per voxel
            var axis = UnityEngine.Random.onUnitSphere;
            var rotDelta = Quaternion.AngleAxis(UnityEngine.Random.Range(0f, _rotationAmount), axis);
            _targetRotations.Add(v.originalLocalRot * rotDelta);

            if (opening)
            {
                _startScales.Add(v.originalLocalScale);
                _endScales.Add(Vector3.zero);
            }
            else
            {
                // appearing: start 0 -> original
                _startScales.Add(Vector3.zero);
                _endScales.Add(v.originalLocalScale);
            }
        }

        float startTime = Time.time;
        float totalDuration = _animationDuration + (_voxels.Count * _staggerDelay) + 0.05f;

        // We'll drive animation until the last voxel finished
        float animationEndTime = startTime + _animationDuration + (_voxels.Count - 1) * _staggerDelay;

        // Main animation loop
        while (Time.time <= animationEndTime + 0.01f)
        {
            float tNow = Time.time;

            // For each voxel we evaluate its normalized progress [0..1] based on its start time and duration.
            for (int i = 0; i < _voxels.Count; i++)
            {
                var v = _voxels[i];
                if (v == null || v.transform == null) continue;

                float voxelStart = _voxelStartTimes[i];
                float elapsed = tNow - voxelStart;
                float progress = Mathf.Clamp01(elapsed / _animationDuration);
                float eased = _animCurve.Evaluate(progress);

                // scale lerp
                Vector3 s = Vector3.LerpUnclamped(_startScales[i], _endScales[i], eased);
                v.transform.localScale = s;

                // rotation slerp towards (or from) target depending on opening/closing
                if (opening)
                {
                    // opening: rotate from original -> target
                    v.transform.localRotation = Quaternion.Slerp(v.originalLocalRot, _targetRotations[i], eased);
                }
                else
                {
                    // closing (appear): rotate from target -> original
                    v.transform.localRotation = Quaternion.Slerp(_targetRotations[i], v.originalLocalRot, eased);
                }

                // Note: position remains originalLocalPos (we don't translate voxels)
            }

            yield return null;
        }

        // Finalize - ensure exact end state
        for (int i = 0; i < _voxels.Count; i++)
        {
            var v = _voxels[i];
            if (v == null || v.transform == null) continue;

            if (opening)
            {
                v.transform.localScale = Vector3.zero;
                v.transform.localRotation = _targetRotations[i];
                // deactivate voxel to remove from scene/physics (keeps pooled)
                v.transform.gameObject.SetActive(false);
            }
            else
            {
                v.transform.localScale = v.originalLocalScale;
                v.transform.localRotation = v.originalLocalRot;
                v.transform.gameObject.SetActive(true);
            }
        }

        _isOpen = opening;
        _isAnimating = false;
        _animationCoroutine = null;
    }
    #endregion

    #region Helpers
    private void UpdateContainerCollider()
    {
        // compute bounds of voxels in local space
        if (TryGetComponent<BoxCollider>(out var bc))
        {
            if (_voxels.Count == 0)
            {
                bc.center = Vector3.zero;
                bc.size = Vector3.one * 0.1f;
                return;
            }

            Bounds b = new Bounds(_voxels[0].originalLocalPos, _voxels[0].originalLocalScale);
            for (int i = 1; i < _voxels.Count; i++)
            {
                b.Encapsulate(new Bounds(_voxels[i].originalLocalPos, _voxels[i].originalLocalScale));
            }

            bc.center = b.center;
            // small padding
            bc.size = b.size + Vector3.one * 0.1f;
            bc.isTrigger = true;
        }
    }

    private void RestoreAllVoxelsImmediate()
    {
        // restore to closed state: all voxels active and original transforms
        for (int i = 0; i < _voxels.Count; i++)
        {
            var v = _voxels[i];
            if (v == null || v.transform == null) continue;
            v.transform.localPosition = v.originalLocalPos;
            v.transform.localRotation = v.originalLocalRot;
            v.transform.localScale = v.originalLocalScale;
            v.transform.gameObject.SetActive(v.wasActiveAtInit);
        }
    }

    private void ApplyColorToAll(Color c)
    {
        // Use VoxelCache.ApplyColor for batched efficiency
        for (int i = 0; i < _voxels.Count; i++)
        {
            var v = _voxels[i];
            if (v == null || v.cache == null) continue;
            try { v.cache.ApplyColor(c); } catch { /* swallow */ }
        }
    }
    #endregion
}