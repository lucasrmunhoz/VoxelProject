// RoomTriggerController.cs
// Controla a detecção de entrada do jogador na sala (lockdown) e integra com o fluxo PR-02:
// - Ao entrar: fecha a porta de ENTRADA (se houver) e notifica o sistema do lockdown.
// - NÃO abre a saída diretamente. A SAÍDA abre somente quando o GameFlowManager emitir
//   GameSignals.RoomShouldOpenExit para ESTA sala (após a próxima sala ficar pronta).

using System.Collections;
using UnityEngine;
using UnityEngine.Events;

// Alias para o contrato único
using RoomInstance = RoomsData.RoomInstance;

[DisallowMultipleComponent]
[AddComponentMenu("Voxel Nightmare/Room Trigger Controller")]
public sealed class RoomTriggerController : MonoBehaviour
{
    [Header("Referências da Sala")]
    [Tooltip("Root/container desta sala (RoomInstance.root). Usado p/ comparar com sinais globais.")]
    [SerializeField] private Transform roomRoot;

    [Tooltip("Container da porta de ENTRADA (blocos 1x1x1).")]
    [SerializeField] private Transform entryDoorRoot;

    [Tooltip("Container da porta de SAÍDA (blocos 1x1x1).")]
    [SerializeField] private Transform exitDoorRoot;

    [Tooltip("Índice desta sala no plano global (RoomPlan.id).")]
    [SerializeField] private int roomIndex = -1;

    [Header("Trigger de Entrada")]
    [Tooltip("Collider que detecta a entrada do jogador. Se vazio, usa o collider deste GO.")]
    [SerializeField] private Collider triggerZone;

    [Tooltip("Tag usada para reconhecer o jogador.")]
    [SerializeField] private string playerTag = "Player";

    [Header("Comportamento")]
    [Tooltip("Disparar apenas uma vez (recomendado).")]
    [SerializeField] private bool oneShot = true;

    [Tooltip("Fechar a porta de ENTRADA assim que o jogador entrar.")]
    [SerializeField] private bool closeEntryOnEnter = true;

    [Tooltip("Notificar o sistema para fazer o lockdown (descarregar sala anterior, etc.).")]
    [SerializeField] private bool notifyLockdown = true;

    [Tooltip("Enviar também um SendMessageUpwards(\"OnRoomLockdownRequested\", roomIndex).")]
    [SerializeField] private bool sendMessageUpwardsOnLockdown = false;

    [System.Serializable]
    public class IntEvent : UnityEvent<int> { }

    [Header("Eventos")]
    [Tooltip("Disparado quando o jogador entra e o lockdown deve acontecer (envia roomIndex).")]
    [SerializeField] private IntEvent onLockdownRequest = new IntEvent();

    [Tooltip("Chamado imediatamente ao detectar o jogador no trigger.")]
    [SerializeField] private UnityEvent onPlayerEntered = new UnityEvent();

    [Tooltip("Chamado após fechar a ENTRADA (se aplicável).")]
    [SerializeField] private UnityEvent onEntryClosed = new UnityEvent();

    // Estado interno
    private bool _armed = true;
    private bool _hasFired;
    private VoxelDoorController _entryDoor;
    private VoxelDoorController _exitDoor;

    private void Awake()
    {
        // Auto-resolve do trigger
        if (!triggerZone) triggerZone = GetComponent<Collider>();
        if (!triggerZone)
        {
            Debug.LogError($"[{nameof(RoomTriggerController)}] Nenhum Collider de trigger atribuído.", this);
        }
        else if (!triggerZone.isTrigger)
        {
            Debug.LogWarning($"[{nameof(RoomTriggerController)}] O collider não está marcado como 'isTrigger'. Corrigindo.", this);
            triggerZone.isTrigger = true;
        }

        // Cache de portas
        if (entryDoorRoot) _entryDoor = entryDoorRoot.GetComponent<VoxelDoorController>();
        if (!_entryDoor && entryDoorRoot) _entryDoor = entryDoorRoot.GetComponentInChildren<VoxelDoorController>(true);

        if (exitDoorRoot) _exitDoor = exitDoorRoot.GetComponent<VoxelDoorController>();
        if (!_exitDoor && exitDoorRoot) _exitDoor = exitDoorRoot.GetComponentInChildren<VoxelDoorController>(true);
    }

    private void OnEnable()
    {
        // Escuta abertura da SAÍDA quando o maestro sinalizar (PR-02)
        GameSignals.RoomShouldOpenExit += OnRoomShouldOpenExitSignal;
    }

    private void OnDisable()
    {
        GameSignals.RoomShouldOpenExit -= OnRoomShouldOpenExitSignal;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!_armed) return;
        if (oneShot && _hasFired) return;
        if (!other) return;

        // Tag do player OU fallback por CharacterController no parent
        bool isPlayer = (string.IsNullOrEmpty(playerTag)) || other.CompareTag(playerTag);
        if (!isPlayer)
        {
            var cc = other.GetComponentInParent<CharacterController>();
            if (cc == null) return;
        }

        _hasFired = true;
        StartCoroutine(LockdownRoutine());
    }

    private IEnumerator LockdownRoutine()
    {
        _armed = false;
        onPlayerEntered?.Invoke();

        // 1) Fecha ENTRADA (caso exista)
        if (closeEntryOnEnter)
        {
            if (_entryDoor)
            {
                // O controller cuida da animação de fechar
                _entryDoor.Close();
                // Pequena folga para animações curtas (sem travar o frame)
                yield return null;
            }
            else if (entryDoorRoot)
            {
                // Fallback: garantir que blocos da porta estejam ATIVOS (fechada)
                SetChildrenActive(entryDoorRoot, true);
                yield return null;
            }

            onEntryClosed?.Invoke();
        }

        // 2) Notifica LOCKDOWN (descarregar sala anterior etc.)
        if (notifyLockdown)
        {
            onLockdownRequest?.Invoke(roomIndex);

            if (sendMessageUpwardsOnLockdown)
            {
                // Livre de dependência direta; GameFlowManager pode implementar:
                // void OnRoomLockdownRequested(int roomIndex)
                SendMessageUpwards("OnRoomLockdownRequested", roomIndex, SendMessageOptions.DontRequireReceiver);
            }
        }
    }

    /// <summary>
    /// Handler do sinal global para abrir a SAÍDA quando a próxima sala estiver pronta.
    /// Usa o contrato único RoomsData.RoomInstance.
    /// </summary>
    private void OnRoomShouldOpenExitSignal(RoomInstance inst)
    {
        if (inst == null || inst.root == null) return;

        // Só responde se o sinal for para ESTA sala
        if (roomRoot && inst.root != roomRoot) return;

        if (_exitDoor)
        {
            _exitDoor.Open();
        }
        else if (exitDoorRoot)
        {
            // "Abrir" = ocultar blocos da cortina
            SetChildrenActive(exitDoorRoot, false);
        }
    }

    /// <summary>Permite rearmar manualmente (para testes/reentrância).</summary>
    public void Arm()
    {
        _armed = true;
        _hasFired = false;
    }

    /// <summary>Desarma definitivamente este trigger.</summary>
    public void Disarm() => _armed = false;

    /// <summary>Define/atualiza metadados em tempo de execução (usado pelo gerador).</summary>
    public void BindRuntime(Transform roomRoot, Transform entryDoorRoot, Transform exitDoorRoot, int roomIndex)
    {
        this.roomRoot = roomRoot;
        this.entryDoorRoot = entryDoorRoot;
        this.exitDoorRoot = exitDoorRoot;
        this.roomIndex = roomIndex;

        // Recache portas
        _entryDoor = null;
        _exitDoor = null;

        if (entryDoorRoot) _entryDoor = entryDoorRoot.GetComponentInChildren<VoxelDoorController>(true);
        if (exitDoorRoot) _exitDoor = exitDoorRoot.GetComponentInChildren<VoxelDoorController>(true);
    }

    private static void SetChildrenActive(Transform root, bool active)
    {
        if (!root) return;
        for (int i = 0; i < root.childCount; i++)
        {
            var c = root.GetChild(i);
            if (c) c.gameObject.SetActive(active);
        }
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (triggerZone)
        {
            Gizmos.color = new Color(1f, 0.8f, 0.2f, 0.35f);

            var box = triggerZone as BoxCollider;
            var sph = triggerZone as SphereCollider;
            var cap = triggerZone as CapsuleCollider;

            if (box)
            {
                Matrix4x4 m = Matrix4x4.TRS(box.transform.TransformPoint(box.center), box.transform.rotation, box.transform.lossyScale);
                using (new UnityEditor.Handles.DrawingScope(m))
                {
                    UnityEditor.Handles.DrawWireCube(Vector3.zero, box.size);
                }
            }
            else if (sph)
            {
                Matrix4x4 m = Matrix4x4.TRS(sph.transform.TransformPoint(sph.center), sph.transform.rotation, sph.transform.lossyScale);
                using (new UnityEditor.Handles.DrawingScope(m))
                {
                    UnityEditor.Handles.DrawWireDisc(Vector3.zero, Vector3.up, sph.radius);
                }
            }
            else if (cap)
            {
                // Representação aproximada
                Matrix4x4 m = Matrix4x4.TRS(cap.transform.TransformPoint(cap.center), cap.transform.rotation, cap.transform.lossyScale);
                using (new UnityEditor.Handles.DrawingScope(m))
                {
                    UnityEditor.Handles.DrawWireDisc(Vector3.zero, Vector3.up, cap.radius);
                }
            }
        }

        // Seta de “fluxo”: entrada → saída
        if (entryDoorRoot && exitDoorRoot)
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(entryDoorRoot.position, exitDoorRoot.position);
            Gizmos.DrawSphere(entryDoorRoot.position, 0.05f);
            Gizmos.DrawSphere(exitDoorRoot.position, 0.05f);
        }
    }
#endif
}
