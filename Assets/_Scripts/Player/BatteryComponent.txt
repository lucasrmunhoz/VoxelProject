// BatteryComponent.cs
using UnityEngine;
using System;

/// <summary>
/// Componente independente para gerenciar uma bateria recarregável.
/// Pode ser usado por lanternas, veículos, drones, ferramentas ou qualquer dispositivo
/// que precise de um sistema de consumo/recarga de energia.
/// </summary>
[DisallowMultipleComponent]
public class BatteryComponent : MonoBehaviour
{
    [Header("Configurações da Bateria")]
    [Tooltip("Carga máxima da bateria, em segundos de uso.")]
    [SerializeField, Min(0)] private float _maxBatteryLife = 120f;

    [Tooltip("Taxa de drenagem por segundo (1 = consumo em tempo real).")]
    [SerializeField, Min(0)] private float _drainRate = 1f;

    /// <summary>
    /// Evento disparado quando a bateria muda de valor.
    /// Parâmetros: bateria atual, bateria máxima.
    /// </summary>
    public event Action<float, float> OnBatteryChanged;

    /// <summary> Bateria atual disponível. </summary>
    public float CurrentBattery { get; private set; }

    /// <summary> Capacidade máxima da bateria. </summary>
    public float MaxBattery => _maxBatteryLife;

    /// <summary> Retorna se a bateria está completamente vazia. </summary>
    public bool IsEmpty => CurrentBattery <= 0f;

    private void Awake()
    {
        CurrentBattery = _maxBatteryLife;
    }

    /// <summary>
    /// Drena bateria com base no tempo decorrido.
    /// </summary>
    public void Drain(float deltaTime)
    {
        if (IsEmpty) return;

        CurrentBattery -= _drainRate * deltaTime;
        CurrentBattery = Mathf.Max(0f, CurrentBattery);

        OnBatteryChanged?.Invoke(CurrentBattery, _maxBatteryLife);
    }

    /// <summary>
    /// Recarrega a bateria em uma quantidade arbitrária.
    /// </summary>
    public void Recharge(float amount)
    {
        if (amount <= 0f) return;

        CurrentBattery = Mathf.Clamp(CurrentBattery + amount, 0f, _maxBatteryLife);
        OnBatteryChanged?.Invoke(CurrentBattery, _maxBatteryLife);
    }

    /// <summary>
    /// Define manualmente a bateria atual.
    /// Útil para cheats, depuração ou restaurar um save.
    /// </summary>
    public void SetBattery(float value)
    {
        CurrentBattery = Mathf.Clamp(value, 0f, _maxBatteryLife);
        OnBatteryChanged?.Invoke(CurrentBattery, _maxBatteryLife);
    }
}
