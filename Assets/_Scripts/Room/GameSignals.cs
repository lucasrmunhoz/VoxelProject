// GameSignals.cs
// Barramento de eventos do fluxo de jogo, desacoplando GameFlowManager,
// geradores de sala, portas e UI. Usa backing fields com reset seguro
// via SubsystemRegistration (útil quando Domain Reload está desativado).

using System;
using UnityEngine;

// Aliases para os tipos do contrato único declarado em RoomsData
using RoomPlan     = RoomsData.RoomPlan;
using RoomInstance = RoomsData.RoomInstance;

public static class GameSignals
{
    // --------------------------------------------------------------------
    // Backing fields (mantêm referência real; os "event" públicos encapsulam)
    // --------------------------------------------------------------------
    private static Action<RoomPlan>     s_roomPlanned;
    private static Action<RoomInstance> s_roomBuilt;
    private static Action<RoomInstance> s_roomPopulated;
    private static Action<RoomInstance> s_roomLightOn;
    private static Action<int>          s_requestNextRoom;
    private static Action<RoomInstance> s_roomShouldOpenExit;

    // --------------------------------------------------------------------
    // Eventos públicos
    // --------------------------------------------------------------------
    public static event Action<RoomPlan> RoomPlanned
    {
        add    { s_roomPlanned += value; }
        remove { s_roomPlanned -= value; }
    }

    public static event Action<RoomInstance> RoomBuilt
    {
        add    { s_roomBuilt += value; }
        remove { s_roomBuilt -= value; }
    }

    public static event Action<RoomInstance> RoomPopulated
    {
        add    { s_roomPopulated += value; }
        remove { s_roomPopulated -= value; }
    }

    public static event Action<RoomInstance> RoomLightOn
    {
        add    { s_roomLightOn += value; }
        remove { s_roomLightOn -= value; }
    }

    /// <summary>
    /// Solicita ao maestro (GameFlowManager) que gere/enfileire a próxima sala (por índice).
    /// </summary>
    public static event Action<int> RequestNextRoom
    {
        add    { s_requestNextRoom += value; }
        remove { s_requestNextRoom -= value; }
    }

    /// <summary>
    /// Indica que a sala atual pode abrir a porta de SAÍDA.
    /// No PR-02, este sinal passa a ser emitido **apenas** pelo GameFlowManager
    /// imediatamente após a confirmação de build/registro da próxima sala.
    /// </summary>
    public static event Action<RoomInstance> RoomShouldOpenExit
    {
        add    { s_roomShouldOpenExit += value; }
        remove { s_roomShouldOpenExit -= value; }
    }

    // --------------------------------------------------------------------
    // Reset seguro entre execuções (quando não há Domain Reload)
    // --------------------------------------------------------------------
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        s_roomPlanned        = null;
        s_roomBuilt          = null;
        s_roomPopulated      = null;
        s_roomLightOn        = null;
        s_requestNextRoom    = null;
        s_roomShouldOpenExit = null;
    }

    // --------------------------------------------------------------------
    // Emissores (chame estes métodos para disparar os eventos)
    // --------------------------------------------------------------------
    public static void EmitRoomPlanned(in RoomPlan plan)                  => s_roomPlanned?.Invoke(plan);
    public static void EmitRoomBuilt(RoomInstance inst)                   => s_roomBuilt?.Invoke(inst);
    public static void EmitRoomPopulated(RoomInstance inst)               => s_roomPopulated?.Invoke(inst);
    public static void EmitRoomLightOn(RoomInstance inst)                 => s_roomLightOn?.Invoke(inst);
    public static void EmitRequestNextRoom(int roomIndex)                 => s_requestNextRoom?.Invoke(roomIndex);
    public static void EmitRoomShouldOpenExit(RoomInstance currentRoom)   => s_roomShouldOpenExit?.Invoke(currentRoom);
}
