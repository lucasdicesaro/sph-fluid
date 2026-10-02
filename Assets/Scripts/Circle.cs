using UnityEngine;
using System.Threading.Tasks;
using Stopwatch = System.Diagnostics.Stopwatch;

[ExecuteAlways]
public class Circle : MonoBehaviour
{
    public Material instancedMaterial;
    public Material pressureFieldMaterial;
    public int numParticles = 3000;
    public float particleRadius = 0.05f;
    [Tooltip("Gap between neighbouring particles in the spawn block")]
    public float spawnGap = 0.02f;
    public float smoothingRadius = 0.3f;
    public float particleMass = 1f;
    public float targetDensity = 1.5f;
    private Vector2 boundsSize;

    public float gravity = 0f;
    [Range(0f, 1f)] public float collisionDamping = 0.95f;
    public float pressureMultiplier = 4f;
    [Range(0f, 1f)] public float damping = 0.1f;
    public int subSteps = 3;

    [Header("Debug")]
    public int vectorFieldResolution = 20;
    public float vectorFieldScale = 0.3f;
    public bool showDensityMs = false;

    [Header("Visualization")]
    public Color particleColor = Color.black;
    public Color negativePressureColor = new Color(0f, 0.429f, 1f);
    public Color positivePressureColor = new Color(0.774f, 0.179f, 0.179f);
    public Color zeroPressureColor = new Color(0.594f, 0.586f, 0.586f);

    private Vector4[] _positions;
    private Vector4[] _predicted;   // positions one lookahead step ahead; densities, forces and the grid use these
    const float PredictionTime = 1f / 120f; // Lague's fixed lookahead, independent of dt
    private Vector2[] _velocities;
    private float[]   _densities;
    private Color[]   _colors;
    private Color[]   _pressureColors;
    private ComputeBuffer _positionBuffer;
    private ComputeBuffer _colorBuffer;
    private ComputeBuffer _pressureColorBuffer;
    private ComputeBuffer _dummyBuffer;
    private Stopwatch _stopwatch = new Stopwatch();
    private double _densityMs;
    private Mesh _quadMesh;
    private Bounds _renderBounds;
    private LineRenderer _boundsLine;

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
        InitializeParticles();
    }

    void SetupBoundsLine()
    {
        GameObject boundsObj = GameObject.Find("BoundsLine");
        if (boundsObj == null)
            boundsObj = new GameObject("BoundsLine");

        boundsObj.transform.position = Vector3.zero;
        _boundsLine = boundsObj.GetComponent<LineRenderer>();
        if (_boundsLine == null)
            _boundsLine = boundsObj.AddComponent<LineRenderer>();

        _boundsLine.positionCount = 5;
        _boundsLine.loop = false;
        _boundsLine.widthMultiplier = 0.05f;
        _boundsLine.material = new Material(Shader.Find("Sprites/Default"));
        _boundsLine.startColor = Color.green;
        _boundsLine.endColor = Color.green;
        _boundsLine.useWorldSpace = true;

        DrawBoundsLine();
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
        _colors         = new Color[numParticles];
        _pressureColors = new Color[numParticles];
        _cellParticles  = new int[numParticles];
        _cellOf         = new int[numParticles];

        // Block of particles centred on screen, spaced one diameter plus spawnGap apart, same aspect as the bounds
        float spacing = particleRadius * 2f + spawnGap;
        int   cols    = Mathf.CeilToInt(Mathf.Sqrt(numParticles * boundsSize.x / boundsSize.y));
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
            _colors[i]     = particleColor;
        }

        _positionBuffer?.Release();
        _colorBuffer?.Release();
        _pressureColorBuffer?.Release();
        _positionBuffer      = new ComputeBuffer(numParticles, sizeof(float) * 4);
        _colorBuffer         = new ComputeBuffer(numParticles, sizeof(float) * 4);
        _pressureColorBuffer = new ComputeBuffer(numParticles, sizeof(float) * 4);
        _positionBuffer.SetData(_positions);
        _colorBuffer.SetData(_colors);
        BuildGrid();
    }

    void Update()
    {
        if (instancedMaterial == null || _positionBuffer == null) return;

        _stopwatch.Restart();
        float dt = Time.deltaTime / subSteps;
        for (int step = 0; step < subSteps; step++)
            SimulationStep(dt);
        _stopwatch.Stop();
        _densityMs = _stopwatch.Elapsed.TotalMilliseconds;

        UpdatePressureColors();
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
        Parallel.For(0, numParticles, i => _densities[i] = CalculateDensity(_predicted[i]));

        // Pass 3: pressure force (from predicted positions) + apply acceleration + damping
        Parallel.For(0, numParticles, i =>
        {
            Vector2 pressureForce        = CalculatePressureForce(i);
            _velocities[i]              += pressureForce / _densities[i] * dt;
            _velocities[i]              *= Mathf.Pow(1f - damping, dt);
        });

        // Pass 4: integrate positions + resolve bounds collisions
        Parallel.For(0, numParticles, i =>
        {
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
                if (dst >= smoothingRadius || dst < 0.0001f) continue;
                Vector2 dir   = rVec / dst;
                float grad    = SmoothingKernelDerivative(dst, smoothingRadius);
                float sharedP = (pressureI + PressureFromDensity(_densities[j])) / (_densities[i] + _densities[j]);
                force        += particleMass * sharedP * grad * dir;
            }
        }
        return force;
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
        if (dst >= radius || dst < 0.0001f) return 0f;
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
        if (!showDensityMs) return;
        GUI.Label(new Rect(10, 10, 300, 20), $"Density: {_densityMs:F3} ms");
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
