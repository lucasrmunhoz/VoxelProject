// BedroomGenerator.cs
// Gerador especializado de quartos (herda BaseRoomGenerator)
// Alterações: MaterialPropertyBlock para colorização, Renderer cache, pós-processamento por coroutine com budget de tempo.
// CORRIGIDO: Atualizado para usar a propriedade 'Rng' da classe base, que implementa inicialização preguiçosa 
// para evitar a NullReferenceException.
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public class BedroomGenerator : BaseRoomGenerator
{
    [Header("Bedroom Styling")]
    [Tooltip("Cor base das paredes/voxels do quarto")]
    public Color wallBaseColor = new Color(0.85f, 0.8f, 0.78f);
    [Tooltip("Variação aleatória aplicada à cor base")]
    [Range(0f, 0.5f)] public float colorVariance = 0.08f;

    [Tooltip("Prefabs de móveis/props que podem aparecer no quarto (camas, estantes, quadros).")]
    public GameObject[] furniturePrefabs;

    [Tooltip("Máximo de props a tentar colocar por quarto.")]
    [Min(0)] public int maxPropsPerRoom = 4;

    [Tooltip("Chance (0..1) de tentar colocar um prop em uma posição candidata.")]
    [Range(0f,1f)] public float propPlacementChance = 0.4f;

    [Header("Post-process / perf tuning")]
    [Tooltip("Budget em milissegundos por frame para a etapa de pós-processamento (colorização e props).")]
    public int postProcessFrameBudgetMilliseconds = 8;

    [Tooltip("Máximo de voxels processados por batch se preferir limitar por contagem (opcional).")]
    public int postProcessMaxPerBatch = 512;

    // ------------ internals ------------
    // O _rendererCache foi removido e substituído pela lógica do VoxelCache.

    // material property block local (evita alocar repetidamente)
    private static MaterialPropertyBlock _localMPB;

    // Para evitar múltiplas coroutines concorrentes no mesmo room
    private HashSet<RoomInstance> _processingRooms = new HashSet<RoomInstance>();

    private void Awake()
    {
        if (_localMPB == null) _localMPB = new MaterialPropertyBlock();

        // assegura que o generator (base) invoque o evento que vamos escutar
        // registramos o listener local que fará pós-processamento quando a sala terminar de ser populada.
        this.OnRoomPopulated += HandleRoomPopulated;
    }

    private void OnDestroy()
    {
        // limpa assinatura para evitar memory leaks
        this.OnRoomPopulated -= HandleRoomPopulated;
    }

    // Se quiser sobrescrever tamanhos específicos para quartos
    public override Vector2Int GetRandomSize(System.Random rng)
    {
        // quartos mais compactos e com proporções típicas
        int w = rng.Next(Mathf.Max(3, minRoomSize.x), Mathf.Max(minRoomSize.x+1, Mathf.Min(10, maxRoomSize.x)));
        int d = rng.Next(Mathf.Max(3, minRoomSize.y), Mathf.Max(minRoomSize.y+1, Mathf.Min(8, maxRoomSize.y)));
        return new Vector2Int(w, d);
    }

    // Handler invocado pelo BaseRoomGenerator quando a sala foi totalmente populada
    private void HandleRoomPopulated(RoomInstance room)
    {
        // safety
        if (room == null) return;
        if (_processingRooms.Contains(room)) return;

        // inicia pós-processamento (colorização + props) de forma time-sliced
        StartCoroutine(PostProcessRoomCoroutine(room));
    }

    // Coroutine que aplica cor e coloca props, respeitando budget de tempo por frame.
    private IEnumerator PostProcessRoomCoroutine(RoomInstance room)
    {
        if (room == null) yield break;
        _processingRooms.Add(room);

        // --- Preparação ---
        // Determina cor base com variação
        Color baseColor = wallBaseColor;
        float v = (float)Rng.NextDouble() * colorVariance * 2f - colorVariance; // ATUALIZADO: _rng -> Rng
        baseColor.r = Mathf.Clamp01(baseColor.r + v);
        baseColor.g = Mathf.Clamp01(baseColor.g + v);
        baseColor.b = Mathf.Clamp01(baseColor.b + v);

        // Lista local dos voxels (copiamos referências - room.spawnedVoxels pode ser modificado durante pooling, mas na prática está estável)
        var voxels = room.spawnedVoxels;
        if (voxels == null || voxels.Count == 0)
        {
            _processingRooms.Remove(room);
            yield break;
        }

        // Time budget
        float budgetSec = Mathf.Max(1, postProcessFrameBudgetMilliseconds) / 1000f;
        float batchStart = Time.realtimeSinceStartup;
        int processedThisBatch = 0;

        ProfilerBeginSampleSafe("Bedroom_PostProcess_Colorize");

        // --- Colorização dos voxels (usando VoxelCache) ---
        for (int i = 0; i < voxels.Count; i++)
        {
            var go = voxels[i];
            if (go == null) continue;

            // Use VoxelCache to apply color efficiently (avoids GetComponent each time).
            try
            {
                var voxelCache = VoxelCache.GetOrAdd(go, ensureAutoInit: true);
                // small jitter per-voxel to break repetition
                int id = go.GetInstanceID();
                float jitter = ((float)((id * 97) & 255) / 255f - 0.5f) * (colorVariance * 0.5f);
                Color voxelColor = new Color(
                    Mathf.Clamp01(baseColor.r + jitter),
                    Mathf.Clamp01(baseColor.g + jitter),
                    Mathf.Clamp01(baseColor.b + jitter),
                    1f
                );

                voxelCache.ApplyColor(voxelColor);
            }
            catch (Exception)
            {
                // Fallback: se algo falhar no cache, tente o caminho antigo (silencioso)
                var rend = go.GetComponent<Renderer>();
                if (rend != null)
                {
                    var mpb = new MaterialPropertyBlock();
                    if (rend.sharedMaterial != null && rend.sharedMaterial.HasProperty("_BaseColor"))
                        mpb.SetColor("_BaseColor", baseColor);
                    else
                        mpb.SetColor("_Color", baseColor);
                    rend.SetPropertyBlock(mpb);
                }
            }

            // respect time-slicing logic afterwards (unchanged)...
            processedThisBatch++;
            if (postProcessMaxPerBatch > 0 && processedThisBatch >= postProcessMaxPerBatch)
            {
                processedThisBatch = 0;
                batchStart = Time.realtimeSinceStartup;
                yield return null;
            }
            if ((Time.realtimeSinceStartup - batchStart) >= budgetSec)
            {
                processedThisBatch = 0;
                batchStart = Time.realtimeSinceStartup;
                yield return null;
            }
        }

        ProfilerEndSampleSafe();

        // --- Colocação de props ---
        ProfilerBeginSampleSafe("Bedroom_PostProcess_PlaceProps");

        // determina candidatos de posicionamento (simples: pontos no chão próximos ao centro e nas paredes)
        var candidates = new List<Vector3>(64);
        Vector3 roomOrigin = GridToWorld(room.originGrid);

        // floor cell positions
        int w = room.size.x;
        int d = room.size.y;
        for (int x = 0; x < w; x++)
            for (int z = 0; z < d; z++)
            {
                // skip immediate perimeter if preferir
                bool isPerimeter = (x == 0 || x == w - 1 || z == 0 || z == d - 1);
                Vector3 p = roomOrigin + new Vector3(x * voxelSize, 0f, z * voxelSize);
                // prefer interior points (lower chance for perimeter)
                if (!isPerimeter || UnityEngine.Random.value > 0.7f) candidates.Add(p);
            }

        // embaralha candidatos (Fisher-Yates)
        for (int i = 0; i < candidates.Count; i++)
        {
            int j = UnityEngine.Random.Range(i, candidates.Count);
            var tmp = candidates[i]; candidates[i] = candidates[j]; candidates[j] = tmp;
        }

        int placedProps = 0;
        float placeBatchStart = Time.realtimeSinceStartup;
        int placeProcessed = 0;

        for (int i = 0; i < candidates.Count && placedProps < maxPropsPerRoom; i++)
        {
            if (furniturePrefabs == null || furniturePrefabs.Length == 0) break;
            if (UnityEngine.Random.value > propPlacementChance) continue;

            // spawn random furniture prefab
            var prefab = furniturePrefabs[UnityEngine.Random.Range(0, furniturePrefabs.Length)];
            if (prefab == null) continue;

            Vector3 pos = candidates[i] + new Vector3(0f, 0f, 0f); // em cima do chão; o prefab deve ter pivot adequado
            Quaternion rot = Quaternion.identity;

            // opcional: se a posição for próxima a uma parede, rotacione o prop para encarar a sala
            // detecta distância à borda mais próxima
            float left = (pos.x - roomOrigin.x) / voxelSize;
            float right = ((roomOrigin.x + (w - 1) * voxelSize) - pos.x) / voxelSize;
            float front = (pos.z - roomOrigin.z) / voxelSize;
            float back = ((roomOrigin.z + (d - 1) * voxelSize) - pos.z) / voxelSize;
            float minDist = Mathf.Min(left, right, front, back);

            if (minDist <= 1.2f)
            {
                if (minDist == left) rot = Quaternion.Euler(0f, 90f, 0f);
                else if (minDist == right) rot = Quaternion.Euler(0f, -90f, 0f);
                else if (minDist == front) rot = Quaternion.Euler(0f, 0f, 0f);
                else rot = Quaternion.Euler(0f, 180f, 0f);
            }
            else
            {
                rot = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f);
            }

            var spawned = SpawnFromPool(prefab, pos + new Vector3(0f, 0f, 0f), rot, room.container);
            if (spawned != null)
            {
                room.spawnedProps.Add(spawned);
                placedProps++;
            }

            placeProcessed++;
            if ((Time.realtimeSinceStartup - placeBatchStart) >= budgetSec || placeProcessed >= postProcessMaxPerBatch)
            {
                placeProcessed = 0;
                placeBatchStart = Time.realtimeSinceStartup;
                yield return null;
            }
        }

        ProfilerEndSampleSafe();

        // finalizações
        _processingRooms.Remove(room);

        yield break;
    }

    // ---------- Utilities ----------
    // Pequenas wrappers para inserir Profiler samples sem obrigar a diretiva UNITY_PROFILER
    private void ProfilerBeginSampleSafe(string name)
    {
#if UNITY_PROFILER || UNITY_EDITOR
        try { UnityEngine.Profiling.Profiler.BeginSample(name); } catch { }
#endif
    }
    private void ProfilerEndSampleSafe()
    {
#if UNITY_PROFILER || UNITY_EDITOR
        try { UnityEngine.Profiling.Profiler.EndSample(); } catch { }
#endif
    }
}