// RoomStateManager.cs
// Gerencia o estado de uma sala individual: luzes, inimigos e a iluminação estática dos voxels.
// Ativado por um evento externo (como um interruptor), ele "resolve" a sala e aciona a geração da próxima.
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public class RoomStateManager : MonoBehaviour
{
    [Header("Referências da Sala")]
    [Tooltip("A luz principal da sala que será acesa ao ativar.")]
    [SerializeField] private Light mainLight;
    [Tooltip("Lista de GameObjects de inimigos a serem desativados.")]
    [SerializeField] private List<GameObject> monstersInRoom;

    [Header("Efeito de Iluminação dos Voxels")]
    [Tooltip("A cor que os voxels terão quando estiverem mais próximos da luz principal.")]
    [SerializeField] private Color maxBrightnessColor = Color.yellow;
    [Tooltip("A cor que os voxels terão quando estiverem mais distantes da luz.")]
    [SerializeField] private Color minBrightnessColor = new Color(0.1f, 0.1f, 0.1f);
    [Tooltip("A distância máxima em que a luz principal influencia a cor dos voxels.")]
    [SerializeField, Min(0.1f)] private float lightFalloffDistance = 15f;
    
    // --- Estado Interno ---
    private GameFlowManager _gameFlowManager;
    private MicroVoxel[] _microVoxelsCache;
    private int _roomIndex = -1;
    private bool _isActivated = false;

    void Start()
    {
        // Encontra o gerenciador de fluxo de jogo para a progressão.
        _gameFlowManager = FindObjectOfType<GameFlowManager>();
        if (_gameFlowManager == null)
        {
            Debug.LogError("[RoomStateManager] GameFlowManager não encontrado na cena!", this);
        }

        // Otimização: encontra todos os MicroVoxels nos filhos apenas uma vez e armazena em cache.
        _microVoxelsCache = GetComponentsInChildren<MicroVoxel>();

        // Garante que a sala comece no estado "não resolvido" (escura).
        if (mainLight != null)
        {
            mainLight.enabled = false;
        }
    }

    /// <summary>
    /// Define o índice desta sala. Deve ser chamado pelo BaseRoomGenerator durante a criação da sala.
    /// </summary>
    public void SetRoomIndex(int index)
    {
        _roomIndex = index;
    }

    /// <summary>
    /// Este é o método público principal a ser chamado pelo interruptor da sala.
    /// Ele acende as luzes, desativa os inimigos e inicia a geração da próxima sala.
    /// </summary>
    public void ActivateRoom()
    {
        // Trava para garantir que a sala só possa ser ativada uma vez.
        if (_isActivated) return;
        _isActivated = true;

        if (_gameFlowManager != null && _gameFlowManager.verbose)
        {
            Debug.Log($"[RoomStateManager] Ativando sala #{_roomIndex}...");
        }

        // 1. Acende a luz principal da sala.
        if (mainLight != null)
        {
            mainLight.enabled = true;
        }

        // 2. Desativa todos os monstros associados a esta sala.
        foreach (var monster in monstersInRoom)
        {
            if (monster != null)
            {
                monster.SetActive(false);
            }
        }

        // 3. Aplica a iluminação estática a todos os MicroVoxels.
        ApplyStaticVoxelLighting();

        // 4. Inicia a geração da próxima sala em segundo plano.
        if (_gameFlowManager != null && _roomIndex > -1)
        {
            _gameFlowManager.EnqueueRoomGeneration(_roomIndex + 1);
            // NOTA: A porta de SAÍDA desta sala deve ouvir o evento GameFlowManager.OnRoomLoaded
            // para saber quando a sala _roomIndex + 1 está pronta para ser aberta.
        }
    }

    /// <summary>
    /// Itera sobre todos os MicroVoxels em cache, desliga sua reatividade
    /// e define uma cor estática com base na distância da luz principal.
    /// </summary>
    private void ApplyStaticVoxelLighting()
    {
        if (_microVoxelsCache == null || _microVoxelsCache.Length == 0 || mainLight == null)
        {
            return;
        }

        Vector3 lightPosition = mainLight.transform.position;

        foreach (var voxel in _microVoxelsCache)
        {
            // Desliga a reatividade do voxel à lanterna do jogador.
            voxel.SetReactionMode(MicroVoxel.ReactionMode.None);

            // Calcula a distância do voxel até a luz.
            float distance = Vector3.Distance(voxel.transform.position, lightPosition);

            // Usa InverseLerp para mapear a distância para um valor de brilho (0 a 1).
            // 0 = mais longe, 1 = mais perto.
            float brightness = 1f - Mathf.Clamp01(distance / lightFalloffDistance);

            // Interpola entre a cor mínima e máxima para obter a cor final.
            Color finalColor = Color.Lerp(minBrightnessColor, maxBrightnessColor, brightness);

            // Aplica a cor estática final ao voxel.
            voxel.ForceSetColor(finalColor);
        }
    }
}