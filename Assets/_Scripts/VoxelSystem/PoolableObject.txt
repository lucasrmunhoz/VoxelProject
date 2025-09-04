// PoolableObject.cs
using UnityEngine;

/// <summary>
/// Interface de hooks de ciclo de vida chamada pelo VoxelPool.
/// </summary>
public interface IPoolable
{
    /// <summary>Chamado com o objeto ainda INATIVO, logo antes de ser ativado.</summary>
    void OnBeforeSpawn();

    /// <summary>Chamado imediatamente após o objeto ser ATIVADO.</summary>
    void OnAfterSpawn();

    /// <summary>Chamado logo antes do objeto ser DESATIVADO e devolvido ao pool.</summary>
    void OnBeforeDespawn();

    /// <summary>Chamado após o objeto ser DESATIVADO e reparentado ao bin.</summary>
    void OnAfterDespawn();
}

/// <summary>
/// Implementação padrão e leve de IPoolable, focada em resets de estado.
/// NÃO faz ativação/desativação nem reparent — isso é do VoxelPool.
/// Anexe apenas onde você precisa de resets específicos.
/// </summary>
[DisallowMultipleComponent]
[AddComponentMenu("Voxel Nightmare/Poolable Object")]
public sealed class PoolableObject : MonoBehaviour, IPoolable
{
    [Header("Resets Opcionais")]
    [Tooltip("Zera velocidade e rotação do Rigidbody (se houver) ao spawn.")]
    public bool resetRigidbodyOnSpawn = true;

    [Tooltip("Para ParticleSystems no despawn.")]
    public bool stopParticlesOnDespawn = true;

    [Tooltip("Para AudioSources no despawn.")]
    public bool stopAudioOnDespawn = true;

    // Caches opcionais (evitam GetComponent/TryGetComponent repetido)
    private Rigidbody _rb;
    private ParticleSystem[] _particles;
    private AudioSource[] _audios;
    private bool _cached;

    private void CacheIfNeeded()
    {
        if (_cached) return;
        TryGetComponent(out _rb);
        _particles = GetComponentsInChildren<ParticleSystem>(true);
        _audios = GetComponentsInChildren<AudioSource>(true);
        _cached = true;
    }

    public void OnBeforeSpawn()
    {
        CacheIfNeeded();

        if (resetRigidbodyOnSpawn && _rb != null)
        {
            _rb.velocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
        }
        // Outros resets de estado visual/flags poderiam ir aqui, se necessário.
    }

    public void OnAfterSpawn()
    {
        // Ex.: reiniciar estados que dependem de estar ativo.
        // (Vazio por padrão)
    }

    public void OnBeforeDespawn()
    {
        // O objeto ainda está ATIVO aqui: ótimo para parar efeitos limpos.
        CacheIfNeeded();

        if (stopParticlesOnDespawn && _particles != null)
        {
            for (int i = 0; i < _particles.Length; i++)
            {
                var ps = _particles[i];
                if (!ps) continue;
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
        }

        if (stopAudioOnDespawn && _audios != null)
        {
            for (int i = 0; i < _audios.Length; i++)
            {
                var a = _audios[i];
                if (!a) continue;
                a.Stop();
            }
        }
    }

    public void OnAfterDespawn()
    {
        // Já desativado/reparentado. Espaço para limpesa adicional se desejar.
        // (Vazio por padrão)
    }
}
