using UnityEngine;
using UnityEngine.InputSystem;

// 3D fluid on the GPU (FluidSim3D.compute): the GpuCircle pipeline with 3D kernels, a 27-cell spatial hash and an
// axis-aligned box, following the 3D part of Sebastian Lague's video. Particles are drawn as camera-facing billboards
// shaded as spheres. Defaults are Lague's 3D scene values (radius 0.2, target 630, pressure 288, near 2.16).
public class GpuSphere : MonoBehaviour
{
    public ComputeShader compute;
    public Material instancedMaterial;

    [Header("Spawn (cube)")]
    [Tooltip("Particles per unit volume; particles per axis = round(size * cbrt(density))")]
    public float spawnDensity = 286f;
    public Vector3 spawnSize = new Vector3(7f, 7f, 14f);   // Lague's two 7-unit cubes side by side
    public Vector3 spawnCentre = new Vector3(-8.3f, -1.3f, 0f);
    public float jitterStrength = 0.035f;

    [Header("Simulation")]
    public Vector3 boundsSize = new Vector3(24f, 10f, 15f);
    public float particleRadius = 0.04f;
    public float smoothingRadius = 0.2f;
    public float targetDensity = 630f;
    public float gravity = 10f;
    [Range(0f, 1f)] public float collisionDamping = 0.95f;
    public float pressureMultiplier = 288f;
    public float nearPressureMultiplier = 2.16f;
    public float viscosityStrength = 0f;
    public int subSteps = 3;

    [Header("Visualization")]
    public Gradient speedGradient = new Gradient
    {
        colorKeys = new[]
        {
            new GradientColorKey(Color.blue,   0f),
            new GradientColorKey(Color.cyan,   0.33f),
            new GradientColorKey(Color.yellow, 0.66f),
            new GradientColorKey(Color.red,    1f)
        },
        alphaKeys = new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) }
    };
    public float maxSpeed = 8f;
    public bool showInfo = true;

    const int ExternalForcesKernel = 0, SpatialHashKernel = 1, CountKernel = 2, ScanKernel = 3, ScatterKernel = 4,
              DensityKernel = 5, PressureKernel = 6, ViscosityKernel = 7, UpdatePositionsKernel = 8, ColorsKernel = 9;
    const int KernelCount = 10;
    const int ThreadGroupSize = 64;
    const int GradientSamples = 64;

    private int _numParticles;
    private ComputeBuffer _positions, _predicted, _velocities, _densities, _spatialIndices, _unsortedEntries, _spatialOffsets, _colors, _gradient;
    private Vector4[] _spawnPositions;
    private Vector3[] _spawnPositions3;
    private Vector3[] _zeroVelocities;
    private Vector4[] _gradientSamples = new Vector4[GradientSamples];
    private Mesh _quadMesh;
    private Bounds _renderBounds;
    private bool _paused = true;

    void Start()
    {
        _quadMesh     = Circle.CreateQuadMesh();
        _renderBounds = new Bounds(Vector3.zero, boundsSize + Vector3.one * smoothingRadius * 2f);
        DrawBoundsBox(Circle.CreateLine("BoundsBox", Color.green, 16, loop: false), boundsSize);
        CreateBuffers();
    }

    // One LineRenderer path that walks all 12 edges of the box
    static void DrawBoundsBox(LineRenderer line, Vector3 size)
    {
        Vector3 h = size * 0.5f;
        Vector3 P(int x, int y, int z) => new Vector3(x * h.x, y * h.y, z * h.z);
        line.SetPositions(new[]
        {
            P(-1,-1,-1), P(1,-1,-1), P(1,-1,1), P(-1,-1,1), P(-1,-1,-1),   // bottom
            P(-1,1,-1), P(1,1,-1), P(1,1,1), P(-1,1,1), P(-1,1,-1),        // top
            P(1,1,-1), P(1,-1,-1), P(1,-1,1), P(1,1,1), P(-1,1,1), P(-1,-1,1) // remaining verticals
        });
    }

    void CreateBuffers()
    {
        ReleaseBuffers();

        // Lague's Spawner3D: a block of particles at spawnDensity per unit volume, with jitter
        float perUnit = Mathf.Pow(spawnDensity, 1f / 3f);
        int nx = Mathf.Max(1, Mathf.RoundToInt(spawnSize.x * perUnit));
        int ny = Mathf.Max(1, Mathf.RoundToInt(spawnSize.y * perUnit));
        int nz = Mathf.Max(1, Mathf.RoundToInt(spawnSize.z * perUnit));
        _numParticles = nx * ny * nz;
        _spawnPositions  = new Vector4[_numParticles];
        _spawnPositions3 = new Vector3[_numParticles];
        _zeroVelocities  = new Vector3[_numParticles];
        int i = 0;
        for (int x = 0; x < nx; x++)
        for (int y = 0; y < ny; y++)
        for (int z = 0; z < nz; z++)
        {
            Vector3 t = new Vector3(nx > 1 ? x / (nx - 1f) : 0.5f, ny > 1 ? y / (ny - 1f) : 0.5f, nz > 1 ? z / (nz - 1f) : 0.5f);
            Vector3 p = spawnCentre + Vector3.Scale(t - Vector3.one * 0.5f, spawnSize) + Random.insideUnitSphere * jitterStrength;
            _spawnPositions3[i] = p;
            _spawnPositions[i]  = new Vector4(p.x, p.y, p.z, 0f);
            i++;
        }

        _positions      = new ComputeBuffer(_numParticles, sizeof(float) * 4);
        _predicted      = new ComputeBuffer(_numParticles, sizeof(float) * 3);
        _velocities     = new ComputeBuffer(_numParticles, sizeof(float) * 3);
        _densities      = new ComputeBuffer(_numParticles, sizeof(float) * 2);
        _spatialIndices = new ComputeBuffer(_numParticles, sizeof(uint) * 3);
        _unsortedEntries = new ComputeBuffer(_numParticles, sizeof(uint) * 3);
        _spatialOffsets = new ComputeBuffer(_numParticles, sizeof(uint));
        _colors         = new ComputeBuffer(_numParticles, sizeof(float) * 4);
        _gradient       = new ComputeBuffer(GradientSamples, sizeof(float) * 4);
        ResetParticles();

        for (int k = 0; k < KernelCount; k++)
        {
            compute.SetBuffer(k, "Positions",          _positions);
            compute.SetBuffer(k, "PredictedPositions", _predicted);
            compute.SetBuffer(k, "Velocities",         _velocities);
            compute.SetBuffer(k, "Densities",          _densities);
            compute.SetBuffer(k, "SpatialIndices",     _spatialIndices);
            compute.SetBuffer(k, "UnsortedEntries",    _unsortedEntries);
            compute.SetBuffer(k, "SpatialOffsets",     _spatialOffsets);
            compute.SetBuffer(k, "Colors",             _colors);
            compute.SetBuffer(k, "SpeedGradient",      _gradient);
        }
        compute.SetInt("numParticles", _numParticles);

        instancedMaterial.SetBuffer("_Positions", _positions);
        instancedMaterial.SetBuffer("_Colors",    _colors);
    }

    void ResetParticles()
    {
        _positions.SetData(_spawnPositions);
        _predicted.SetData(_spawnPositions3);
        _velocities.SetData(_zeroVelocities);
    }

    void Update()
    {
        if (compute == null || instancedMaterial == null || _positions == null) return;

        Keyboard kb = Keyboard.current;
        bool stepOnce = false;
        if (kb != null)
        {
            if (kb.rKey.wasPressedThisFrame) { CreateBuffers(); _paused = true; } // rebuild so inspector changes (count, spawn) apply
            if (kb.spaceKey.wasPressedThisFrame) _paused = !_paused;
            stepOnce = _paused && kb.rightArrowKey.wasPressedThisFrame;
        }

        if (!_paused || stepOnce)
        {
            float frameDt = _paused ? 1f / 60f : Mathf.Min(Time.deltaTime, 1f / 60f);
            SetSettings(frameDt / subSteps);
            for (int step = 0; step < subSteps; step++)
                RunSimulationStep();
        }

        UpdateColors();
        instancedMaterial.SetFloat("_ParticleRadius", particleRadius);
        Graphics.DrawMeshInstancedProcedural(_quadMesh, 0, instancedMaterial, _renderBounds, _numParticles);
    }

    void SetSettings(float dt)
    {
        compute.SetFloat("deltaTime",              dt);
        compute.SetFloat("gravity",                gravity);
        compute.SetFloat("collisionDamping",       collisionDamping);
        compute.SetFloat("smoothingRadius",        smoothingRadius);
        compute.SetFloat("targetDensity",          targetDensity);
        compute.SetFloat("pressureMultiplier",     pressureMultiplier);
        compute.SetFloat("nearPressureMultiplier", nearPressureMultiplier);
        compute.SetFloat("viscosityStrength",      viscosityStrength);
        compute.SetVector("boundsSize",            boundsSize);
    }

    void RunSimulationStep()
    {
        Dispatch(ExternalForcesKernel);
        Dispatch(SpatialHashKernel);
        Dispatch(CountKernel);
        compute.Dispatch(ScanKernel, 1, 1, 1); // single thread group scans all counts
        Dispatch(ScatterKernel);
        Dispatch(DensityKernel);
        Dispatch(PressureKernel);
        if (viscosityStrength != 0f) Dispatch(ViscosityKernel);
        Dispatch(UpdatePositionsKernel);
    }

    void Dispatch(int kernel) => compute.Dispatch(kernel, Mathf.CeilToInt(_numParticles / (float)ThreadGroupSize), 1, 1);

    void UpdateColors()
    {
        for (int i = 0; i < GradientSamples; i++)
            _gradientSamples[i] = speedGradient.Evaluate(i / (float)(GradientSamples - 1));
        _gradient.SetData(_gradientSamples);
        compute.SetFloat("maxSpeed", maxSpeed);
        Dispatch(ColorsKernel);
    }

    void OnGUI()
    {
        if (_paused) GUI.Label(new Rect(10, 30, 400, 20), "PAUSED  (Space: run, Right arrow: step, R: restart)");
        if (showInfo) GUI.Label(new Rect(10, 10, 400, 20), $"GPU 3D   Particles: {_numParticles}   Box: {boundsSize.x} x {boundsSize.y} x {boundsSize.z}");
    }

    void ReleaseBuffers()
    {
        _positions?.Release(); _predicted?.Release(); _velocities?.Release(); _densities?.Release();
        _spatialIndices?.Release(); _unsortedEntries?.Release(); _spatialOffsets?.Release(); _colors?.Release(); _gradient?.Release();
        _positions = null;
    }

    void OnDestroy() => ReleaseBuffers();
}
