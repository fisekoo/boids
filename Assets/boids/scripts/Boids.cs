using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using System.Runtime.InteropServices;
using Random = UnityEngine.Random;

// Preferrably want to have all buffer structs in power of 2...
// sizeof(float) = 4
// (3 + 3 + 1 + 1) * (sizeof(float)) = 32
[StructLayout(LayoutKind.Sequential)]
public struct Boid
{
    public float3 position;
    public float3 direction;
    public float noise_offset;
    public float rd_scale;
    public const int STRIDE_SIZE = (3 + 3 + 1 + 1) * sizeof(float);
}
[System.Serializable]
public struct Predator : IEquatable<Predator>
{
    public Transform Transform;
    public float Radius;

    public Predator(Transform transform, float radius)
    {
        Transform = transform;
        Radius = radius;
    }
    public bool Equals(Predator other)
    {
        return Equals(Transform, other.Transform) && Radius.Equals(other.Radius);
    }

    public override bool Equals(object obj)
    {
        return obj is Predator other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Transform, Radius);
    }
}
public class Boids : MonoBehaviour
{
    [SerializeField] private ComputeShader boidCS;
    private ComputeShader _boidCSInstance;
    [SerializeField] private Transform target;

    // TODO: data from scriptable object
    [SerializeField] private Mesh boidMesh;
    [SerializeField] private Material boidMaterial;
    [SerializeField] private int boidCount = 32;
    [SerializeField] private float spawnRadius = 1.5f;
    [SerializeField] private float boidSpeed = 7f, speedVariation = 0.1f;
    [SerializeField] private Vector2 boidMinMaxScale = new Vector2(1f, 2f);
    [SerializeField][Range(0, 5f)] private float seperationFactor = 0.5f;
    [SerializeField][Range(0, 1f)] private float alignmentFactor = 1;
    [SerializeField][Range(0, 1f)] private float cohesionFactor = 1;
    [SerializeField] private float orbitSpeed = 2.92f;
    [SerializeField] private List<Predator> predators;
    [SerializeField] private float predatorScare = 7;
    private int _boidKernelID;
    private ComputeBuffer _drawArgsBuffer;
    private ComputeBuffer _boidBuffer;
    private ComputeBuffer _predatorBuffer;
    private Boid[] _boids;
    private float4[] _predatorsArray;
    private uint _cachedPredatorsCount;
    private const int GROUP_SIZE = 256;
    private RenderParams _rparams;
    private bool _isInitialized;
    void Awake()
    {
        CreateInstance();
        _boidKernelID = _boidCSInstance.FindKernel("CSMain");

        _drawArgsBuffer = new ComputeBuffer(1, 5 * sizeof(uint), ComputeBufferType.IndirectArguments);
        // 0 == number of triangle indices, 1 == population, others are only relevant if drawing submeshes.
        _drawArgsBuffer.SetData(new uint[] { boidMesh.GetIndexCount(0), (uint)boidCount, 0, 0, 0 });

        _boids = new Boid[boidCount];
        for (int i = 0; i < boidCount; i++)
        {
            _boids[i] = CreateBoid();
        }
        _boidBuffer = new ComputeBuffer(boidCount, Boid.STRIDE_SIZE);
        _boidBuffer.SetData(_boids);

        _cachedPredatorsCount = (uint)predators.Count;
        _predatorsArray = new float4[_cachedPredatorsCount];
        CreatePredatorBuffer(_cachedPredatorsCount);
        UpdatePredatorPositions();

        _rparams = new RenderParams
        {
            material = boidMaterial,
            worldBounds = new Bounds(Vector3.zero, Vector3.one * 1000),
            shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off
        };
        _isInitialized = true;
    }
    private void OnValidate()
    {
        if (_isInitialized == false) return;
        Start();
    }
    private void Start()
    {
        SetComputeData();
        SetComputeUpdateData();
        SetMaterialData();
    }
    private void Update()
    {
        SetComputeUpdateData();
        _boidCSInstance.Dispatch(_boidKernelID, (int)math.ceil(boidCount / GROUP_SIZE + 1), 1, 1);
        GL.Flush();
    }
    private void LateUpdate()
    {
        Graphics.DrawMeshInstancedIndirect(boidMesh, 0, _rparams.material, _rparams.worldBounds, _drawArgsBuffer);
    }
    private void CreateInstance()
    {
        if (boidCS == null) return;
        _boidCSInstance = Instantiate(boidCS);
    }
    private void DestroyInstance()
    {
        if (_boidCSInstance == null) return;
        if (Application.isPlaying)
        {
            Destroy(_boidCSInstance);
        }
        else
        {
            DestroyImmediate(_boidCSInstance);
        }
    }
    private Boid CreateBoid()
    {
        var b = new Boid();
        b.position = transform.position + Random.insideUnitSphere * spawnRadius;
        b.direction = Quaternion.Slerp(transform.rotation, Random.rotation, 0.3f).eulerAngles;
        b.noise_offset = Random.value * 1000f;
        b.rd_scale = math.max(boidMinMaxScale.x, Random.value * boidMinMaxScale.y);
        return b;
    }

    private void SetComputeData()
    {
        _boidCSInstance.SetFloat("_Speed", boidSpeed);
        _boidCSInstance.SetFloat("_SpeedVariation", speedVariation);
        _boidCSInstance.SetFloat("_OrbitSpeed", orbitSpeed);
        _boidCSInstance.SetFloat("_SeperationFactor", seperationFactor);
        _boidCSInstance.SetFloat("_AlignmentFactor", alignmentFactor);
        _boidCSInstance.SetFloat("_CohesionFactor", cohesionFactor);
        _boidCSInstance.SetFloat("_PredatorScare", predatorScare);
        _boidCSInstance.SetInt("_BoidCount", boidCount);
        _boidCSInstance.SetInt("_PredatorCount", _predatorsArray.Length);
        _boidCSInstance.SetBuffer(_boidKernelID, "_BoidsBuffer", _boidBuffer);
        _boidCSInstance.SetBuffer(_boidKernelID, "_PredatorsBuffer", _predatorBuffer);
    }
    private void SetComputeUpdateData()
    {
        _boidCSInstance.SetFloat("_DeltaTime", Time.deltaTime);
        _boidCSInstance.SetVector("_TargetPosition", target.position);
        if (_predatorsArray.Length <= 0) return;
        UpdatePredatorPositions();
    }
    private void UpdatePredatorPositions()
    {
        if (_cachedPredatorsCount != predators.Count) CreatePredatorBuffer((uint)predators.Count);
        for (int i = 0; i < _predatorsArray.Length; i++)
        {
            _predatorsArray[i].xyz = predators[i].Transform.position;
            _predatorsArray[i].w = predators[i].Radius;
        }
        _predatorBuffer.SetData(_predatorsArray);
        _boidCSInstance.SetBuffer(_boidKernelID, "_PredatorsBuffer", _predatorBuffer);
    }
    private void CreatePredatorBuffer(uint count)
    {
        _predatorBuffer?.Dispose();
        Array.Resize(ref _predatorsArray, (int)count);
        _predatorBuffer = new ComputeBuffer(_predatorsArray.Length, 4 * sizeof(float));
        _boidCSInstance.SetInt("_PredatorCount", _predatorsArray.Length);
        _cachedPredatorsCount = count;
    }
    public void AddPredator(Predator predator)
    {
        predators.Add(predator);
    }
    public void RemovePredator(Predator predator)
    {
        predators.Remove(predator);
    }
    private void SetMaterialData()
    {
        boidMaterial.SetBuffer("_BoidsBuffer", _boidBuffer);
    }
    private void OnDestroy()
    {
        _boidBuffer?.Dispose();
        _predatorBuffer?.Dispose();
        _drawArgsBuffer?.Dispose();
        DestroyInstance();
    }
}