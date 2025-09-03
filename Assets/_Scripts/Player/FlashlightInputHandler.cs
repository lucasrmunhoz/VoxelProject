// FlashlightInputHandler.cs
using UnityEngine;
using System;

/// <summary>
/// Handler genérico para entrada da lanterna.
/// - Dispara um evento quando a tecla configurada é pressionada.
/// - Mantém o FlashlightControl desacoplado do sistema de Input.
/// - Permite fácil migração para o novo Input System ou integração com IA/eventos.
/// </summary>
[DisallowMultipleComponent]
public class FlashlightInputHandler : MonoBehaviour
{
    [Header("Configuração de Entrada")]
    [Tooltip("Tecla usada para alternar a lanterna (toggle).")]
    [SerializeField] private KeyCode _toggleKey = KeyCode.F;

    /// <summary>
    /// Evento disparado quando o jogador solicita alternar a lanterna.
    /// </summary>
    public static event Action OnFlashlightToggleRequested;

    private void Update()
    {
        if (Input.GetKeyDown(_toggleKey))
        {
            OnFlashlightToggleRequested?.Invoke();
        }
    }
}
