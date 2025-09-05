// VoxelDoorController.cs
using UnityEngine;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// Controla uma porta composta por múltiplos voxels. Gerencia a animação de abrir/fechar,
/// o estado de travamento e a interação com o jogador (IInteractable) e o GameFlowManager.
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class VoxelDoorController : MonoBehaviour, IInteractable
{
    // ---------------------- Tipos Internos ----------------------
    // Estado original de cada voxel, usado para restaurar ao fechar
    private struct VoxelState
    {
        public Transform VoxelTransform;
        public Vector3   OriginalPosition;
        public Quaternion OriginalRotation;
        public Vector3   OriginalScale;
        public CompositeVoxel VoxelComponent; // cache p/ feedback visual
    }

    // ---------------------- Inspector ----------------------
    [Header("Animação")]
    [Tooltip("Duração total da animação de abrir/fechar (s).")]
    [SerializeField, Min(0.1f)] private float _animationDuration = 1.2f;

    [Tooltip("Atraso por voxel para formar o efeito de 'onda' (s).")]
    [SerializeField, Min(0f)] private float _voxelAnimationDelay = 0.04f;

    [Tooltip("Curva de easing da animação.")]
    [SerializeField] private AnimationCurve _animationCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);

    [Tooltip("Intensidade da rotação aleatória aplicada ao abrir (graus).")]
    [SerializeField] private float _rotationAmount = 180f;

    [Header("Interação")]
    [Tooltip("Se verdadeiro, a porta inicia trancada.")]
    [SerializeField] private bool _startLocked = true;

    [Header("Áudio")]
    [SerializeField] private AudioClip _openSound;
    [SerializeField] private AudioClip _closeSound;
    [SerializeField] private AudioClip _lockedSound;
    [SerializeField] private AudioClip _unlockSound;

    [Header("Feedback de Foco (IInteractable)")]
    [SerializeField] private Color _defaultColor = Color.gray;
    [SerializeField] private Color _focusColor   = Color.yellow;
    [SerializeField] private Color _lockedColor  = new Color(0.8f, 0.2f, 0.2f);

    [Header("Ordenação de Onda (opcional)")]
    [SerializeField] private WallSide _wallSide = WallSide.North;
    [SerializeField, Min(0.0001f)] private float _voxelWorldSize = 1f;

    // ---------------------- Estado Interno ----------------------
    private readonly List<VoxelState> _doorVoxels = new List<VoxelState>();
    private bool _isOpen   = false;
    private bool _isLocked = false;
    private bool _isMoving = false;

    private AudioSource _audioSource;
    private Coroutine _animationCoroutine;

    // ---------------------- Inicialização ----------------------
    private void Awake()
    {
        _audioSource = GetComponent<AudioSource>();

        // Garante um BoxCollider no container para interações (economiza colliders por voxel)
        if (GetComponent<BoxCollider>() == null)
        {
            var col = gameObject.AddComponent<BoxCollider>();
            col.isTrigger = true; // facilita triggers de foco/interação
        }
    }

    private void OnEnable()
    {
        // Reset de estado para reutilização via pooling
        _isLocked = _startLocked;

        if (_animationCoroutine != null)
        {
            StopCoroutine(_animationCoroutine);
            _animationCoroutine = null;
        }

        // Garante visual fechado
        for (int i = 0; i < _doorVoxels.Count; i++)
        {
            var state = _doorVoxels[i];
            if (state.VoxelTransform == null) continue;

            state.VoxelTransform.gameObject.SetActive(true);
            state.VoxelTransform.localPosition = state.OriginalPosition;
            state.VoxelTransform.localRotation = state.OriginalRotation;
            state.VoxelTransform.localScale    = state.OriginalScale;
        }

        _isOpen   = false;
        _isMoving = false;
    }

    // ===========================================================
    // API PÚBLICA para geradores / GFM
    // ===========================================================

    /// <summary>
    /// Inicialização chamada pelo gerador após criar os GOs dos voxels da porta.
    /// Ajusta também um BoxCollider que envolva a porta inteira.
    /// </summary>
    public void Initialize(List<GameObject> voxels)
    {
        _doorVoxels.Clear();

        Bounds bounds = new Bounds();
        bool boundsInitialized = false;

        foreach (var voxelGO in voxels)
        {
            if (voxelGO == null) continue;
            var t = voxelGO.transform;

            _doorVoxels.Add(new VoxelState
            {
                VoxelTransform  = t,
                OriginalPosition = t.localPosition,
                OriginalRotation = t.localRotation,
                OriginalScale    = t.localScale,
                VoxelComponent   = voxelGO.GetComponent<CompositeVoxel>()
            });

            var b = new Bounds(t.localPosition, t.localScale);
            if (!boundsInitialized)
            {
                bounds = b;
                boundsInitialized = true;
            }
            else bounds.Encapsulate(b);
        }

        // Ajusta o BoxCollider do container para cobrir todos os voxels
        if (TryGetComponent<BoxCollider>(out var boxCollider))
        {
            boxCollider.center = bounds.center;
            boxCollider.size   = bounds.size + Vector3.one * 0.1f; // leve padding
        }

        RecomputeWaveOrdering();
    }

    /// <summary>Define metadados de ordenação e escala para a “onda”.</summary>
    public void Setup(WallSide side, float voxelSize)
    {
        _wallSide = side;
        _voxelWorldSize = Mathf.Max(0.0001f, voxelSize);
        RecomputeWaveOrdering();
    }

    /// <summary>
    /// Indexa automaticamente os voxels filhos do root do controlador.
    /// Útil quando a cortina é construída por outro script (ex.: BaseRoomGenerator).
    /// </summary>
    public void InitializeFromChildren()
    {
        _doorVoxels.Clear();

        Bounds bounds = new Bounds();
        bool boundsInitialized = false;

        for (int i = 0; i < transform.childCount; i++)
        {
            var child = transform.GetChild(i);
            if (!child || !child.gameObject.activeInHierarchy) continue;

            _doorVoxels.Add(new VoxelState
            {
                VoxelTransform   = child,
                OriginalPosition = child.localPosition,
                OriginalRotation = child.localRotation,
                OriginalScale    = child.localScale,
                VoxelComponent   = child.GetComponent<CompositeVoxel>()
            });

            var b = new Bounds(child.localPosition, child.localScale);
            if (!boundsInitialized)
            {
                bounds = b;
                boundsInitialized = true;
            }
            else bounds.Encapsulate(b);
        }

        if (TryGetComponent<BoxCollider>(out var boxCollider))
        {
            boxCollider.center = bounds.center;
            boxCollider.size   = bounds.size + Vector3.one * 0.1f;
        }

        RecomputeWaveOrdering();
    }

    /// <summary>Limpa a lista interna de voxels (usado por geradores).</summary>
    public void Clear() => _doorVoxels.Clear();

    /// <summary>Adiciona um voxel à cortina (usado por geradores).</summary>
    public void AddVoxel(Transform voxel)
    {
        if (!voxel) return;
        _doorVoxels.Add(new VoxelState
        {
            VoxelTransform   = voxel,
            OriginalPosition = voxel.localPosition,
            OriginalRotation = voxel.localRotation,
            OriginalScale    = voxel.localScale,
            VoxelComponent   = voxel.GetComponent<CompositeVoxel>()
        });
    }

    // ===========================================================
    // API PÚBLICA (Door)
    // ===========================================================
    public bool IsOpen => _isOpen;
    public bool IsMoving => _isMoving;

    public void Open()  => SetOpen(true);
    public void Close() => SetOpen(false);

    public void Unlock()
    {
        if (!_isLocked) return;
        _isLocked = false;
        if (_unlockSound) _audioSource.PlayOneShot(_unlockSound);
    }

    /// <summary>
    /// Aplica abertura/fechamento com guards de idempotência e corrida:
    /// - Ignora se já está no estado desejado.
    /// - Ignora se já está animando (protege contra sinais repetidos).
    /// </summary>
    public void SetOpen(bool open)
    {
        if (_isOpen == open) return;     // idempotência
        if (_isMoving) return;           // evita corrida

        if (_animationCoroutine != null)
            StopCoroutine(_animationCoroutine);

        _animationCoroutine = StartCoroutine(AnimateDoorCoroutine(open));
    }

    // ===========================================================
    // Implementação de IInteractable
    // ===========================================================
    public string InteractionPrompt => _isLocked ? "Trancada" : (_isOpen ? "Fechar Porta" : "Abrir Porta");

    public bool Interact(GameObject interactor)
    {
        if (_isMoving) return false;
        if (_isLocked)
        {
            if (_lockedSound) _audioSource.PlayOneShot(_lockedSound);
            return false;
        }

        SetOpen(!_isOpen);
        return true;
    }

    public void OnFocusEnter() => ApplyColorToAllVoxels(_isLocked ? _lockedColor : _focusColor);
    public void OnFocusExit()  => ApplyColorToAllVoxels(_defaultColor);

    // ===========================================================
    // Lógica de Animação
    // ===========================================================
    private IEnumerator AnimateDoorCoroutine(bool open)
    {
        _isMoving = true;

        if (open && _openSound)  _audioSource.PlayOneShot(_openSound);
        if (!open && _closeSound) _audioSource.PlayOneShot(_closeSound);

        // Dispara animação com atraso entre voxels (efeito de onda)
        int count = _doorVoxels.Count;
        for (int i = 0; i < count; i++)
        {
            int index = open ? i : (count - 1 - i); // invertido para fechar de trás p/ frente
            var state = _doorVoxels[index];

            if (state.VoxelTransform != null)
                StartCoroutine(AnimateVoxelCoroutine(state, open));

            if (_voxelAnimationDelay > 0f)
                yield return new WaitForSeconds(_voxelAnimationDelay);
        }

        // Espera o tempo total passar (aproximação simples)
        float totalDuration = _animationDuration + (count * _voxelAnimationDelay);
        if (totalDuration > 0f) yield return new WaitForSeconds(totalDuration);

        _isOpen = open;
        _isMoving = false;
        _animationCoroutine = null;
    }

    private IEnumerator AnimateVoxelCoroutine(VoxelState state, bool open)
    {
        var t = state.VoxelTransform;
        if (t == null) yield break;

        // Ao fechar, ativa o voxel no início
        if (!open) t.gameObject.SetActive(true);

        Vector3    startScale   = open ? state.OriginalScale : Vector3.zero;
        Vector3    endScale     = open ? Vector3.zero : state.OriginalScale;
        Quaternion startRotation= open ? state.OriginalRotation : t.localRotation; // mantém rotação randômica ao fechar
        Quaternion endRotation  = open ? state.OriginalRotation * Quaternion.Euler(Random.insideUnitSphere * _rotationAmount)
                                       : state.OriginalRotation;

        float elapsed = 0f;
        while (elapsed < _animationDuration)
        {
            float k = _animationCurve.Evaluate(_animationDuration <= 0f ? 1f : (elapsed / _animationDuration));
            t.localScale   = Vector3.Lerp(startScale, endScale, k);
            t.localRotation= Quaternion.Slerp(startRotation, endRotation, k);
            elapsed += Time.deltaTime;
            yield return null;
        }

        // Estado final garantido
        t.localScale    = endScale;
        t.localRotation = endRotation;

        // Ao abrir, desativa o voxel no final
        if (open) t.gameObject.SetActive(false);
    }

    // ===========================================================
    // Helpers
    // ===========================================================
    private void ApplyColorToAllVoxels(Color color)
    {
        for (int i = 0; i < _doorVoxels.Count; i++)
            _doorVoxels[i].VoxelComponent?.SetColor(color);
    }

    /// <summary>
    /// Reordena a lista de voxels para a animação em “onda”.
    /// Heurística:
    /// - North/South: ordena por X local (esquerda → direita).
    /// - East/West:   ordena por Z local (frente → trás).
    /// </summary>
    private void RecomputeWaveOrdering()
    {
        if (_doorVoxels.Count <= 1) return;

        // Copia para array e ordena com comparador customizado (evita LINQ em plataformas AOT)
        var arr = _doorVoxels.ToArray();

        System.Comparison<VoxelState> cmp;
        switch (_wallSide)
        {
            case WallSide.East:
            case WallSide.West:
                cmp = (a, b) =>
                {
                    float az = a.VoxelTransform ? a.VoxelTransform.localPosition.z : 0f;
                    float bz = b.VoxelTransform ? b.VoxelTransform.localPosition.z : 0f;
                    return az.CompareTo(bz);
                };
                break;

            case WallSide.North:
            case WallSide.South:
            default:
                cmp = (a, b) =>
                {
                    float ax = a.VoxelTransform ? a.VoxelTransform.localPosition.x : 0f;
                    float bx = b.VoxelTransform ? b.VoxelTransform.localPosition.x : 0f;
                    return ax.CompareTo(bx);
                };
                break;
        }

        System.Array.Sort(arr, cmp);

        // Regrava na lista
        _doorVoxels.Clear();
        _doorVoxels.AddRange(arr);
    }
}
