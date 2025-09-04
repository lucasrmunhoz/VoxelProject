using UnityEngine;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// Controla uma porta composta por múltiplos voxels. Gerencia a animação de abertura/fechamento,
/// o estado de travamento e a interação com o jogador e o GameFlowManager.
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class VoxelDoorController : MonoBehaviour, IInteractable
{
    // Struct para armazenar o estado original de cada voxel, essencial para a animação de fechamento.
    private struct VoxelState
    {
        public Transform VoxelTransform;
        public Vector3 OriginalPosition;
        public Quaternion OriginalRotation;
        public Vector3 OriginalScale;
        public CompositeVoxel VoxelComponent; // Cache para feedback visual
    }

    [Header("Animação")]
    [Tooltip("Duração total da animação de abrir ou fechar em segundos.")]
    [SerializeField, Min(0.1f)] private float _animationDuration = 1.2f;
    [Tooltip("Pequeno atraso entre a animação de cada voxel, criando um efeito de 'onda'.")]
    [SerializeField, Min(0f)] private float _voxelAnimationDelay = 0.04f;
    [Tooltip("Curva que define a aceleração/desaceleração da animação para um efeito mais suave.")]
    [SerializeField] private AnimationCurve _animationCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);
    [Tooltip("Intensidade da rotação aleatória aplicada a cada voxel ao abrir.")]
    [SerializeField] private float _rotationAmount = 180f;
    
    [Header("Interação")]
    [Tooltip("Define se a porta começa trancada.")]
    [SerializeField] private bool _startLocked = true;

    [Header("Áudio")]
    [SerializeField] private AudioClip _openSound;
    [SerializeField] private AudioClip _closeSound;
    [SerializeField] private AudioClip _lockedSound;
    [SerializeField] private AudioClip _unlockSound;
    
    [Header("Feedback de Foco (IInteractable)")]
    [SerializeField] private Color _defaultColor = Color.gray;
    [SerializeField] private Color _focusColor = Color.yellow;
    [SerializeField] private Color _lockedColor = new Color(0.8f, 0.2f, 0.2f);

    // --- Estado Interno ---
    private readonly List<VoxelState> _doorVoxels = new List<VoxelState>();
    private bool _isOpen = false;
    private bool _isLocked = false;
    private bool _isMoving = false;
    private AudioSource _audioSource;
    private Coroutine _animationCoroutine;

    #region Inicialização e Ciclo de Vida
    private void Awake()
    {
        _audioSource = GetComponent<AudioSource>();
        // Adiciona um colisor grande ao container para a detecção de interação do jogador.
        // Isso é mais eficiente do que ter um colisor em cada voxel para este propósito.
        if (GetComponent<Collider>() == null)
        {
            var col = gameObject.AddComponent<BoxCollider>();
            col.isTrigger = true; // Necessário para OnFocusEnter/Exit se o trigger do jogador não for rígido
        }
    }

    private void OnEnable()
    {
        // Reseta o estado para garantir consistência ao ser reutilizado pelo sistema de pooling
        _isLocked = _startLocked;
        if (_animationCoroutine != null)
        {
            StopCoroutine(_animationCoroutine);
            _animationCoroutine = null;
        }
        
        // Garante que a porta esteja visualmente fechada
        foreach (var state in _doorVoxels)
        {
            if (state.VoxelTransform != null)
            {
                state.VoxelTransform.gameObject.SetActive(true);
                state.VoxelTransform.localPosition = state.OriginalPosition;
                state.VoxelTransform.localRotation = state.OriginalRotation;
                state.VoxelTransform.localScale = state.OriginalScale;
            }
        }
        _isOpen = false;
        _isMoving = false;
    }
    
    /// <summary>
    /// Método de inicialização chamado pelo BaseRoomGenerator após criar os voxels da porta.
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
                VoxelTransform = t,
                OriginalPosition = t.localPosition,
                OriginalRotation = t.localRotation,
                OriginalScale = t.localScale,
                VoxelComponent = voxelGO.GetComponent<CompositeVoxel>() // Cache do componente
            });

            if (!boundsInitialized)
            {
                bounds = new Bounds(t.localPosition, t.localScale);
                boundsInitialized = true;
            }
            else
            {
                bounds.Encapsulate(new Bounds(t.localPosition, t.localScale));
            }
        }

        // Ajusta o colisor do container para envolver todos os voxels
        if (TryGetComponent<BoxCollider>(out var boxCollider))
        {
            boxCollider.center = bounds.center;
            boxCollider.size = bounds.size + Vector3.one * 0.1f; // Um pouco de padding
        }
    }
    #endregion

    #region API Pública (para GameFlowManager)
    public void Open() => SetOpen(true);
    public void Close() => SetOpen(false);
    public void Unlock()
    {
        if (!_isLocked) return;
        _isLocked = false;
        _audioSource.PlayOneShot(_unlockSound);
    }

    public void SetOpen(bool open)
    {
        if (_isOpen == open || _isMoving) return;

        if (_animationCoroutine != null) StopCoroutine(_animationCoroutine);
        _animationCoroutine = StartCoroutine(AnimateDoorCoroutine(open));
    }
    #endregion

    #region Implementação de IInteractable
    public string InteractionPrompt => _isLocked ? "Trancada" : (_isOpen ? "Fechar Porta" : "Abrir Porta");

    public bool Interact(GameObject interactor)
    {
        if (_isMoving) return false;
        if (_isLocked)
        {
            _audioSource.PlayOneShot(_lockedSound);
            return false;
        }

        SetOpen(!_isOpen);
        return true;
    }

    public void OnFocusEnter() => ApplyColorToAllVoxels(_isLocked ? _lockedColor : _focusColor);
    public void OnFocusExit() => ApplyColorToAllVoxels(_defaultColor);
    #endregion

    #region Lógica de Animação
    private IEnumerator AnimateDoorCoroutine(bool open)
    {
        _isMoving = true;
        _audioSource.PlayOneShot(open ? _openSound : _closeSound);

        // Dispara a animação para cada voxel com um atraso
        for (int i = 0; i < _doorVoxels.Count; i++)
        {
            // Para fechar, invertemos a ordem para um efeito mais natural
            int index = open ? i : _doorVoxels.Count - 1 - i;
            if(_doorVoxels[index].VoxelTransform != null)
            {
                StartCoroutine(AnimateVoxelCoroutine(_doorVoxels[index], open));
            }
            
            if (_voxelAnimationDelay > 0)
                yield return new WaitForSeconds(_voxelAnimationDelay);
        }

        // Espera a animação completa terminar
        float totalDuration = _animationDuration + (_doorVoxels.Count * _voxelAnimationDelay);
        yield return new WaitForSeconds(totalDuration);

        _isOpen = open;
        _isMoving = false;
        _animationCoroutine = null;
    }

    private IEnumerator AnimateVoxelCoroutine(VoxelState state, bool open)
    {
        Transform t = state.VoxelTransform;
        if (t == null) yield break;

        // Ativa o voxel no início da animação de fechamento
        if (!open) t.gameObject.SetActive(true);

        Vector3 startScale = open ? state.OriginalScale : Vector3.zero;
        Vector3 endScale = open ? Vector3.zero : state.OriginalScale;

        Quaternion startRotation = open ? state.OriginalRotation : t.localRotation; // Mantém a rotação aleatória ao fechar
        Quaternion endRotation = open ? state.OriginalRotation * Quaternion.Euler(Random.insideUnitSphere * _rotationAmount) : state.OriginalRotation;

        float elapsedTime = 0f;
        while (elapsedTime < _animationDuration)
        {
            float progress = _animationCurve.Evaluate(elapsedTime / _animationDuration);

            t.localScale = Vector3.Lerp(startScale, endScale, progress);
            t.localRotation = Quaternion.Slerp(startRotation, endRotation, progress);

            elapsedTime += Time.deltaTime;
            yield return null;
        }

        // Garante o estado final
        t.localScale = endScale;
        t.localRotation = endRotation;

        // Desativa o voxel no final da animação de abertura
        if (open) t.gameObject.SetActive(false);
    }
    #endregion
    
    #region Helpers
    private void ApplyColorToAllVoxels(Color color)
    {
        foreach (var state in _doorVoxels)
        {
            state.VoxelComponent?.SetColor(color);
        }
    }
    #endregion
}