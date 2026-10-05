using UnityEngine;
using UnityEngine.InputSystem;

// GPU version of Circle: the same simulation run as compute shader kernels (FluidSim2D.compute), with neighbour search
// via spatial hash + bitonic merge sort, as in Sebastian Lague's video. Circle stays as the CPU reference; enable one
// or the other in the scene. Particle data never leaves the GPU, so the CPU-only debug readouts don't exist here.
// Defaults are Lague's dense 2D scene (Test B: smoothing radius 0.2, target 234) sized for ~20000 particles; the
// CPU component keeps the 4032-particle set (radius 0.35, target 55). Fluid area scales with particles / (target - self density).
public class GpuCircle : MonoBehaviour
{
    public ComputeShader compute;
    public Material instancedMaterial;
    public int numParticles = 20000;
    public float particleRadius = 0.02f;
    [Tooltip("Gap between neighbouring particles in the spawn block")]
    public float spawnGap = 0.015f;
    public float smoothingRadius = 0.2f;
    public float targetDensity = 234f;
    public float gravity = 13f;
    [Range(0f, 1f)] public float collisionDamping = 0.5f;
    public float pressureMultiplier = 225f;
    public float nearPressureMultiplier = 18f;
    public float viscosityStrength = 0.03f;
    public int subSteps = 3;

    [Header("Interaction")]
    public float interactionRadius = 2.5f;
    public float interactionStrength = 75f;

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
    public float maxSpeed = 5f;
    public bool showInfo = false;

    // Kernel indices follow the #pragma kernel order in FluidSim2D.compute
    const int ExternalForcesKernel = 0, SpatialHashKernel = 1, SortKernel = 2, OffsetsKernel = 3,
              DensityKernel = 4, PressureKernel = 5, ViscosityKernel = 6, UpdatePositionsKernel = 7, ColorsKernel = 8;
    const int KernelCount = 9;
    const int ThreadGroupSize = 64;
    const int SortThreadGroupSize = 128;
    const int GradientSamples = 64;

    private ComputeBuffer _positions, _predicted, _velocities, _densities, _spatialIndices, _spatialOffsets, _colors, _gradient;
    private Vector4[] _spawnPositions;
    private Vector2[] _spawnPositions2;
    private Vector2[] _zeroVelocities;
    private Vector4[] _gradientSamples = new Vector4[GradientSamples];
    private Vector2 _boundsSize;
    private Mesh _quadMesh;
    private Bounds _renderBounds;
    private LineRenderer _interactionCircle;
    private bool _paused = true; // start paused; Space to run
    private Vector2 _interactionPos;
    private float _interactionSign;

    void Start()
    {
        float height = Camera.main.orthographicSize * 2f;
        float width  = height * Camera.main.aspect;
        _boundsSize  = new Vector2(width, height);

        _quadMesh     = Circle.CreateQuadMesh();
        _renderBounds = new Bounds(Vector3.zero, new Vector3(width + smoothingRadius * 2f, height + smoothingRadius * 2f, 1f));
        Circle.DrawBoundsLine(Circle.CreateLine("BoundsLine", Color.green, 5, loop: false), _boundsSize);
        _interactionCircle = Circle.CreateLine("InteractionCircle", Color.green, Circle.InteractionCircleSegments, loop: true);
        _interactionCircle.enabled = false;

        CreateBuffers();
    }

    void CreateBuffers()
    {
        ReleaseBuffers();
        _spawnPositions  = Circle.SpawnBlock(numParticles, particleRadius * 2f + spawnGap, _boundsSize);
        _spawnPositions2 = new Vector2[numParticles];
        for (int i = 0; i < numParticles; i++) _spawnPositions2[i] = _spawnPositions[i];
        _zeroVelocities  = new Vector2[numParticles];

        _positions      = new ComputeBuffer(numParticles, sizeof(float) * 4);
        _predicted      = new ComputeBuffer(numParticles, sizeof(float) * 2);
        _velocities     = new ComputeBuffer(numParticles, sizeof(float) * 2);
        _densities      = new ComputeBuffer(numParticles, sizeof(float) * 2);
        _spatialIndices = new ComputeBuffer(numParticles, sizeof(uint) * 3);
        _spatialOffsets = new ComputeBuffer(numParticles, sizeof(uint));
        _colors         = new ComputeBuffer(numParticles, sizeof(float) * 4);
        _gradient       = new ComputeBuffer(GradientSamples, sizeof(float) * 4);
        ResetParticles();

        // Bind every buffer to every kernel; Unity ignores names a kernel doesn't use
        for (int k = 0; k < KernelCount; k++)
        {
            compute.SetBuffer(k, "Positions",          _positions);
            compute.SetBuffer(k, "PredictedPositions", _predicted);
            compute.SetBuffer(k, "Velocities",         _velocities);
            compute.SetBuffer(k, "Densities",          _densities);
            compute.SetBuffer(k, "SpatialIndices",     _spatialIndices);
            compute.SetBuffer(k, "SpatialOffsets",     _spatialOffsets);
            compute.SetBuffer(k, "Colors",             _colors);
            compute.SetBuffer(k, "SpeedGradient",      _gradient);
        }
        compute.SetInt("numParticles", numParticles);

        instancedMaterial.SetBuffer("_Positions", _positions);
        instancedMaterial.SetBuffer("_Colors",    _colors);
    }

    void ResetParticles()
    {
        _positions.SetData(_spawnPositions);
        _predicted.SetData(_spawnPositions2);
        _velocities.SetData(_zeroVelocities);
    }

    void Update()
    {
        if (compute == null || instancedMaterial == null || _positions == null) return;

        // Controls: R restart, Space pause/resume, Right arrow single step while paused
        Keyboard kb = Keyboard.current;
        bool stepOnce = false;
        if (kb != null)
        {
            if (kb.rKey.wasPressedThisFrame) { ResetParticles(); _paused = true; }
            if (kb.spaceKey.wasPressedThisFrame) _paused = !_paused;
            stepOnce = _paused && kb.rightArrowKey.wasPressedThisFrame;
        }

        _interactionSign = Circle.ReadMouse(ref _interactionPos);
        Circle.DrawRing(_interactionCircle, _interactionPos, interactionRadius, _interactionSign);

        if (!_paused || stepOnce)
        {
            // Never advance more than 1/60 s per frame (Lague's maxTimestepFPS): a slow frame runs the sim slower, not coarser
            float frameDt = _paused ? 1f / 60f : Mathf.Min(Time.deltaTime, 1f / 60f);
            SetSettings(frameDt / subSteps);
            for (int step = 0; step < subSteps; step++)
                RunSimulationStep();
        }

        UpdateColors();
        instancedMaterial.SetFloat("_ParticleRadius", particleRadius);
        Graphics.DrawMeshInstancedProcedural(_quadMesh, 0, instancedMaterial, _renderBounds, numParticles);
    }

    void SetSettings(float dt)
    {
        compute.SetFloat("deltaTime",              dt);
        compute.SetFloat("gravity",                gravity);
        compute.SetFloat("collisionDamping",       collisionDamping);
        compute.SetFloat("particleRadius",         particleRadius);
        compute.SetFloat("smoothingRadius",        smoothingRadius);
        compute.SetFloat("targetDensity",          targetDensity);
        compute.SetFloat("pressureMultiplier",     pressureMultiplier);
        compute.SetFloat("nearPressureMultiplier", nearPressureMultiplier);
        compute.SetFloat("viscosityStrength",      viscosityStrength);
        compute.SetVector("boundsSize",            _boundsSize);
        compute.SetVector("interactionInputPoint", _interactionPos);
        compute.SetFloat("interactionInputStrength", interactionStrength * _interactionSign);
        compute.SetFloat("interactionInputRadius", interactionRadius);
    }

    void RunSimulationStep()
    {
        Dispatch(ExternalForcesKernel);
        Dispatch(SpatialHashKernel);
        RunSort();
        Dispatch(OffsetsKernel);
        Dispatch(DensityKernel);
        Dispatch(PressureKernel);
        Dispatch(ViscosityKernel);
        Dispatch(UpdatePositionsKernel);
    }

    void Dispatch(int kernel)
    {
        compute.Dispatch(kernel, Mathf.CeilToInt(numParticles / (float)ThreadGroupSize), 1, 1);
    }

    // Bitonic merge sort of SpatialIndices by key (Lague's GPUSort): every (stage, step) pair is one dispatch
    void RunSort()
    {
        int numPairs  = Mathf.NextPowerOfTwo(numParticles) / 2;
        int numStages = (int)Mathf.Log(numPairs * 2, 2);
        int groups    = Mathf.CeilToInt(numPairs / (float)SortThreadGroupSize);
        for (int stage = 0; stage < numStages; stage++)
        {
            for (int step = 0; step < stage + 1; step++)
            {
                int groupWidth  = 1 << (stage - step);
                int groupHeight = 2 * groupWidth - 1;
                compute.SetInt("groupWidth",  groupWidth);
                compute.SetInt("groupHeight", groupHeight);
                compute.SetInt("stepIndex",   step);
                compute.Dispatch(SortKernel, groups, 1, 1);
            }
        }
    }

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
        if (showInfo) GUI.Label(new Rect(10, 10, 400, 20), $"GPU   Particles: {numParticles}   Bounds: {_boundsSize.x:F1} x {_boundsSize.y:F1}");
    }

    void ReleaseBuffers()
    {
        _positions?.Release(); _predicted?.Release(); _velocities?.Release(); _densities?.Release();
        _spatialIndices?.Release(); _spatialOffsets?.Release(); _colors?.Release(); _gradient?.Release();
        _positions = null;
    }

    void OnDestroy() => ReleaseBuffers();
}
