using UnityEngine;

/// <summary>
/// Componente "etiqueta" para objetos gerenciados pelo VoxelPool.
/// Guarda referência ao prefab de origem e expõe hooks opcionais via IPoolable.
/// </summary>
public class PoolableObject : MonoBehaviour, IPoolable
{
    // Mantido como CAMPO público (não propriedade) para aparecer no Inspector.
    public GameObject OriginalPrefab;

    // ---- IPoolable (hooks opcionais – implementações vazias) ----
    public void OnBeforeSpawn() { }
    public void OnAfterSpawn()  { }
    public void OnBeforeDespawn() { }
    public void OnAfterDespawn()  { }
}
