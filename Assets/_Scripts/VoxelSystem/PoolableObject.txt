// Assets/_Scripts/VoxelSystem/PoolableObject.cs
// PR-03 — Ponte entre instâncias e o VoxelPool (reset leve + binding PrefabId).
// Funções principais:
//  1) Garante VoxelCache no Awake e tenta vincular o PrefabId usando OriginalPrefab quando possível.
//  2) Implementa hooks IPoolable com resets idempotentes e baratos (sem alocação):
//     - OnBeforeSpawn: garante binding do PrefabId e zera dinâmica do Rigidbody.
//     - OnAfterSpawn : (no-op seguro) — ponto para efeitos visuais se necessário.
//     - OnBeforeDespawn: pausa efeitos, zera física e prepara para SetActive(false).
//     - OnAfterDespawn : (no-op).
//  3) Compatível com SearchChildren opcional do VoxelPool (sem depender disso).

using System;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class PoolableObject : MonoBehaviour, IPoolable
{
    [Header("PR-03 — Binding do Prefab")]
    [Tooltip("Prefab de origem desta instância. Usado para registrar/descobrir o PrefabId no VoxelPool.")]
    public GameObject OriginalPrefab;

    [Header("Resets (leves)")]
    [Tooltip("Zerar velocidade/rotação do Rigidbody no spawn/despawn.")]
    public bool resetRigidbody = true;

    [Tooltip("Reabilitar todos os Colliders no spawn.")]
    public bool enableCollidersOnSpawn = true;

    [Tooltip("Reabilitar todos os Renderers no spawn.")]
    public bool enableRenderersOnSpawn = true;

    // Caches internos (evita GetComponent por frame)
    private Rigidbody _rb;
    private Collider[] _colliders;
    private Renderer[] _renderers;
    private AudioSource[] _audioSources;
    private ParticleSystem[] _particles;
    private VoxelCache _cache;

    private void Awake()
    {
        // Cache de componentes (em toda a hierarquia do objeto)
        TryCacheComponents();

        // Garante VoxelCache e tenta definir PrefabId se possível
        EnsureVoxelCache();
        TryBindPrefabId();
    }

    // ---------------------------------------------------------
    // IPoolable Hooks
    // ---------------------------------------------------------
    // Ordem chamada pelo VoxelPool:
    //  - OnBeforeSpawn()  [objeto ainda INATIVO]
    //  - SetActive(true)
    //  - OnAfterSpawn()   [objeto ATIVO]
    //  - ...
    //  - OnBeforeDespawn()[objeto ATIVO, prestes a desativar]
    //  - SetActive(false)
    //  - OnAfterDespawn() [objeto INATIVO]

    public void OnBeforeSpawn()
    {
        // Garantir cache
        if (!_cache) EnsureVoxelCache();

        // Tenta vincular o PrefabId (caso cena foi criada no Editor sem pool)
        TryBindPrefabId();

        if (resetRigidbody) ResetRigidbody();

        if (enableCollidersOnSpawn && _colliders != null)
            for (int i = 0; i < _colliders.Length; i++)
                if (_colliders[i]) _colliders[i].enabled = true;

        if (enableRenderersOnSpawn && _renderers != null)
            for (int i = 0; i < _renderers.Length; i++)
                if (_renderers[i]) _renderers[i].enabled = true;
    }

    public void OnAfterSpawn()
    {
        // No-op seguro. Ponto de extensão para FX (se desejar).
        // Mantemos vazio para evitar custo extra por padrão.
    }

    public void OnBeforeDespawn()
    {
        // Pausar/limpar efeitos ativos antes de desativar
        if (_audioSources != null)
            for (int i = 0; i < _audioSources.Length; i++)
                if (_audioSources[i]) _audioSources[i].Stop();

        if (_particles != null)
            for (int i = 0; i < _particles.Length; i++)
                if (_particles[i]) _particles[i].Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        if (resetRigidbody) ResetRigidbody();
    }

    public void OnAfterDespawn()
    {
        // No-op. Objeto já inativo.
    }

    // ---------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------

    private void TryCacheComponents()
    {
        // Cache leve — evita repetir GetComponentsInChildren
        _rb = GetComponent<Rigidbody>();
        _colliders = GetComponentsInChildren<Collider>(true);
        _renderers = GetComponentsInChildren<Renderer>(true);
        _audioSources = GetComponentsInChildren<AudioSource>(true);
        _particles = GetComponentsInChildren<ParticleSystem>(true);
    }

    private void EnsureVoxelCache()
    {
        if (!TryGetComponent(out _cache))
            _cache = gameObject.AddComponent<VoxelCache>();
        _cache.EnsureCached();
    }

    /// <summary>
    /// Vincula o PrefabId no VoxelCache usando o VoxelPool quando possível.
    /// Se já houver um PrefabId válido, não faz nada.
    /// </summary>
    private void TryBindPrefabId()
    {
        if (_cache == null) return;

        // Já tem id válido?
        if (_cache.PrefabId > 0) return;

        // Sem prefab de origem, não há o que registrar
        if (OriginalPrefab == null) return;

        var pool = VoxelPool.Instance;
        if (pool == null) return;

        try
        {
            int id = pool.RegisterPrefab(OriginalPrefab, maxPoolSize: 512, prewarm: 0);
            if (id > 0)
                _cache.SetPrefabId(id);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[PoolableObject] Falha ao registrar prefab no VoxelPool: {ex.Message}");
        }
    }

    private void ResetRigidbody()
    {
        if (_rb)
        {
            _rb.velocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
            // Opcional: limpar forças dormindo
            _rb.Sleep();
        }
    }

#if UNITY_EDITOR
    // Para cenas montadas no Editor: se alterarmos OriginalPrefab no Inspector,
    // podemos tentar pré-vincular o PrefabId (quando existir um VoxelPool na cena).
    private void OnValidate()
    {
        if (!Application.isPlaying)
        {
            if (_cache == null) _cache = GetComponent<VoxelCache>();
            if (_cache == null) _cache = gameObject.AddComponent<VoxelCache>();
            _cache.EnsureCached();

            var pool = FindObjectOfType<VoxelPool>();
            if (pool != null && OriginalPrefab != null)
            {
                try
                {
                    int id = pool.RegisterPrefab(OriginalPrefab, maxPoolSize: 512, prewarm: 0);
                    if (id > 0) _cache.SetPrefabId(id);
                }
                catch { /* silencioso no editor */ }
            }
        }
    }
#endif
}
//