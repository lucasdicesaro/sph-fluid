using UnityEngine;
using UnityEngine.InputSystem;
using System.Threading.Tasks;
using Stopwatch = System.Diagnostics.Stopwatch;

[ExecuteAlways]
public class Circle : MonoBehaviour
{
    public Material instancedMaterial;
    public Material pressureFieldMaterial;
    public int numParticles = 4032;
    public float particleRadius = 0.05f;
    [Tooltip("Gap between neighbouring particles in the spawn block")]
    public float spawnGap = 0.02f;
    public float smoothingRadius = 0.35f;
    public float particleMass = 1f;
    public float targetDensity = 55f;
    private Vector2 boundsSize;

    public float gravity = 10f;
    [Range(0f, 1f)] public float collisionDamping = 0.95f;
    public float pressureMultiplier = 500f;
    [Tooltip("Repulsion from a sharper 'near density' with no target: stops particles pairing up or clumping")]
    public float nearPressureMultiplier = 18f;
    [Range(0f, 1f)] public float damping = 0f;
    [Tooltip("Pulls each particle's velocity toward its neighbours'. Damps relative motion only, so the fluid flows coherently")]
    public float viscosityStrength = 0.06f;
    public int subSteps = 5;

    [Header("Interaction")]
    [Tooltip("Radius around the cursor that is affected")]
    public float interactionRadius = 3f;
    [Tooltip("Left click pulls particles in, right click pushes them away")]
    public float interactionStrength = 200f;

    [Header("Debug")]
    public int vectorFieldResolution = 20;
    public float vectorFieldScale = 0.3f;
    [Tooltip("Show sim timing, bounds and the density under the cursor")]
    public bool showDensityMs = false;

    [Header("Visualization")]
    [Tooltip("Particle colour by speed: left = stationary, right = maxSpeed")]
    public Gradient speedGradient = new Gradient
    {
        // Heat map: blue -> cyan -> yellow -> red; interpolation fills in the greens and oranges between the 4 markers
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
    public Color negativePressureColor = Color.black;
    public Color positivePressureColor = Color.black;
    public Color zeroPressureColor = Color.black;

    private Vector4[] _positions;
    private Vector4[] _predicted;   // positions one lookahead step ahead; densities, forces and the grid use these
    const float PredictionTime = 1f / 120f; // Lague's fixed lookahead, independent of dt
    private Vector2[] _velocities;
    private float[]   _densities;
    private float[]   _nearDensities;
    private Vector2[] _accelerations; // pressure + viscosity + interaction, computed in one pass and applied in the next
    private Color[]   _colors;
    private Color[]   _pressureColors;
    private ComputeBuffer _positionBuffer;
    private ComputeBuffer _colorBuffer;
    private ComputeBuffer _pressureColorBuffer;
    private ComputeBuffer _dummyBuffer;
    private Stopwatch _stopwatch = new Stopwatch();
    private bool _paused = true; // start paused; Space to run
    private Vector2 _interactionPos;
    private float   _interactionSign; // +1 pull, -1 push, 0 none; read from the mouse once per frame
    private double _densityMs;
    private Mesh _quadMesh;
    private Bounds _renderBounds;
    private LineRenderer _boundsLine;
    private LineRenderer _interactionCircle;
    const int InteractionCircleSegments = 64;

    // Spatial grid: dense grid over bounds, cell size = smoothingRadius, counting-sorted each step.
    // Particles in cell c are _cellParticles[_cellStart[c] .. _cellStart[c+1]).
    private int   _gridW, _gridH;
    private int[] _cellStart;
    private int[] _cellParticles;
    private int[] _cellOf;

    void Awake()
    {
        if (Application.isPlaying) return;
        // Bind dummy buffers in edit mode to suppress "buffer not provided" shader warnings
        _dummyBuffer?.Release();
        _dummyBuffer = new ComputeBuffer(1, sizeof(float) * 4);
        _dummyBuffer.SetData(new Vector4[] { Vector4.zero });
        instancedMaterial?.SetBuffer("_Positions", _dummyBuffer);
        instancedMaterial?.SetBuffer("_Colors", _dummyBuffer);
        pressureFieldMaterial?.SetBuffer("_Positions", _dummyBuffer);
        pressureFieldMaterial?.SetBuffer("_PressureColors", _dummyBuffer);
    }

    void Start()
    {
        if (!Application.isPlaying) return;
        float height = Camera.main.orthographicSize * 2f;
        float width  = height * Camera.main.aspect;
        boundsSize   = new Vector2(width, height);

        _quadMesh = CreateQuadMesh();
        _renderBounds = new Bounds(Vector3.zero, new Vector3(boundsSize.x + smoothingRadius * 2f, boundsSize.y + smoothingRadius * 2f, 1f));
        SetupBoundsLine();
        _interactionCircle = CreateLine("InteractionCircle", Color.green, InteractionCircleSegments, loop: true);
        _interactionCircle.enabled = false;
        InitializeParticles();
    }

    void SetupBoundsLine()
    {
        _boundsLine = CreateLine("BoundsLine", Color.green, 5, loop: false);
        DrawBoundsLine();
    }

    LineRenderer CreateLine(string name, Color color, int positionCount, bool loop)
    {
        GameObject obj = GameObject.Find(name);
        if (obj == null) obj = new GameObject(name);
        obj.transform.position = Vector3.zero;
        LineRenderer line = obj.GetComponent<LineRenderer>();
        if (line == null) line = obj.AddComponent<LineRenderer>();
        line.positionCount    = positionCount;
        line.loop             = loop;
        line.widthMultiplier  = 0.05f;
        line.material         = new Material(Shader.Find("Sprites/Default"));
        line.startColor       = color;
        line.endColor         = color;
        line.useWorldSpace    = true;
        return line;
    }

    void DrawInteractionCircle()
    {
        if (_interactionCircle == null) return;
        _interactionCircle.enabled = _interactionSign != 0f;
        if (!_interactionCircle.enabled) return;

        Color color = _interactionSign > 0f ? Color.green : Color.red;
        _interactionCircle.startColor = color;
        _interactionCircle.endColor   = color;
        for (int k = 0; k < InteractionCircleSegments; k++)
        {
            float a = k * 2f * Mathf.PI / InteractionCircleSegments;
            _interactionCircle.SetPosition(k, new Vector3(_interactionPos.x + Mathf.Cos(a) * interactionRadius,
                                                          _interactionPos.y + Mathf.Sin(a) * interactionRadius, 0f));
        }
    }

    void DrawBoundsLine()
    {
        if (_boundsLine == null) return;
        float hx = boundsSize.x * 0.5f;
        float hy = boundsSize.y * 0.5f;
        _boundsLine.SetPositions(new Vector3[]
        {
            new Vector3(-hx,  hy, 0),
            new Vector3( hx,  hy, 0),
            new Vector3( hx, -hy, 0),
            new Vector3(-hx, -hy, 0),
            new Vector3(-hx,  hy, 0)
        });
    }

    void InitializeParticles()
    {
        _positions      = new Vector4[numParticles];
        _predicted      = new Vector4[numParticles];
        _velocities     = new Vector2[numParticles];
        _densities      = new float[numParticles];
        _nearDensities  = new float[numParticles];
        _accelerations  = new Vector2[numParticles];
        _colors         = new Color[numParticles];
        _pressureColors = new Color[numParticles];
        _cellParticles  = new int[numParticles];
        _cellOf         = new int[numParticles];

        // Square block of particles centred on screen, spaced one diameter plus spawnGap apart
        float spacing = particleRadius * 2f + spawnGap;
        int   cols    = Mathf.CeilToInt(Mathf.Sqrt(numParticles));
        int   rows    = Mathf.CeilToInt((float)numParticles / cols);
        if (cols * spacing > boundsSize.x || rows * spacing > boundsSize.y)
            Debug.LogWarning($"Spawn block {cols}x{rows} ({cols * spacing:F1}x{rows * spacing:F1}) exceeds bounds {boundsSize.x:F1}x{boundsSize.y:F1}; reduce numParticles or spawnGap");
        Vector2 origin = new Vector2(-(cols - 1) * spacing * 0.5f, -(rows - 1) * spacing * 0.5f);
        for (int i = 0; i < numParticles; i++)
        {
            Vector2 pos = origin + new Vector2(i % cols, i / cols) * spacing;
            _positions[i]  = new Vector4(pos.x, pos.y, 0f, 0f);
            _predicted[i]  = _positions[i];
            _velocities[i] = Vector2.zero;
        }

        _positionBuffer?.Release();
        _colorBuffer?.Release();
        _pressureColorBuffer?.Release();
        _positionBuffer      = new ComputeBuffer(numParticles, sizeof(float) * 4);
        _colorBuffer         = new ComputeBuffer(numParticles, sizeof(float) * 4);
        _pressureColorBuffer = new ComputeBuffer(numParticles, sizeof(float) * 4);
        _positionBuffer.SetData(_positions);
        BuildGrid();
    }

    void Update()
    {
        if (instancedMaterial == null || _positionBuffer == null) return;

        // Controls: R restart, Space pause/resume, Right arrow single step while paused
        Keyboard kb = Keyboard.current;
        bool stepOnce = false;
        if (kb != null)
        {
            if (kb.rKey.wasPressedThisFrame) { InitializeParticles(); _paused = true; }
            if (kb.spaceKey.wasPressedThisFrame) _paused = !_paused;
            stepOnce = _paused && kb.rightArrowKey.wasPressedThisFrame;
        }

        ReadMouse();
        DrawInteractionCircle();

        if (!_paused || stepOnce)
        {
            _stopwatch.Restart();
            float dt = (_paused ? 1f / 60f : Time.deltaTime) / subSteps; // fixed 60 Hz frame when stepping
            for (int step = 0; step < subSteps; step++)
                SimulationStep(dt);
            _stopwatch.Stop();
            _densityMs = _stopwatch.Elapsed.TotalMilliseconds;
        }

        UpdatePressureColors();
        UpdateSpeedColors();
        DrawVectorField();

        if (pressureFieldMaterial != null)
        {
            pressureFieldMaterial.SetBuffer("_Positions", _positionBuffer);
            pressureFieldMaterial.SetBuffer("_PressureColors", _pressureColorBuffer);
            pressureFieldMaterial.SetFloat("_SmoothingRadius", smoothingRadius);
            Graphics.DrawMeshInstancedProcedural(_quadMesh, 0, pressureFieldMaterial, _renderBounds, numParticles);
        }

        instancedMaterial.SetBuffer("_Positions", _positionBuffer);
        instancedMaterial.SetBuffer("_Colors", _colorBuffer);
        instancedMaterial.SetFloat("_ParticleRadius", particleRadius);
        Graphics.DrawMeshInstancedProcedural(_quadMesh, 0, instancedMaterial, _renderBounds, numParticles);
    }

    void ReadMouse()
    {
        Mouse mouse = Mouse.current;
        _interactionSign = 0f;
        if (mouse == null) return;
        if      (mouse.leftButton.isPressed)  _interactionSign =  1f;
        else if (mouse.rightButton.isPressed) _interactionSign = -1f;
        if (_interactionSign != 0f)
            _interactionPos = Camera.main.ScreenToWorldPoint(mouse.position.ReadValue());
    }

    // Lague's interaction force: pull toward (or push away from) the cursor, fading to zero at interactionRadius,
    // with a damping term so particles near the cursor settle instead of overshooting, and gravity cancelled inside
    // the ring so held water doesn't sag. To pick up a ball: click inside the pool, then drag out slowly.
    Vector2 InteractionForce(int i)
    {
        Vector2 offset = _interactionPos - new Vector2(_positions[i].x, _positions[i].y);
        float sqrDst = offset.sqrMagnitude;
        if (sqrDst >= interactionRadius * interactionRadius) return Vector2.zero;
        float dst = Mathf.Sqrt(sqrDst);
        Vector2 dir = dst <= 0.0001f ? Vector2.zero : offset / dst;
        float centreT = 1f - dst / interactionRadius;
        Vector2 force = (dir * interactionStrength * _interactionSign - _velocities[i]) * centreT;
        if (_interactionSign > 0f) force += Vector2.up * gravity * centreT * Mathf.Clamp01(interactionStrength / 10f);
        return force;
    }

    void SimulationStep(float dt)
    {
        // Pass 1: apply gravity + predict where each particle will be
        Parallel.For(0, numParticles, i =>
        {
            _velocities[i] += Vector2.down * gravity * dt;
            _predicted[i].x = _positions[i].x + _velocities[i].x * PredictionTime;
            _predicted[i].y = _positions[i].y + _velocities[i].y * PredictionTime;
        });
        BuildGrid();

        // Pass 2: densities from predicted positions
        Parallel.For(0, numParticles, i =>
        {
            _densities[i] = CalculateDensity(_predicted[i]);
            if (nearPressureMultiplier != 0f) _nearDensities[i] = CalculateNearDensity(_predicted[i]);
        });

        // Pass 3: accelerations from predicted positions. Reads velocities (viscosity) so nothing may write them here
        Parallel.For(0, numParticles, i =>
        {
            Vector2 accel = CalculatePressureForce(i) / _densities[i];
            if (viscosityStrength != 0f) accel += CalculateViscosityForce(i);
            if (_interactionSign != 0f)  accel += InteractionForce(i);
            _accelerations[i] = accel;
        });

        // Pass 4: apply accelerations + damping, integrate positions, resolve bounds collisions
        Parallel.For(0, numParticles, i =>
        {
            _velocities[i] += _accelerations[i] * dt;
            _velocities[i] *= Mathf.Pow(1f - damping, dt);
            _positions[i].x += _velocities[i].x * dt;
            _positions[i].y += _velocities[i].y * dt;
            ResolveCollisions(ref _positions[i], ref _velocities[i]);
        });

        _positionBuffer.SetData(_positions);
    }

    void BuildGrid()
    {
        _gridW = Mathf.Max(1, Mathf.CeilToInt(boundsSize.x / smoothingRadius));
        _gridH = Mathf.Max(1, Mathf.CeilToInt(boundsSize.y / smoothingRadius));
        int cells = _gridW * _gridH;
        if (_cellStart == null || _cellStart.Length != cells + 1) _cellStart = new int[cells + 1];
        else System.Array.Clear(_cellStart, 0, _cellStart.Length);

        // Counting sort: count -> inclusive prefix sum (cell end) -> scatter backwards (leaves cell start)
        for (int i = 0; i < numParticles; i++)
        {
            _cellOf[i] = CellIndex(_predicted[i].x, _predicted[i].y);
            _cellStart[_cellOf[i]]++;
        }
        for (int c = 1; c < cells; c++) _cellStart[c] += _cellStart[c - 1];
        for (int i = numParticles - 1; i >= 0; i--) _cellParticles[--_cellStart[_cellOf[i]]] = i;
        _cellStart[cells] = numParticles;
    }

    int CellIndex(float x, float y)
    {
        CellCoords(x, y, out int cx, out int cy);
        return cx + cy * _gridW;
    }

    void CellCoords(float x, float y, out int cx, out int cy)
    {
        cx = Mathf.Clamp((int)((x + boundsSize.x * 0.5f) / smoothingRadius), 0, _gridW - 1);
        cy = Mathf.Clamp((int)((y + boundsSize.y * 0.5f) / smoothingRadius), 0, _gridH - 1);
    }

    float CalculateDensity(Vector4 position)
    {
        float density = 0f;
        CellCoords(position.x, position.y, out int cx, out int cy);
        for (int y = Mathf.Max(cy - 1, 0); y <= Mathf.Min(cy + 1, _gridH - 1); y++)
        for (int x = Mathf.Max(cx - 1, 0); x <= Mathf.Min(cx + 1, _gridW - 1); x++)
        {
            int c = x + y * _gridW;
            for (int k = _cellStart[c]; k < _cellStart[c + 1]; k++)
            {
                int j = _cellParticles[k];
                float dst = Vector2.Distance(new Vector2(position.x, position.y),
                                             new Vector2(_predicted[j].x, _predicted[j].y));
                density += particleMass * SmoothingKernel(dst, smoothingRadius);
            }
        }
        return density;
    }

    float CalculateNearDensity(Vector4 position)
    {
        float density = 0f;
        CellCoords(position.x, position.y, out int cx, out int cy);
        for (int y = Mathf.Max(cy - 1, 0); y <= Mathf.Min(cy + 1, _gridH - 1); y++)
        for (int x = Mathf.Max(cx - 1, 0); x <= Mathf.Min(cx + 1, _gridW - 1); x++)
        {
            int c = x + y * _gridW;
            for (int k = _cellStart[c]; k < _cellStart[c + 1]; k++)
            {
                int j = _cellParticles[k];
                float dst = Vector2.Distance(new Vector2(position.x, position.y),
                                             new Vector2(_predicted[j].x, _predicted[j].y));
                density += particleMass * NearDensityKernel(dst, smoothingRadius);
            }
        }
        return density;
    }

    float PressureFromDensity(float density)
    {
        return pressureMultiplier * (density - targetDensity);
    }

    Vector2 CalculatePressureForce(int i)
    {
        Vector2 force   = Vector2.zero;
        float pressureI = PressureFromDensity(_densities[i]);
        CellCoords(_predicted[i].x, _predicted[i].y, out int cx, out int cy);
        for (int y = Mathf.Max(cy - 1, 0); y <= Mathf.Min(cy + 1, _gridH - 1); y++)
        for (int x = Mathf.Max(cx - 1, 0); x <= Mathf.Min(cx + 1, _gridW - 1); x++)
        {
            int c = x + y * _gridW;
            for (int k = _cellStart[c]; k < _cellStart[c + 1]; k++)
            {
                int j = _cellParticles[k];
                if (i == j) continue;
                Vector2 rVec = new Vector2(_predicted[i].x - _predicted[j].x,
                                           _predicted[i].y - _predicted[j].y);
                float dst = rVec.magnitude;
                if (dst >= smoothingRadius) continue;
                // Coincident particles must still repel (Lague uses a random direction); golden-angle spread by index is thread-safe
                Vector2 dir   = dst > 0.0001f ? rVec / dst : new Vector2(Mathf.Cos(i * 2.39996f), Mathf.Sin(i * 2.39996f));
                float grad    = SmoothingKernelDerivative(dst, smoothingRadius);
                float sharedP = (pressureI + PressureFromDensity(_densities[j])) / (_densities[i] + _densities[j]);
                force        += particleMass * sharedP * grad * dir;
                if (nearPressureMultiplier != 0f)
                {
                    // Near pressure (Clavet et al. double density relaxation): always repulsive, strongest for close pairs
                    float sharedNearP = nearPressureMultiplier * (_nearDensities[i] + _nearDensities[j]) / (2f * _nearDensities[j]);
                    force += particleMass * sharedNearP * NearDensityKernelDerivative(dst, smoothingRadius) * dir;
                }
            }
        }
        return force;
    }

    Vector2 CalculateViscosityForce(int i)
    {
        Vector2 force = Vector2.zero;
        CellCoords(_predicted[i].x, _predicted[i].y, out int cx, out int cy);
        for (int y = Mathf.Max(cy - 1, 0); y <= Mathf.Min(cy + 1, _gridH - 1); y++)
        for (int x = Mathf.Max(cx - 1, 0); x <= Mathf.Min(cx + 1, _gridW - 1); x++)
        {
            int c = x + y * _gridW;
            for (int k = _cellStart[c]; k < _cellStart[c + 1]; k++)
            {
                int j = _cellParticles[k];
                if (i == j) continue;
                float dst = Vector2.Distance(new Vector2(_predicted[i].x, _predicted[i].y),
                                             new Vector2(_predicted[j].x, _predicted[j].y));
                force += (_velocities[j] - _velocities[i]) * ViscosityKernel(dst, smoothingRadius);
            }
        }
        return force * viscosityStrength;
    }

    // Cubic spike: sharper than the density kernel, so near density mostly counts very close neighbours
    float NearDensityKernel(float dst, float radius)
    {
        if (dst >= radius) return 0f;
        float v = radius - dst;
        return v * v * v * 10f / (Mathf.PI * Mathf.Pow(radius, 5));
    }

    float NearDensityKernelDerivative(float dst, float radius)
    {
        if (dst >= radius) return 0f;
        float v = radius - dst;
        return v * v * 30f / (Mathf.PI * Mathf.Pow(radius, 5));
    }

    // Poly6: smooth at dst = 0, so viscosity has no kick between close neighbours
    float ViscosityKernel(float dst, float radius)
    {
        if (dst >= radius) return 0f;
        float v = radius * radius - dst * dst;
        return v * v * v / (Mathf.PI * Mathf.Pow(radius, 8) / 4f);
    }

    void ResolveCollisions(ref Vector4 position, ref Vector2 velocity)
    {
        Vector2 halfBounds = boundsSize * 0.5f - Vector2.one * particleRadius;

        if (Mathf.Abs(position.x) > halfBounds.x)
        {
            position.x  = halfBounds.x * Mathf.Sign(position.x);
            velocity.x *= -collisionDamping;
        }
        if (Mathf.Abs(position.y) > halfBounds.y)
        {
            position.y  = halfBounds.y * Mathf.Sign(position.y);
            velocity.y *= -collisionDamping;
        }
    }

    float SmoothingKernel(float dst, float radius)
    {
        if (dst >= radius) return 0f;
        float volume = Mathf.PI * Mathf.Pow(radius, 4) / 6f;
        return (radius - dst) * (radius - dst) / volume;
    }

    float SmoothingKernelDerivative(float dst, float radius)
    {
        if (dst >= radius) return 0f;
        float scale = 12f / (Mathf.PI * Mathf.Pow(radius, 4));
        return (radius - dst) * scale;
    }

    void UpdatePressureColors()
    {
        for (int i = 0; i < numParticles; i++)
        {
            float pressure = PressureFromDensity(_densities[i]);
            float t = Mathf.Clamp(pressure / (pressureMultiplier * targetDensity), -1f, 1f);
            _pressureColors[i] = t > 0
                ? Color.Lerp(zeroPressureColor, positivePressureColor,  t)
                : Color.Lerp(zeroPressureColor, negativePressureColor, -t);
        }
        _pressureColorBuffer.SetData(_pressureColors);
    }

    void UpdateSpeedColors()
    {
        for (int i = 0; i < numParticles; i++)
            _colors[i] = speedGradient.Evaluate(Mathf.Clamp01(_velocities[i].magnitude / maxSpeed));
        _colorBuffer.SetData(_colors);
    }

    Vector2 DensityGradient(Vector2 samplePos)
    {
        Vector2 gradient = Vector2.zero;
        CellCoords(samplePos.x, samplePos.y, out int cx, out int cy);
        for (int y = Mathf.Max(cy - 1, 0); y <= Mathf.Min(cy + 1, _gridH - 1); y++)
        for (int x = Mathf.Max(cx - 1, 0); x <= Mathf.Min(cx + 1, _gridW - 1); x++)
        {
            int c = x + y * _gridW;
            for (int k = _cellStart[c]; k < _cellStart[c + 1]; k++)
            {
                int j = _cellParticles[k];
                Vector2 rVec = samplePos - new Vector2(_predicted[j].x, _predicted[j].y);
                float dst = rVec.magnitude;
                if (dst >= smoothingRadius || dst < 0.0001f) continue;
                float grad = SmoothingKernelDerivative(dst, smoothingRadius);
                gradient -= particleMass * grad * rVec / dst;
            }
        }
        return gradient;
    }

    void DrawVectorField()
    {
        float stepX = boundsSize.x / vectorFieldResolution;
        float stepY = boundsSize.y / vectorFieldResolution;

        for (int ix = 0; ix <= vectorFieldResolution; ix++)
        {
            for (int iy = 0; iy <= vectorFieldResolution; iy++)
            {
                Vector2 samplePos = new Vector2(
                    -boundsSize.x * 0.5f + ix * stepX,
                    -boundsSize.y * 0.5f + iy * stepY);

                Vector2 grad = DensityGradient(samplePos);
                if (grad.magnitude < 0.0001f) continue;

                float mag = Mathf.Min(grad.magnitude * vectorFieldScale, stepX * 0.45f);
                Vector2 dir = grad.normalized;
                Vector2 tip = samplePos + dir * mag;

                Debug.DrawLine(new Vector3(samplePos.x, samplePos.y, 0),
                               new Vector3(tip.x, tip.y, 0), Color.yellow);

                Vector2 perp = new Vector2(-dir.y, dir.x) * mag * 0.3f;
                Vector2 back = samplePos + dir * mag * 0.7f;
                Debug.DrawLine(new Vector3(tip.x, tip.y, 0),
                               new Vector3(back.x + perp.x, back.y + perp.y, 0), Color.yellow);
                Debug.DrawLine(new Vector3(tip.x, tip.y, 0),
                               new Vector3(back.x - perp.x, back.y - perp.y, 0), Color.yellow);
            }
        }
    }

    [ContextMenu("Validate spatial grid")]
    void ValidateGrid()
    {
        // Brute-force density vs grid density; any difference means the grid misses or duplicates neighbors
        float maxErr = 0f;
        for (int i = 0; i < numParticles; i++)
        {
            float brute = 0f;
            for (int j = 0; j < numParticles; j++)
                brute += particleMass * SmoothingKernel(Vector2.Distance(_predicted[i], _predicted[j]), smoothingRadius);
            maxErr = Mathf.Max(maxErr, Mathf.Abs(brute - CalculateDensity(_predicted[i])));
        }
        Debug.Log($"Spatial grid {_gridW}x{_gridH}, {numParticles} particles, max density error vs brute force: {maxErr}");
    }

    void OnGUI()
    {
        if (_paused) GUI.Label(new Rect(10, 30, 400, 20), "PAUSED  (Space: run, Right arrow: step, R: restart)");
        if (!showDensityMs) return;
        GUI.Label(new Rect(10, 10, 400, 20), $"Sim: {_densityMs:F3} ms   Bounds: {boundsSize.x:F1} x {boundsSize.y:F1}   Particles: {numParticles}");
        if (Mouse.current != null && _predicted != null)
        {
            Vector2 screen = Mouse.current.position.ReadValue();
            Vector2 world  = Camera.main.ScreenToWorldPoint(screen);
            // GUI y runs top-down, mouse y bottom-up
            GUI.Label(new Rect(screen.x + 12, Screen.height - screen.y - 10, 300, 20), $"density {CalculateDensity(world):F2}");
        }
    }

    void OnDestroy()
    {
        _positionBuffer?.Release();
        _colorBuffer?.Release();
        _pressureColorBuffer?.Release();
        _dummyBuffer?.Release();
    }

    private Mesh CreateQuadMesh()
    {
        Mesh mesh = new Mesh();
        mesh.vertices = new Vector3[]
        {
            new Vector3(-0.5f, -0.5f, 0),
            new Vector3( 0.5f, -0.5f, 0),
            new Vector3( 0.5f,  0.5f, 0),
            new Vector3(-0.5f,  0.5f, 0)
        };
        mesh.uv = new Vector2[]
        {
            new Vector2(0, 0),
            new Vector2(1, 0),
            new Vector2(1, 1),
            new Vector2(0, 1)
        };
        mesh.triangles = new int[] { 0, 1, 2, 2, 3, 0 };
        mesh.RecalculateNormals();
        return mesh;
    }
}
