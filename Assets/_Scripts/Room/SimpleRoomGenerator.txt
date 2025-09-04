// SimpleRoomEditorWindow.cs
// Editor tool para criar salas estáticas no Editor do Unity.
// Coloque em Assets/Editor/ para funcionar.
// - Cria um GameObject container chamado "Room_WxDxH" com voxels instanciados como filhos.
// - Aplica inicialização e otimização de faces automaticamente (correção crítica).
// - Usa PrefabUtility.InstantiatePrefab quando possível (mantém ligação ao prefab).
// - Suporta Undo e marca a cena como suja.
// - ADICIONADO: Cria um "ExitAnchor" no local da porta para integração com o BaseRoomGenerator.
// - ALTERADO: Anexa o componente DoorAnchorInfo ao ExitAnchor com as dimensões da porta.
using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

public class SimpleRoomEditorWindow : EditorWindow
{
    // Room
    int width = 8;
    int depth = 8;
    int height = 3;
    float voxelSize = 1f;
    GameObject voxelPrefab;

    // Occupancy toggles
    bool buildFloor = true;
    bool buildWalls = true;
    bool buildCeiling = false;

    // Door
    bool doorEnabled = true;
    enum DoorWall { North, South, East, West }
    DoorWall doorWall = DoorWall.North;
    int doorWidth = 2;
    int doorHeight = 2;
    bool doorCentered = true;
    int doorOffset = 0; // usado se doorCentered == false

    // Parent / naming
    string roomNamePrefix = "Room";
    Transform parentForRoom = null;
    
    // SUGESTÃO 2: Variável para controlar a posição de origem da sala
    Vector3 roomOrigin = Vector3.zero;

    // Guarda referência ao último container criado para ClearLastCreatedRoom
    private GameObject _lastCreatedContainer;

    [MenuItem("Tools/Simple Room Creator")]
    public static void OpenWindow()
    {
        var w = GetWindow<SimpleRoomEditorWindow>("Simple Room Creator");
        w.minSize = new Vector2(360, 320);
    }

    void OnGUI()
    {
        EditorGUILayout.LabelField("Room size (tiles/voxels)", EditorStyles.boldLabel);
        width = EditorGUILayout.IntField("Width (X)", Mathf.Max(1, width));
        depth = EditorGUILayout.IntField("Depth (Z)", Mathf.Max(1, depth));
        height = EditorGUILayout.IntField("Height (Y)", Mathf.Max(1, height));
        voxelSize = EditorGUILayout.FloatField("Voxel Size (units)", Mathf.Max(0.001f, voxelSize));
        EditorGUILayout.Space();

        EditorGUILayout.LabelField("Voxel prefab", EditorStyles.boldLabel);
        voxelPrefab = (GameObject)EditorGUILayout.ObjectField("Voxel Prefab", voxelPrefab, typeof(GameObject), false);
        EditorGUILayout.HelpBox("Recomendado: prefab contendo o seu Voxel (CompositeVoxel / VoxelFaceController).", MessageType.Info);
        EditorGUILayout.Space();

        EditorGUILayout.LabelField("Build options", EditorStyles.boldLabel);
        buildFloor = EditorGUILayout.Toggle("Build Floor", buildFloor);
        buildWalls = EditorGUILayout.Toggle("Build Walls (perimeter)", buildWalls);
        buildCeiling = EditorGUILayout.Toggle("Build Ceiling", buildCeiling);
        EditorGUILayout.Space();

        EditorGUILayout.LabelField("Door (hole) settings", EditorStyles.boldLabel);
        doorEnabled = EditorGUILayout.Toggle("Enable Door (hole)", doorEnabled);
        using (new EditorGUI.DisabledScope(!doorEnabled))
        {
            doorWall = (DoorWall)EditorGUILayout.EnumPopup("Door Wall", doorWall);
            doorWidth = EditorGUILayout.IntField("Door Width (tiles)", Mathf.Clamp(doorWidth, 1, Mathf.Max(width, depth)));
            doorHeight = EditorGUILayout.IntField("Door Height (tiles)", Mathf.Clamp(doorHeight, 1, height));
            doorCentered = EditorGUILayout.Toggle("Center Door on Wall", doorCentered);
            if (!doorCentered)
                doorOffset = EditorGUILayout.IntField("Door Offset (from left edge)", Mathf.Max(0, doorOffset));
        }
        EditorGUILayout.Space();

        EditorGUILayout.LabelField("Hierarchy & Positioning", EditorStyles.boldLabel);
        roomNamePrefix = EditorGUILayout.TextField("Room Name Prefix", roomNamePrefix);
        parentForRoom = (Transform)EditorGUILayout.ObjectField("Parent Transform (optional)", parentForRoom, typeof(Transform), true);
        
        // SUGESTÃO 2: Campo para editar a origem da sala na UI
        roomOrigin = EditorGUILayout.Vector3Field("Room Origin", roomOrigin);
        
        EditorGUILayout.Space();

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Create Room", GUILayout.Height(36)))
        {
            if (voxelPrefab == null)
            {
                if (!EditorUtility.DisplayDialog("Voxel Prefab não atribuido", "Nenhum voxel prefab foi atribuído. Deseja continuar e criar objetos vazios (GameObjects) em vez de prefabs?", "Sim", "Não"))
                    return;
            }
            CreateRoom();
        }

        if (GUILayout.Button("Clear Last Room Created", GUILayout.Height(36)))
        {
            ClearLastCreatedRoom();
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox("Use Undo (Ctrl/Cmd+Z) para reverter a criação. O container da sala fica selecionado após a criação.", MessageType.None);
    }

    // ===========================
    // Método de cálculo de máscara
    // ===========================
    VoxelFaceController.Face CalculateFaceMask(int x, int y, int z, int w, int d, int h)
    {
        // Se este voxel não é uma parede, não precisa de máscara complexa.
        // Chão só mostra o topo, teto só mostra o fundo.
        if (x > 0 && x < w - 1 && z > 0 && z < d - 1)
        {
            if (y == 0 && buildFloor) return VoxelFaceController.Face.Top;
            if (y == h - 1 && buildCeiling) return VoxelFaceController.Face.Bottom;
            return VoxelFaceController.Face.None;
        }

        // Lógica para as paredes do perímetro
        var mask = VoxelFaceController.Face.None;
        if (x == 0) mask |= VoxelFaceController.Face.East;
        if (x == w - 1) mask |= VoxelFaceController.Face.West;
        if (z == 0) mask |= VoxelFaceController.Face.North;
        if (z == d - 1) mask |= VoxelFaceController.Face.South;

        // Se a parede também for chão ou teto, adicione essas faces.
        if (y == 0 && buildFloor) mask |= VoxelFaceController.Face.Top;
        if (y == h - 1 && buildCeiling) mask |= VoxelFaceController.Face.Bottom;

        return mask;
    }

    // ===========================
    // CreateRoom
    // ===========================
    void CreateRoom()
    {
        // Nome único
        string containerName = $"{roomNamePrefix}_{width}x{depth}x{height}";
        var containerGO = new GameObject(containerName);

        // SUGESTÃO 2: Define a posição global do container da sala com a variável
        containerGO.transform.position = roomOrigin;

        if (parentForRoom != null) containerGO.transform.SetParent(parentForRoom, false);

        // Register Undo para criação do container
        Undo.RegisterCreatedObjectUndo(containerGO, "Create Room Container");

        _lastCreatedContainer = containerGO;

        // Método utilitário para saber se devemos pular a colocação por causa da porta
        bool ShouldSkipDueToDoor(int x, int y, int z)
        {
            if (!doorEnabled) return false;

            int wallLength = (doorWall == DoorWall.North || doorWall == DoorWall.South) ? width : depth;
            int start = 0;
            if (doorCentered)
            {
                start = Mathf.FloorToInt((wallLength - doorWidth) / 2f);
                start = Mathf.Clamp(start, 0, Mathf.Max(0, wallLength - doorWidth));
            }
            else
            {
                start = Mathf.Clamp(doorOffset, 0, Mathf.Max(0, wallLength - doorWidth));
            }

            switch (doorWall)
            {
                case DoorWall.North:
                    if (z == depth - 1)
                        if (x >= start && x < start + doorWidth && y >= 0 && y < doorHeight) return true;
                    break;
                case DoorWall.South:
                    if (z == 0)
                        if (x >= start && x < start + doorWidth && y >= 0 && y < doorHeight) return true;
                    break;
                case DoorWall.East:
                    if (x == width - 1)
                        if (z >= start && z < start + doorWidth && y >= 0 && y < doorHeight) return true;
                    break;
                case DoorWall.West:
                    if (x == 0)
                        if (z >= start && z < start + doorWidth && y >= 0 && y < doorHeight) return true;
                    break;
            }
            return false;
        }

        // Spawn loop: coloca floor/walls/ceiling conforme flags
        for (int y = 0; y < height; y++)
        {
            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    // --- SUGESTÃO DE ALTERAÇÃO ---
                    // Esta nova lógica unificada gera voxels apenas se eles pertencerem a UMA ÚNICA face.
                    int planeCount = 0;
                    
                    // O voxel está em um plano horizontal (chão ou teto)?
                    if ((y == 0 && buildFloor) || (y == height - 1 && buildCeiling))
                    {
                        planeCount++;
                    }
                    
                    // O voxel está em um plano vertical no eixo Z (paredes Norte/Sul)?
                    if ((z == 0 || z == depth - 1) && buildWalls)
                    {
                        planeCount++;
                    }
                    
                    // O voxel está em um plano vertical no eixo X (paredes Leste/Oeste)?
                    if ((x == 0 || x == width - 1) && buildWalls)
                    {
                        planeCount++;
                    }
                    
                    // Só gera o voxel se ele estiver em exatamente UM plano.
                    // Se planeCount > 1, significa que é uma aresta. Se planeCount > 2, é uma quina.
                    if (planeCount != 1)
                    {
                        continue;
                    }
                    // --- FIM DA SUGESTÃO ---

                    if (ShouldSkipDueToDoor(x, y, z)) continue;

                    GameObject go = null;
                    if (voxelPrefab != null)
                    {
                        try
                        {
                            // Tenta instanciar mantendo ligação com prefab
                            if (PrefabUtility.IsPartOfPrefabAsset(voxelPrefab))
                            {
                                go = (GameObject)PrefabUtility.InstantiatePrefab(voxelPrefab);
                            }
                            else
                            {
                                go = (GameObject)PrefabUtility.InstantiatePrefab(voxelPrefab, containerGO.scene);
                                if (go == null) go = (GameObject)Instantiate(voxelPrefab);
                            }
                        }
                        catch
                        {
                            // fallback seguro
                            go = (GameObject)Instantiate(voxelPrefab);
                        }
                    }
                    else
                    {
                        go = new GameObject("Voxel");
                        go.transform.localScale = Vector3.one * voxelSize;
                    }

                    // Register undo for created voxel
                    Undo.RegisterCreatedObjectUndo(go, "Create Voxel");

                    // parent and set local transform
                    go.transform.SetParent(containerGO.transform, false);
                    Vector3 localPos = new Vector3(x * voxelSize, y * voxelSize, z * voxelSize);
                    go.transform.localPosition = localPos;
                    go.transform.localRotation = Quaternion.identity;
                    
                    // --- CÓDIGO A SER ADICIONADO ---
                    Debug.Log($"[SimpleRoomGenerator] VoxelPrefab '{go.name}' posicionado no mundo em X: {go.transform.position.x}, Y: {go.transform.position.y}, Z: {go.transform.position.z}");
                    // --- FIM DO CÓDIGO ---

                    // *** INÍCIO DA NOVA CORREÇÃO SIMPLIFICADA ***
                    try
                    {
                        var baseVoxel = go.GetComponent<BaseVoxel>();
                        if (baseVoxel != null)
                        {
                            // Primeiro, inicializa o estado base do voxel
                            baseVoxel.Initialize(VoxelType.Empty, true);

                            // Em seguida, calcula e aplica a máscara de face correta para otimização
                            var mask = CalculateFaceMask(x, y, z, width, depth, height);

                            var faceController = go.GetComponent<VoxelFaceController>();
                            if (faceController != null)
                            {
                                faceController.ApplyFaceMask(mask, true);
                            }
                            else // Fallback se estiver usando CompositeVoxel
                            {
                                var compositeVoxel = go.GetComponent<CompositeVoxel>();
                                if (compositeVoxel != null)
                                {
                                    compositeVoxel.ApplyFaceMask((CompositeVoxel.Face)mask);
                                }
                            }
                        }
                        else
                        {
                            Debug.LogWarning($"O prefab '{go.name}' não possui um componente derivado de BaseVoxel. Nenhuma máscara será aplicada.");
                        }
                    }
                    catch (Exception ex)
                    {
                        // Mudamos para LogError para a mensagem ficar vermelha e mais visível
                        Debug.LogError($"[SimpleRoomEditorWindow] Erro ao inicializar o voxel '{go.name}': {ex.Message}\n{ex.StackTrace}");
                    }
                    // *** FIM DA NOVA CORREÇÃO SIMPLIFICADA ***
                } // x
            } // z
        } // y

        // =======================================================================
        // INÍCIO DA ALTERAÇÃO: Criar o ExitAnchor para a porta
        // =======================================================================
        if (doorEnabled)
        {
            // Calcula o ponto de início da porta na parede
            int wallLength = (doorWall == DoorWall.North || doorWall == DoorWall.South) ? width : depth;
            int start = 0;
            if (doorCentered)
            {
                start = Mathf.FloorToInt((wallLength - doorWidth) / 2f);
            }
            else
            {
                start = doorOffset;
            }
            start = Mathf.Clamp(start, 0, Mathf.Max(0, wallLength - doorWidth));

            // Calcula a posição local e rotação do centro da porta
            Vector3 doorCenterLocalPos = Vector3.zero;
            Quaternion doorRotation = Quaternion.identity;
            float halfDoorWidth = (doorWidth / 2f - 0.5f) * voxelSize;
            float doorHeightOffset = (doorHeight / 2f - 0.5f) * voxelSize;

            switch (doorWall)
            {
                case DoorWall.South: // Parede Z=0
                    doorCenterLocalPos = new Vector3((start * voxelSize) + halfDoorWidth, doorHeightOffset, 0);
                    doorRotation = Quaternion.Euler(0, 0, 0);
                    break;
                case DoorWall.North: // Parede Z=depth-1
                    doorCenterLocalPos = new Vector3((start * voxelSize) + halfDoorWidth, doorHeightOffset, (depth - 1) * voxelSize);
                    doorRotation = Quaternion.Euler(0, 180, 0);
                    break;
                case DoorWall.West: // Parede X=0
                    doorCenterLocalPos = new Vector3(0, doorHeightOffset, (start * voxelSize) + halfDoorWidth);
                    doorRotation = Quaternion.Euler(0, 90, 0);
                    break;
                case DoorWall.East: // Parede X=width-1
                    doorCenterLocalPos = new Vector3((width - 1) * voxelSize, doorHeightOffset, (start * voxelSize) + halfDoorWidth);
                    doorRotation = Quaternion.Euler(0, -90, 0);
                    break;
            }

            var anchorGO = new GameObject("ExitAnchor");
            Undo.RegisterCreatedObjectUndo(anchorGO, "Create Door Anchor");
            anchorGO.transform.SetParent(containerGO.transform, false);
            anchorGO.transform.localPosition = doorCenterLocalPos;
            anchorGO.transform.localRotation = doorRotation;
            
            // =======================================================================
            // INÍCIO DO BLOCO ADICIONADO
            // =======================================================================
            // "Anexa" as informações do tamanho da porta ao anchor.
            var anchorInfo = anchorGO.AddComponent<DoorAnchorInfo>();
            anchorInfo.doorWidth = this.doorWidth;
            anchorInfo.doorHeight = this.doorHeight;
            // =======================================================================
            // FIM DO BLOCO ADICIONADO
            // =======================================================================
        }
        // =======================================================================
        // FIM DA ALTERAÇÃO
        // =======================================================================

        // Final touches: select container and mark scene dirty
        Selection.activeGameObject = containerGO;
        EditorSceneManager.MarkSceneDirty(containerGO.scene);
        _lastCreatedContainer = containerGO;
    }

    void ClearLastCreatedRoom()
    {
        if (_lastCreatedContainer == null)
        {
            EditorUtility.DisplayDialog("Nada para limpar", "Não há um container de sala criado por esta janela para limpar.", "OK");
            return;
        }

        if (EditorUtility.DisplayDialog("Remover sala", $"Deseja remover o container '{_lastCreatedContainer.name}' criado anteriormente?", "Remover", "Cancelar"))
        {
            Undo.DestroyObjectImmediate(_lastCreatedContainer);
            _lastCreatedContainer = null;
            EditorSceneManager.MarkAllScenesDirty();
        }
    }
}