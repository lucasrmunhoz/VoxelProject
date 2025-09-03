// MicroVoxel.cs
// VERSÃO ATUALIZADA: A animação de "ligar" foi alterada para uma rotação aleatória
// que termina alinhada aos eixos, sem o efeito de "vai e volta".
// VERSÃO CORRIGIDA: A lógica de fade foi ajustada para não reiniciar a cada frame.
// VERSÃO CORRIGIDA (2): A animação 'AnimateOn' agora usa '_highlightColor' como destino.
// VERSÃO CORRIGIDA (3): A lógica da cor de "memória" foi ajustada para usar Color.Lerp.

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;

[DisallowMultipleComponent]
[RequireComponent(typeof(Collider))]
public class MicroVoxel : BaseVoxel
{
    // O VoxelState 'AnimatingOff' não é mais usado para iniciar uma animação,
    // mas a transição de estados ainda existe internamente.
    private enum VoxelState { IdleBlack, AnimatingOn, FullyLit, AnimatingOff, Memory, FadingToBlack }

    public enum ReactionMode : byte
    {
        None = 0,
        PulseOnly = 1,
        ColorOnly = 2,
        PulseAndColor = 3,
        ParticleBurst = 4,
        RotateAndFade = 5
    }

    [Header("Components (assign optional)")]
    [SerializeField] private Renderer _renderer;
    [SerializeField] private Collider _triggerCollider;       // should be isTrigger
    [Tooltip("Optional particle prefab to use for bursts. If empty, will use a child ParticleSystem if available.")]
    [SerializeField] private ParticleSystem _particlePrefab;

    [Header("Colors")]
    [SerializeField] private Color _baseColor = Color.white;
    [SerializeField] private Color _highlightColor = Color.cyan;

    [Header("Fade")]
    [SerializeField, Min(0.001f)] private float _fadeDuration = 0.35f;
    [SerializeField] private AnimationCurve _fadeCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);

    [Header("Pulse")]
    [SerializeField, Min(0f)] private float _pulseAmplitude = 0.06f; // relative to local scale
    [SerializeField, Min(0.01f)] private float _pulseFrequency = 1.4f; // Hz
    
    [Header("Rotation")]
    [Tooltip("Velocidade de rotação em graus por segundo em cada eixo quando iluminado.")]
    [SerializeField] private Vector3 _rotationSpeed = new Vector3(45f, 45f, 45f);

    [Header("Animação Customizada (RotateAndFade)")]
    [Tooltip("Duração da animação de ligar.")]
    [SerializeField] private float animationDuration = 0.4f;
    [Tooltip("Curva para a animação de rotação (ex: um pico no meio).")]
    [SerializeField] private AnimationCurve rotationCurve = new AnimationCurve(new Keyframe(0, 0), new Keyframe(0.5f, 1), new Keyframe(1, 0));
    [Tooltip("Duração em segundos que o voxel permanece com a cor de 'memória'.")]
    [SerializeField] private float memoryDuration = 5f;
    [Tooltip("Brilho da cor de 'memória' (0 = preto, 1 = cor original).")]
    [SerializeField, Range(0f, 1f)] private float memoryBrightness = 0.2f;
    [Tooltip("Período de 'graça' (em segundos) antes de o voxel ser considerado 'apagado'. Ajuda a evitar oscilações.")]
    [SerializeField] private float unlitGracePeriod = 0.2f;
    private float _unlitGracePeriodTimer;


    [Header("Light detection")]
    [SerializeField] private bool _reactToLights = true;
    [SerializeField] private bool _reactToAmbient = true;
    [SerializeField] private float _ambientInfluence = 1f;

    [Header("Behavior")]
    [SerializeField] private ReactionMode _reactionMode = ReactionMode.PulseAndColor;
    [SerializeField, Range(0f, 5f)] private float _particleBurstIntensity = 1f; // scale factor for burst

    [Header("Shader")]
    [SerializeField] private string _colorPropertyName = "_Color";

    // Internal caches
    private MaterialPropertyBlock _mpb;
    private int _colorPropId;
    private Color _currentColor;
    private Color _fadeFrom;
    private Color _fadeTo;
    private float _fadeElapsed;
    private float _fadeInstanceDuration;
    private bool _isFading;

    private Vector3 _baseLocalScale;
    private float _pulsePhaseOffset;

    private readonly List<Light> _activeLights = new List<Light>(2);

    // caches used between Manager Update and Advance
    private float _cachedNormalizedForPulse = 0f;
    private float _cachedTimeForPulse = 0f;

    // registration status with manager
    private bool _registered;

    private bool _isInitialized = false;
    
    private VoxelState _currentState = VoxelState.IdleBlack;
    private Coroutine _animationCoroutine;
    private float _memoryTimer;

    // Event for external systems (audio, bloom, etc.)
    public static event Action<MicroVoxel, ReactionMode> OnMicroVoxelReact;

    #region Unity lifecycle
    private void Awake()
    {
        EnsureInitialized();

        _baseLocalScale = transform.localScale;
        _pulsePhaseOffset = ((GetInstanceID() * 73856093) & 0xFFFF) / 65535f * Mathf.PI * 2f;

        // apply immediate color
        ApplyColorImmediate(_currentColor);
    }
    
    private void EnsureInitialized()
    {
        if (_isInitialized) return;

        if (_renderer == null)
            TryGetComponent(out _renderer);

        if (_triggerCollider == null)
            TryGetComponent(out _triggerCollider);

        if (_triggerCollider == null)
            Debug.LogWarning($"[MicroVoxel] Trigger collider not assigned on {name}.", this);
        else if (!_triggerCollider.isTrigger)
            Debug.LogWarning($"[MicroVoxel] Collider on {name} is not marked as isTrigger — recommended to set it.", this);

        _mpb = new MaterialPropertyBlock();
        _colorPropId = Shader.PropertyToID(_colorPropertyName);

        _currentColor = _baseColor;
        _fadeInstanceDuration = Mathf.Max(0.0001f, _fadeDuration);

        _isInitialized = true;
    }

    private void OnEnable()
    {
        RegisterToManager();
    }

    private void OnDisable()
    {
        UnregisterFromManager();
    }
    #endregion

    #region BaseVoxel integration
    protected override void OnInitialize(VoxelType type, bool isSolid)
    {
        EnsureInitialized();

        _activeLights.Clear();
        
        if (_animationCoroutine != null)
        {
            StopCoroutine(_animationCoroutine);
            _animationCoroutine = null;
        }

        if (_reactionMode == ReactionMode.RotateAndFade)
        {
            _currentState = VoxelState.IdleBlack;
            _currentColor = Color.black;
            transform.localRotation = Quaternion.identity;
        }
        else
        {
            _currentColor = _baseColor;
        }
        
        _isFading = false;
        ApplyColorImmediate(_currentColor);

        if (_triggerCollider != null)
            _triggerCollider.enabled = true;
    }
    #endregion

    #region Trigger handling
    private void OnTriggerEnter(Collider other)
    {
        if (!_reactToLights) return;
        var l = other.GetComponentInChildren<Light>() ?? other.GetComponent<Light>();
        if (l != null && !_activeLights.Contains(l))
            _activeLights.Add(l);
    }

    private void OnTriggerExit(Collider other)
    {
        if (!_reactToLights) return;
        var l = other.GetComponentInChildren<Light>() ?? other.GetComponent<Light>();
        if (l != null)
            _activeLights.Remove(l);
    }
    #endregion

    #region Manager callbacks (called by MicroVoxelManager)
    internal void ManagerUpdate(float dt, float time)
    {
        // --- Etapa 1: Calcular a intensidade da luz (código inalterado) ---
        float localIntensity = 0f;
        if (_reactToLights && _activeLights.Count > 0)
        {
            float maxIntensity = 0f;
            for (int i = _activeLights.Count - 1; i >= 0; i--)
            {
                var l = _activeLights[i];
                // --- INÍCIO DA CORREÇÃO ---
                // Verifica se a luz foi destruída OU se o componente foi desabilitado.
                if (l == null || !l.enabled) { _activeLights.RemoveAt(i); continue; }
                // --- FIM DA CORREÇÃO ---
                float dist = Vector3.Distance(transform.position, l.transform.position);
                float range = Mathf.Max(0.0001f, l.range);
                float att = Mathf.Clamp01(1f - (dist / range));
                float contrib = l.intensity * att;
                if (contrib > maxIntensity) maxIntensity = contrib;
            }
            localIntensity = maxIntensity;
        }
        float ambientIntensity = 0f;
        if (_reactToAmbient)
        {
            Color amb = RenderSettings.ambientLight;
            ambientIntensity = (amb.r + amb.g + amb.b) / 3f * _ambientInfluence;
        }
        float normalized = Mathf.Clamp01(Mathf.Max(localIntensity, ambientIntensity));
        
        // --- Etapa 2: Lógica do Período de Graça (código inalterado) ---
        bool isReceivingLightNow = normalized > 0.1f;

        if (isReceivingLightNow)
        {
            _unlitGracePeriodTimer = unlitGracePeriod;
        }
        else
        {
            if (_unlitGracePeriodTimer > 0)
            {
                _unlitGracePeriodTimer -= dt;
            }
        }
        bool isLit = _unlitGracePeriodTimer > 0f;

        // --- Etapa 3: Máquina de Estados (Lógica inalterada) ---
        if (_reactionMode == ReactionMode.RotateAndFade)
        {
            if (_animationCoroutine == null)
            {
                if (isLit && (_currentState == VoxelState.IdleBlack || _currentState == VoxelState.FadingToBlack || _currentState == VoxelState.Memory))
                {
                    _animationCoroutine = StartCoroutine(AnimateOn());
                }
                else if (!isLit && _currentState == VoxelState.FullyLit)
                {
                    _currentState = VoxelState.Memory;
                    _memoryTimer = memoryDuration;
                    
                    // --- INÍCIO DA MUDANÇA ---
                    // CÓDIGO CORRIGIDO - Interpola entre a cor base e a de destaque para criar a cor de memória.
                    Color memoryColor = Color.Lerp(_baseColor, _highlightColor, memoryBrightness);
                    // --- FIM DA MUDANÇA ---
                    memoryColor.a = _baseColor.a; // Mantém a transparência original, se houver

                    StartFade(memoryColor, _fadeDuration); 
                }
            }
        }
        else if (_reactionMode != ReactionMode.None)
        {
            Color targetColor = Color.Lerp(_baseColor, _highlightColor, normalized);
            
            // --- INÍCIO DA ALTERAÇÃO ---
            if (_reactionMode == ReactionMode.ColorOnly || _reactionMode == ReactionMode.PulseAndColor)
            {
                // A condição agora verifica se o *destino do fade* (_fadeTo) é diferente do novo alvo,
                // ou se não há nenhum fade acontecendo (_isFading == false).
                // Isso previne que o fade seja reiniciado desnecessariamente a cada frame.
                if ((_isFading && !ColorsApproximatelyEqual(_fadeTo, targetColor)) || (!_isFading && !ColorsApproximatelyEqual(_currentColor, targetColor)))
                {
                    StartFade(targetColor, Mathf.Lerp(0.06f, _fadeInstanceDuration, normalized));
                }
            }
            // --- FIM DA ALTERAÇÃO ---
            
            if (_reactionMode == ReactionMode.ParticleBurst && normalized > 0.2f)
            {
                TriggerParticleBurst(normalized);
            }
            
            OnMicroVoxelReact?.Invoke(this, _reactionMode);
            
            _cachedNormalizedForPulse = normalized;
            _cachedTimeForPulse = time;
        }
    }

    internal void ManagerAdvance(float dt)
    {
        // Lógica de fade (inalterada)
        if (_isFading)
        {
            _fadeElapsed += dt;
            float t = Mathf.Clamp01(_fadeElapsed / _fadeInstanceDuration);
            _currentColor = Color.Lerp(_fadeFrom, _fadeTo, t);
            ApplyColorImmediate(_currentColor);
            if (t >= 1f)
            {
                _isFading = false;
                if (_currentState == VoxelState.FadingToBlack)
                {
                    _currentState = VoxelState.IdleBlack;
                }
            }
        }

        // Gerencia o timer da memória (inalterado)
        if (_currentState == VoxelState.Memory)
        {
            _memoryTimer -= dt;
            if (_memoryTimer <= 0)
            {
                _currentState = VoxelState.FadingToBlack;
                StartFade(Color.black, _fadeDuration);
            }
        }

        // Pulse advance para outros modos
        if (_reactionMode == ReactionMode.PulseOnly || _reactionMode == ReactionMode.PulseAndColor)
        {
            ProcessPulse(_cachedTimeForPulse);
        }
    }
    #endregion

    #region Fade / Pulse / Burst internals
    // --- INÍCIO DA MUDANÇA ---
    // A corrotina foi modificada para realizar uma rotação aleatória para um estado final alinhado aos eixos.
    private IEnumerator AnimateOn()
    {
        _currentState = VoxelState.AnimatingOn;

        float elapsed = 0f;
        Quaternion startRotation = transform.localRotation;
        
        // Sorteia uma nova rotação final que seja alinhada aos eixos (múltipla de 90 graus).
        Quaternion targetRotation = GetRandomAxisAlignedRotation();

        Color startColor = _currentColor;
        // CÓDIGO CORRIGIDO - O destino agora é a cor de destaque
        Color targetColor = _highlightColor;

        while (elapsed < animationDuration)
        {
            elapsed += Time.deltaTime;
            float progress = Mathf.Clamp01(elapsed / animationDuration);

            // Interpola suavemente da rotação inicial para a nova rotação aleatória alinhada.
            // O efeito de "vai e volta" foi removido.
            transform.localRotation = Quaternion.Slerp(startRotation, targetRotation, progress);
            
            // O fade da cor acompanha o progresso da animação.
            Color newColor = Color.Lerp(startColor, targetColor, progress);
            ApplyColorImmediate(newColor);
            _currentColor = newColor;

            yield return null;
        }

        // Garante que o estado final seja exatamente o alvo.
        transform.localRotation = targetRotation;
        ApplyColorImmediate(targetColor);
        _currentColor = targetColor;
        _currentState = VoxelState.FullyLit;
        
        _animationCoroutine = null;
    }

    /// <summary>
    /// Retorna uma das 24 orientações possíveis de um cubo perfeitamente alinhado aos eixos.
    /// </summary>
    private Quaternion GetRandomAxisAlignedRotation()
    {
        int choice = UnityEngine.Random.Range(0, 24);
        switch (choice)
        {
            // Face "Up" apontando para +Y (4 rotações)
            case 0: return Quaternion.Euler(0, 0, 0);
            case 1: return Quaternion.Euler(0, 90, 0);
            case 2: return Quaternion.Euler(0, 180, 0);
            case 3: return Quaternion.Euler(0, 270, 0);

            // Face "Up" apontando para -Y (4 rotações)
            case 4: return Quaternion.Euler(180, 0, 0);
            case 5: return Quaternion.Euler(180, 90, 0);
            case 6: return Quaternion.Euler(180, 180, 0);
            case 7: return Quaternion.Euler(180, 270, 0);

            // Face "Up" apontando para +X (4 rotações)
            case 8: return Quaternion.Euler(0, 0, 270);
            case 9: return Quaternion.Euler(0, 90, 270);
            case 10: return Quaternion.Euler(0, 180, 270);
            case 11: return Quaternion.Euler(0, 270, 270);

            // Face "Up" apontando para -X (4 rotações)
            case 12: return Quaternion.Euler(0, 0, 90);
            case 13: return Quaternion.Euler(0, 90, 90);
            case 14: return Quaternion.Euler(0, 180, 90);
            case 15: return Quaternion.Euler(0, 270, 90);

            // Face "Up" apontando para +Z (4 rotações)
            case 16: return Quaternion.Euler(90, 0, 0);
            case 17: return Quaternion.Euler(90, 0, 90);
            case 18: return Quaternion.Euler(90, 0, 180);
            case 19: return Quaternion.Euler(90, 0, 270);
            
            // Face "Up" apontando para -Z (4 rotações)
            case 20: return Quaternion.Euler(270, 0, 0);
            case 21: return Quaternion.Euler(270, 0, 90);
            case 22: return Quaternion.Euler(270, 0, 180);
            case 23: return Quaternion.Euler(270, 0, 270);
        }
        return Quaternion.identity; // Fallback
    }
    // --- FIM DA MUDANÇA ---

    private void StartFade(Color to, float duration)
    {
        if (_isFading && ColorsApproximatelyEqual(_fadeTo, to) && Mathf.Approximately(_fadeInstanceDuration, duration))
            return;

        _fadeFrom = _currentColor;
        _fadeTo = to;
        _fadeElapsed = 0f;
        _fadeInstanceDuration = Mathf.Max(0.0001f, duration);
        _isFading = true;
    }

    private void ProcessPulse(float time)
    {
        float scaleFactor = Mathf.Max(0.0001f, _baseLocalScale.magnitude);
        float freq = _pulseFrequency / Mathf.Max(0.5f, scaleFactor);
        float phase = time * freq * Mathf.PI * 2f + _pulsePhaseOffset;
        float amplitude = _pulseAmplitude * scaleFactor * (1f + _cachedNormalizedForPulse);
        float s = 1f + Mathf.Sin(phase) * amplitude;
        transform.localScale = _baseLocalScale * s;
    }

    private void ProcessRotation(float dt)
    {
        float intensity = _cachedNormalizedForPulse;
        if (intensity > 0.01f)
        {
            transform.Rotate(_rotationSpeed * intensity * dt, Space.Self);
        }
    }

    private void TriggerParticleBurst(float normalizedIntensity)
    {
        var prefab = _particlePrefab;
        if (prefab == null)
        {
            if (_renderer != null)
            {
                var ps = GetComponentInChildren<ParticleSystem>();
                if (ps != null) prefab = ps;
            }
        }

        if (prefab == null)
            return;

        var psInstance = ParticleSystemPool.Play(prefab, transform.position, transform.rotation, (ps) =>
        {
            var main = ps.main;
            main.startSpeed = Mathf.Lerp(0.2f, 2f, normalizedIntensity) * _particleBurstIntensity;
            main.startSize = Mathf.Lerp(0.01f, 0.08f, normalizedIntensity) * transform.localScale.magnitude;
        });
    }
    #endregion

    #region Utilities
    private void ApplyColorImmediate(Color color)
    {
        EnsureInitialized();
        if (_renderer == null) return;
        _mpb.Clear();
        _mpb.SetColor(_colorPropId, color);
        _renderer.SetPropertyBlock(_mpb);
    }

    private static bool ColorsApproximatelyEqual(Color a, Color b, float eps = 0.003f)
    {
        float dx = a.r - b.r, dy = a.g - b.g, dz = a.b - b.b, da = a.a - b.a;
        return (dx * dx + dy * dy + dz * dz + da * da) <= (eps * eps);
    }
    #endregion

    #region Manager registration
    private void RegisterToManager()
    {
        if (_registered) return;
        MicroVoxelManager.Register(this);
        _registered = true;
    }

    private void UnregisterFromManager()
    {
        if (!_registered) return;
        MicroVoxelManager.Unregister(this);
        _registered = false;
    }
    #endregion

    #region Public API (batch-friendly)
    public void Configure(Color baseColor, Color highlightColor, ReactionMode mode, float pulseAmp)
    {
        EnsureInitialized();
        _baseColor = baseColor;
        _highlightColor = highlightColor;
        _reactionMode = mode;
        _pulseAmplitude = pulseAmp;
        ForceSetColor(baseColor);
    }

    public void ForceSetColor(Color color)
    {
        EnsureInitialized();
        _currentColor = color;
        ApplyColorImmediate(color);
        _isFading = false;
    }

    public void TriggerBurstNow(float intensity = 1f)
    {
        TriggerParticleBurst(intensity);
        OnMicroVoxelReact?.Invoke(this, ReactionMode.ParticleBurst);
    }

    public void SetReactionMode(ReactionMode mode) => _reactionMode = mode;
    public void SetHighlightColor(Color color) => _highlightColor = color;
    public void SetPulseAmplitude(float amp) => _pulseAmplitude = amp;
    #endregion
}

// O CÓDIGO DO MicroVoxelManager E ParticleSystemPool PERMANECE O MESMO.
// COLE ESTE CÓDIGO NO FINAL DO ARQUIVO MicroVoxel.cs, APÓS O FIM DA CLASSE MicroVoxel
[DefaultExecutionOrder(-1000)]
internal class MicroVoxelManager : MonoBehaviour
{
    private static MicroVoxelManager _instance;
    private readonly List<MicroVoxel> _items = new List<MicroVoxel>(1024);
    private readonly HashSet<MicroVoxel> _set = new HashSet<MicroVoxel>(1024);

    private float _time;

    private static MicroVoxelManager Instance
    {
        get
        {
            if (_instance == null)
            {
                var go = new GameObject("MicroVoxelManager");
                go.hideFlags = HideFlags.HideAndDontSave;
                _instance = go.AddComponent<MicroVoxelManager>();
                DontDestroyOnLoad(go);
            }
            return _instance;
        }
    }

    private void Update()
    {
        float dt = Time.deltaTime;
        _time += dt;

        // Decision phase
        for (int i = 0; i < _items.Count; i++)
        {
            var m = _items[i];
            if (m == null) continue;
            m.ManagerUpdate(dt, _time);
        }

        // Advance phase
        for (int i = 0; i < _items.Count; i++)
        {
            var m = _items[i];
            if (m == null) continue;
            m.ManagerAdvance(dt);
        }
    }

    #region Registration (swap-pop removal)
    public static void Register(MicroVoxel m)
    {
        if (m == null) return;
        var inst = Instance;
        if (inst._set.Contains(m)) return;
        inst._set.Add(m);
        inst._items.Add(m);
    }

    public static void Unregister(MicroVoxel m)
    {
        if (m == null || _instance == null) return;
        var inst = _instance;
        if (!inst._set.Remove(m)) return;
        int idx = inst._items.IndexOf(m);
        if (idx >= 0)
        {
            int last = inst._items.Count - 1;
            if (idx != last)
                inst._items[idx] = inst._items[last];
            inst._items.RemoveAt(last);
        }
    }
    #endregion

    #region Batch APIs
    public static void BatchConfigure(IEnumerable<MicroVoxel> voxels, Color baseColor, Color highlightColor, MicroVoxel.ReactionMode mode, float pulseAmp)
    {
        if (voxels == null) return;
        foreach (var v in voxels)
        {
            if (v == null) continue;
            v.Configure(baseColor, highlightColor, mode, pulseAmp);
        }
    }

    public static void BatchTriggerBurst(IEnumerable<MicroVoxel> voxels, float intensity = 1f)
    {
        if (voxels == null) return;
        foreach (var v in voxels)
            v.TriggerBurstNow(intensity);
    }
    #endregion
}

internal class ParticleSystemPool : MonoBehaviour
{
    private static ParticleSystemPool _instance;
    private readonly Dictionary<int, List<ParticleSystem>> _poolByPrefabId = new Dictionary<int, List<ParticleSystem>>();
    private readonly List<ActivePS> _active = new List<ActivePS>(64);

    private class ActivePS
    {
        public ParticleSystem ps;
        public float remaining;
        public int prefabId;
    }

    private static ParticleSystemPool Instance
    {
        get
        {
            if (_instance == null)
            {
                var go = new GameObject("ParticleSystemPool");
                go.hideFlags = HideFlags.HideAndDontSave;
                _instance = go.AddComponent<ParticleSystemPool>();
                DontDestroyOnLoad(go);
            }
            return _instance;
        }
    }

    private void Update()
    {
        float dt = Time.deltaTime;
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var ap = _active[i];
            if (ap.ps == null) { _active.RemoveAt(i); continue; }
            ap.remaining -= dt;
            if (ap.remaining <= 0f)
            {
                ap.ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                ReturnToPool(ap.prefabId, ap.ps);
                _active.RemoveAt(i);
            }
        }
    }
    
    public static ParticleSystem Play(ParticleSystem prefab, Vector3 pos, Quaternion rot, Action<ParticleSystem> configure = null)
    {
        if (prefab == null) return null;
        var inst = Instance;
        int id = prefab.GetInstanceID();
        var pool = inst._poolByPrefabId;
        ParticleSystem psInstance = null;

        if (pool.TryGetValue(id, out var list) && list.Count > 0)
        {
            int last = list.Count - 1;
            psInstance = list[last];
            list.RemoveAt(last);
            if (psInstance == null) psInstance = GameObject.Instantiate(prefab);
        }
        else
        {
            psInstance = GameObject.Instantiate(prefab);
        }

        psInstance.transform.SetParent(Instance.transform, true);
        psInstance.transform.position = pos;
        psInstance.transform.rotation = rot;

        configure?.Invoke(psInstance);

        var main = psInstance.main;
        float duration = main.duration;
        float startLifetimeMax = 0f;
        try { startLifetimeMax = main.startLifetime.constantMax; } catch { startLifetimeMax = 0.5f; }
        float total = duration + startLifetimeMax;

        psInstance.Play(true);
        inst._active.Add(new ActivePS { ps = psInstance, remaining = total, prefabId = id });

        return psInstance;
    }

    private void ReturnToPool(int prefabId, ParticleSystem ps)
    {
        if (ps == null) return;
        ps.transform.SetParent(Instance.transform, true);
        if (!_poolByPrefabId.TryGetValue(prefabId, out var list))
        {
            list = new List<ParticleSystem>(8);
            _poolByPrefabId.Add(prefabId, list);
        }
        list.Add(ps);
    }
}