// FlashlightControl.cs
using UnityEngine;
using System;

/// <summary>
/// Orquestra o comportamento da lanterna:
/// - controla o estado (on/off) da Light + MeshCollider
/// - interage com BatteryComponent para drenagem/recharge
/// - delega a geração do cone para FlashlightConeGenerator
/// - escuta FlashlightInputHandler para entrada desacoplada
///
/// Observação importante: evita adicionar componentes durante OnValidate para
/// não disparar mensagens do editor ("SendMessage cannot be called during Awake, CheckConsistency, or OnValidate").
/// </summary>
[RequireComponent(typeof(Light), typeof(MeshCollider))]
[DisallowMultipleComponent]
public class FlashlightControl : MonoBehaviour
{
    [Header("Componentes (preenchidos automaticamente se possível)")]
    [SerializeField] private Light _flashlightLight;
    [SerializeField] private MeshCollider _meshCollider;
    [SerializeField] private BatteryComponent _batteryComponent;
    [SerializeField] private FlashlightConeGenerator _coneGenerator;

    [Header("Comportamento")]
    [Tooltip("Se true, a lanterna consumirá carga via BatteryComponent.")]
    [SerializeField] private bool _useBattery = true;

    [Tooltip("Inicia ligada na cena (apenas para edição/testes).")]
    [SerializeField] private bool _startOn = false;

    /// <summary>Evento: a lanterna foi ligada/desligada. Parâmetro: estado atual (true = ligado).</summary>
    public event Action<bool> OnFlashlightToggled;

    /// <summary>Encaminha eventos de bateria (current, max) para assinantes externos.</summary>
    public event Action<float, float> OnBatteryChanged;

    public bool IsOn { get; private set; }

    // --- Unity lifecycle ---
    private void Awake()
    {
        // cache components sem forçar AddComponent (AddComponent é seguro em runtime, mas evitamos fazê-lo no editor/OnValidate)
        if (_flashlightLight == null) TryGetComponent(out _flashlightLight);
        if (_meshCollider == null) TryGetComponent(out _meshCollider);
        if (_batteryComponent == null) TryGetComponent(out _batteryComponent);
        if (_coneGenerator == null) TryGetComponent(out _coneGenerator);

        // validações básicas
        if (_flashlightLight == null)
        {
            Debug.LogError($"[FlashlightControl] Light requerida não encontrada em '{name}'. Desabilitando componente.", this);
            enabled = false;
            return;
        }

        if (_meshCollider == null)
        {
            Debug.LogError($"[FlashlightControl] MeshCollider requerido não encontrado em '{name}'. Desabilitando componente.", this);
            enabled = false;
            return;
        }

        // se não houver generator em tempo de execução, adiciona um — isso é seguro no runtime
        if (_coneGenerator == null)
        {
            _coneGenerator = GetComponent<FlashlightConeGenerator>();
            if (_coneGenerator == null)
                _coneGenerator = gameObject.AddComponent<FlashlightConeGenerator>();
        }

        // inicializa generator com referências (ele fará suas próprias validações)
        _coneGenerator.Initialize(_flashlightLight, _meshCollider);

        // ajusta estados iniciais do MeshCollider conforme a lanterna
        _meshCollider.convex = true;
        _meshCollider.isTrigger = true;

        // valida tipo de light (spot é o esperado); apenas avisa
        if (_flashlightLight.type != LightType.Spot)
        {
            Debug.LogWarning($"[FlashlightControl] Light '{_flashlightLight.name}' não é SpotLight. O cone não será preciso.", this);
        }
    }

    private void OnEnable()
    {
        // Inscreve evento de input desacoplado
        FlashlightInputHandler.OnFlashlightToggleRequested += HandleToggleRequested;

        // Inscreve evento de bateria, se existir
        if (_batteryComponent != null)
            _batteryComponent.OnBatteryChanged += HandleBatteryChanged;
    }

    private void Start()
    {
        // Estado inicial: respeita _startOn e se há bateria disponível
        IsOn = _startOn && (!_useBattery || _batteryComponent == null || !_batteryComponent.IsEmpty);
        ApplyStateImmediate(IsOn, suppressEvent: true);

        // Notifica estado inicial de bateria (útil para UI)
        if (_batteryComponent != null)
            OnBatteryChanged?.Invoke(_batteryComponent.CurrentBattery, _batteryComponent.MaxBattery);
    }

    private void Update()
    {
        // Atualiza apenas o cone (ele decide internamente se precisa reconstruir)
        _coneGenerator?.UpdateCone();

        // Drena a bateria apenas se necessário (minimiza trabalho)
        if (IsOn && _useBattery && _batteryComponent != null)
        {
            _batteryComponent.Drain(Time.deltaTime);
            // HandleBatteryChanged será chamado pelo evento OnBatteryChanged caso a bateria mude/zerar
        }
    }

    private void OnDisable()
    {
        FlashlightInputHandler.OnFlashlightToggleRequested -= HandleToggleRequested;

        if (_batteryComponent != null)
            _batteryComponent.OnBatteryChanged -= HandleBatteryChanged;
    }

    private void OnDestroy()
    {
        // segurança extra para evitar leaks (eventos estáticos)
        FlashlightInputHandler.OnFlashlightToggleRequested -= HandleToggleRequested;
        if (_batteryComponent != null)
            _batteryComponent.OnBatteryChanged -= HandleBatteryChanged;
    }

    // --- Handlers / API pública ---

    /// <summary>
    /// Alterna o estado da lanterna — público para chamadas externas (UI, AI, cutscenes).
    /// </summary>
    public void Toggle()
    {
        // não liga se bateria está vazia e uso de bateria ativo
        if (!IsOn && _useBattery && _batteryComponent != null && _batteryComponent.IsEmpty)
            return;

        SetState(!IsOn);
    }

    /// <summary>
    /// Define explicitamente o estado da lanterna.
    /// </summary>
    public void SetState(bool enable)
    {
        // se pediram ligar mas bateria está vazia, ignora
        if (enable && _useBattery && _batteryComponent != null && _batteryComponent.IsEmpty)
            return;

        ApplyStateImmediate(enable);
    }

    /// <summary>
    /// Recarrega bateria (encaminha para BatteryComponent).
    /// </summary>
    public void RechargeBattery(float amount)
    {
        if (_batteryComponent == null || amount <= 0f) return;
        _batteryComponent.Recharge(amount);
    }

    /// <summary>
    /// Força atualização do cone (útil para editores/cheats).
    /// </summary>
    public void ForceUpdateCone()
    {
        _coneGenerator?.UpdateCone();
    }

    // --- Implementação interna ---

    private void ApplyStateImmediate(bool enable, bool suppressEvent = false)
    {
        IsOn = enable;

        // altera Light e Collider atomically
        if (_flashlightLight != null) _flashlightLight.enabled = IsOn;
        if (_meshCollider != null) _meshCollider.enabled = IsOn;

        if (!suppressEvent)
            OnFlashlightToggled?.Invoke(IsOn);
    }

    private void HandleToggleRequested()
    {
        Toggle();
    }

    private void HandleBatteryChanged(float current, float max)
    {
        // repassa para assinantes (UI, SFX, etc.)
        OnBatteryChanged?.Invoke(current, max);

        // auto-desliga quando bateria acaba
        if (current <= 0f && IsOn)
        {
            ApplyStateImmediate(false);
        }
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        // NÃO adicionar componentes aqui — AddComponent durante OnValidate pode disparar SendMessage e lançar erros.
        // Apenas tenta reconciliar referências já existentes para facilitar edição no Inspector.

        if (Application.isPlaying) return; // evita confusão enquanto o jogo roda no editor

        // tenta obter referências existentes
        TryGetComponent(out _flashlightLight);
        TryGetComponent(out _meshCollider);
        TryGetComponent(out _batteryComponent);

        // NÃO usar AddComponent aqui; apenas pega se já existir.
        _coneGenerator = GetComponent<FlashlightConeGenerator>();

        // Se tudo existir, podemos pedir ao generator para inicializar (ele internamente valida)
        if (_flashlightLight != null && _meshCollider != null && _coneGenerator != null)
        {
            // Initialize no editor é aceitável porque não adicionamos componentes nem chamamos SendMessage proibido.
            _coneGenerator.Initialize(_flashlightLight, _meshCollider);
        }
    }
#endif
}
