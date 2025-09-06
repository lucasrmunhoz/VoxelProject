// Assets/Editor/SimpleRoomEditorWindow.cs
// Editor tool para criar salas estáticas no Editor do Unity.
// - Cria um GameObject container "Room_WxDxH" com voxels como filhos.
// - Aplica inicialização e otimização de faces automaticamente.
// - Mantém ligação ao prefab quando possível (PrefabUtility).
// - Suporta Undo e marca a cena como suja.
// PR-03 (ajuste de Editor): cada voxel criado já sai preparado para o pooling em runtime.
//   • Garante presença de PoolableObject (OriginalPrefab = voxelPrefab).
//   • Garante presença de VoxelCache; se houver um VoxelPool na cena do Editor,
//     registra o prefab e grava o PrefabId no VoxelCache imediatamente.
//   • Se não houver VoxelPool no momento da criação, o PoolableObject permitirá
//     que o mapeamento PrefabId seja resolvido em runtime (pelo script PoolableObject.cs).
//
// Observação: coloque este arquivo em Assets/Editor/.

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

    // Posição global do container
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
        roomOrigin = EditorGUILayout.Vector3Field("Room Origin", roomOrigin);

        EditorGUILayout.Space();

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Create Room", GUILayout.Height(36)))
        {
            if (voxelPrefab == null)
            {
                if (!EditorUtility.DisplayDialog("Voxel Prefab não atribuído",
                        "Nenhum voxel prefab foi atribuído. Deseja continuar e criar objetos vazios (GameObjects) em vez de prefabs?",
                        "Sim", "Não"))
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
        if (x == 0)      mask |= VoxelFaceController.Face.East;
        if (x == w - 1)  mask |= VoxelFaceController.Face.West;
        if (z == 0)      mask |= VoxelFaceController.Face.North;
        if (z == d - 1)  mask |= VoxelFaceController.Face.South;

        // Se a parede também for chão ou teto, adicione essas faces.
        if (y == 0 && buildFloor)       mask |= VoxelFaceController.Face.Top;
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

        // Posiciona o container
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

        // Tenta localizar um VoxelPool na cena (Editor). Se existir, já registra o prefab e obtém o id.
        int editorPrefabId = -1;
        VoxelPool editorPool = FindObjectOfType<VoxelPool>();
        if (editorPool != null && voxelPrefab != null)
        {
            try
            {
                editorPrefabId = editorPool.RegisterPrefab(voxelPrefab, maxPoolSize: 512, prewarm: 0);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SimpleRoomEditorWindow] Não foi possível registrar prefab no VoxelPool do Editor: {ex.Message}");
            }
        }

        // Spawn loop: coloca floor/walls/ceiling conforme flags
        for (int y = 0; y < height; y++)
        {
            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    // Gera voxels apenas em um plano (evita arestas/quinas)
                    int planeCount = 0;

                    if ((y == 0 && buildFloor) || (y == height - 1 && buildCeiling)) planeCount++;
                    if ((z == 0 || z == depth - 1) && buildWalls) planeCount++;
                    if ((x == 0 || x == width - 1) && buildWalls) planeCount++;

                    if (planeCount != 1) continue;
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

                    // parent e transform local
                    go.transform.SetParent(containerGO.transform, false);
                    Vector3 localPos = new Vector3(x * voxelSize, y * voxelSize, z * voxelSize);
                    go.transform.localPosition = localPos;
                    go.transform.localRotation = Quaternion.identity;

                    // === PREPARO PARA POOLING EM RUNTIME (PR-03) ===
                    TrySetupPoolingFor(go, voxelPrefab, editorPool, editorPrefabId);

                    // Inicialização de voxel + máscara de faces
                    try
                    {
                        var baseVoxel = go.GetComponent<BaseVoxel>();
                        if (baseVoxel != null)
                        {
                            baseVoxel.Initialize(VoxelType.Empty, true);

                            var mask = CalculateFaceMask(x, y, z, width, depth, height);

                            var faceController = go.GetComponent<VoxelFaceController>();
                            if (faceController != null)
                            {
                                faceController.ApplyFaceMask(mask, true);
                            }
                            else
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
                        Debug.LogError($"[SimpleRoomEditorWindow] Erro ao inicializar o voxel '{go.name}': {ex.Message}\n{ex.StackTrace}");
                    }
                } // x
            } // z
        } // y

        // Final: seleciona container e marca cena suja
        Selection.activeGameObject = containerGO;
        EditorSceneManager.MarkSceneDirty(containerGO.scene);
        _lastCreatedContainer = containerGO;
    }

    /// <summary>
    /// Garante que o voxel recém-criado esteja pronto para o sistema de pooling/caching no runtime.
    /// - PoolableObject com OriginalPrefab preenchido (usado como fallback para mapear PrefabId em runtime).
    /// - VoxelCache presente; se houver um VoxelPool no Editor, grava o PrefabId agora.
    /// </summary>
    private static void TrySetupPoolingFor(GameObject go, GameObject sourcePrefab, VoxelPool editorPool, int editorPrefabId)
    {
        if (go == null) return;

        // 1) Tag do fallback de pooling (para runtime mapear o PrefabId caso esteja faltando)
        var tag = go.GetComponent<PoolableObject>() ?? go.AddComponent<PoolableObject>();
        tag.OriginalPrefab = sourcePrefab;

        // 2) Cache para o VoxelPool (PrefabId + refs internas)
        var cache = go.GetComponent<VoxelCache>() ?? go.AddComponent<VoxelCache>();
        cache.EnsureCached();

        // Se estamos no Editor e há um VoxelPool na cena, já gravamos o PrefabId.
        if (editorPool != null && sourcePrefab != null && editorPrefabId > 0)
        {
            try
            {
                // Garante que o id ainda é válido para este VoxelPool
                int id = editorPool.RegisterPrefab(sourcePrefab, maxPoolSize: 512, prewarm: 0);
                if (id > 0) cache.SetPrefabId(id);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SimpleRoomEditorWindow] Falha ao definir PrefabId no VoxelCache: {ex.Message}");
            }
        }
        else
        {
            // Sem VoxelPool no editor agora: tudo bem. Em runtime,
            // PoolableObject/gerador fará o mapeamento e atualizará o VoxelCache.
            // (PrefabId permanecerá zerado até lá.)
        }
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
