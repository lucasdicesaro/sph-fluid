using UnityEngine;

public class Circle : MonoBehaviour
{
    public Material instancedMaterial;
    public int numParticles = 100;
    public float particleRadius = 0.2f;
    public float smoothingRadius = 1f;
    public float particleMass = 1f;
    public float targetDensity = 2f;
    public Vector2 boundsSize = new Vector2(10f, 10f);
    public Color particleColor = Color.white;
    public float particleSpacing = 0f;
    public float gravity = 0f;
    public float collisionDamping = 0.8f;
    public int vectorFieldResolution = 15;
    public float vectorFieldScale = 0.3f;

    private Vector4[] _positions;
    private Vector2[] _velocities;
    private float[]   _densities;
    private Color[]   _colors;
    private ComputeBuffer _positionBuffer;
    private ComputeBuffer _colorBuffer;
    private Mesh _quadMesh;
    private Bounds _renderBounds;
    private LineRenderer _boundsLine;

    void Start()
    {
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
        _positions  = new Vector4[numParticles];
        _velocities = new Vector2[numParticles];
        _densities  = new float[numParticles];
        _colors     = new Color[numParticles];

        Vector2 spawnHalf = boundsSize * 0.5f - Vector2.one * particleRadius;
        for (int i = 0; i < numParticles; i++)
        {
            float x = Random.Range(-spawnHalf.x, spawnHalf.x);
            float y = Random.Range(-spawnHalf.y, spawnHalf.y);
            _positions[i]  = new Vector4(x, y, 0f, 0f);
            _velocities[i] = Vector2.zero;
            _colors[i]     = particleColor;
        }

        _positionBuffer?.Release();
        _colorBuffer?.Release();
        _positionBuffer = new ComputeBuffer(numParticles, sizeof(float) * 4);
        _colorBuffer    = new ComputeBuffer(numParticles, sizeof(float) * 4);
        _positionBuffer.SetData(_positions);
        _colorBuffer.SetData(_colors);
    }

    void Update()
    {
        if (instancedMaterial == null || _positionBuffer == null) return;

        SimulateParticles();
        CalculateDensities();
        UpdateDensityColors();
        DrawVectorField();

        instancedMaterial.SetBuffer("_Positions", _positionBuffer);
        instancedMaterial.SetBuffer("_Colors", _colorBuffer);
        instancedMaterial.SetFloat("_ParticleRadius", particleRadius);
        instancedMaterial.SetFloat("_SmoothingRadius", smoothingRadius);

        Graphics.DrawMeshInstancedProcedural(_quadMesh, 0, instancedMaterial, _renderBounds, numParticles);
    }

    void SimulateParticles()
    {
        Vector2 halfBounds = boundsSize * 0.5f - Vector2.one * particleRadius;
        float dt = Time.deltaTime;

        for (int i = 0; i < numParticles; i++)
        {
            _velocities[i] += Vector2.down * gravity * dt;
            _positions[i].x += _velocities[i].x * dt;
            _positions[i].y += _velocities[i].y * dt;

            if (Mathf.Abs(_positions[i].x) > halfBounds.x)
            {
                _positions[i].x = halfBounds.x * Mathf.Sign(_positions[i].x);
                _velocities[i].x *= -collisionDamping;
            }
            if (Mathf.Abs(_positions[i].y) > halfBounds.y)
            {
                _positions[i].y = halfBounds.y * Mathf.Sign(_positions[i].y);
                _velocities[i].y *= -collisionDamping;
            }
        }

        _positionBuffer.SetData(_positions);
    }

    float Poly6Kernel(float dst, float radius)
    {
        if (dst >= radius) return 0f;
        float scale = 4f / (Mathf.PI * Mathf.Pow(radius, 8));
        float v = radius * radius - dst * dst;
        return scale * v * v * v;
    }

    void CalculateDensities()
    {
        for (int i = 0; i < numParticles; i++)
        {
            _densities[i] = 0f;
            for (int j = 0; j < numParticles; j++)
            {
                float dst = Vector2.Distance(
                    new Vector2(_positions[i].x, _positions[i].y),
                    new Vector2(_positions[j].x, _positions[j].y));
                _densities[i] += particleMass * Poly6Kernel(dst, smoothingRadius);
            }
        }
    }

    void UpdateDensityColors()
    {
        for (int i = 0; i < numParticles; i++)
        {
            float t = Mathf.Clamp01(_densities[i] / (targetDensity * 2f));
            _colors[i] = Color.Lerp(Color.blue, Color.red, t);
        }
        _colorBuffer.SetData(_colors);
    }

    Vector2 DensityGradient(Vector2 samplePos)
    {
        Vector2 gradient = Vector2.zero;
        float scale = -24f / (Mathf.PI * Mathf.Pow(smoothingRadius, 8));
        for (int j = 0; j < numParticles; j++)
        {
            Vector2 rVec = samplePos - new Vector2(_positions[j].x, _positions[j].y);
            float dst = rVec.magnitude;
            if (dst >= smoothingRadius) continue;
            float v = smoothingRadius * smoothingRadius - dst * dst;
            gradient += particleMass * scale * v * v * rVec;
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

    public void UpdatePositions(Vector4[] newPositions)
    {
        _positionBuffer.SetData(newPositions);
    }

    void OnDestroy()
    {
        _positionBuffer?.Release();
        _colorBuffer?.Release();
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
