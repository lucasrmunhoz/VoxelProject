// AUTOR: Gemini
// DATA: 16 de Agosto de 2025
// PROPÓSITO: Servir como uma "etiqueta" para objetos gerenciados pelo VoxelPool. (VERSÃO CORRIGIDA)
// DESCRIÇÃO: Este script agora usa um campo público para ser visível no Inspector do Unity.

using UnityEngine;

/// <summary>
/// Um componente "etiqueta" que identifica um GameObject como sendo reciclável
/// e guarda uma referência ao seu prefab de origem para o sistema VoxelPool.
/// </summary>
public class PoolableObject : MonoBehaviour
{
    // #################### CORREÇÃO AQUI ####################
    // Trocamos a "propriedade" { get; set; } por um "campo" público simples,
    // que é o que o Inspector do Unity consegue exibir.
    public GameObject OriginalPrefab;
    // ######################################################
}