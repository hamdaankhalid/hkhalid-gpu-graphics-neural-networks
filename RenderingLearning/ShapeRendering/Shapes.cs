
using System.Numerics;

namespace RenderingLearning.ShapeRendering;

public interface IShape
{
  Triangle[] GetTriangles();
  Vector3[] GetVertices();
}

public struct Triangle : IShape
{
  public Vector3 V1, V2, V3;
  public readonly int Scale;
  public readonly Vector3 Origin;

  // Unit triangle centered at origin (XY plane)
  public Triangle(int scale = 1, Vector3? origin = null)
  {
    Scale = scale;
    Origin = origin ?? Vector3.Zero;
    float half = scale / 2f;

    // Local space: X right, Y up, Z forward (left-handed)
    V1 = new Vector3(-half, -half, 0.0f) + Origin;
    V2 = new Vector3(half, -half, 0.0f) + Origin;
    V3 = new Vector3(half, half, 0.0f) + Origin;
  }

  // Direct vertex constructor for shapes like Cube that need custom geometry
  public Triangle(Vector3 v1, Vector3 v2, Vector3 v3)
  {
    V1 = v1; V2 = v2; V3 = v3;
    Scale = 0; Origin = Vector3.Zero; // Metadata placeholder
  }

  public Triangle[] GetTriangles() => new[] { this };
  public Vector3[] GetVertices() => new[] { V1, V2, V3 };
}

public struct Quad : IShape
{
  public readonly Triangle T1, T2;
  public readonly int Scale;
  public readonly Vector3 Origin;

  public Quad(int scale = 1, Vector3? origin = null)
  {
    Scale = scale;
    Origin = origin ?? Vector3.Zero;
    float half = scale / 2f;

    // Split quad along diagonal (bottom-left to top-right)
    T1 = new Triangle(new Vector3(-half, -half, 0) + Origin, new Vector3(half, -half, 0) + Origin, new Vector3(half, half, 0) + Origin);
    T2 = new Triangle(new Vector3(-half, -half, 0) + Origin, new Vector3(half, half, 0) + Origin, new Vector3(-half, half, 0) + Origin);
  }

  public Triangle[] GetTriangles() => new[] { T1, T2 };

  // Flattens to a single array for easier vertex buffer binding
  public Vector3[] GetVertices() => new Vector3[6]
  {
        T1.V1, T1.V2, T1.V3,
        T2.V1, T2.V2, T2.V3
  };
}

public struct Cube : IShape
{
  private readonly Triangle[] _triangles;
  public readonly int Scale;
  public readonly Vector3 Origin;

  // 6 faces × 2 triangles = 12 triangles total
  public Cube(int scale = 1, Vector3? origin = null)
  {
    Scale = scale;
    Origin = origin ?? Vector3.Zero;
    float half = scale / 2f;

    // Corner vertices relative to local origin
    Vector3 c0 = new(-half, -half, -half);
    Vector3 c1 = new(half, -half, -half);
    Vector3 c2 = new(half, half, -half);
    Vector3 c3 = new(-half, half, -half);
    Vector3 c4 = new(-half, -half, half);
    Vector3 c5 = new(half, -half, half);
    Vector3 c6 = new(half, half, half);
    Vector3 c7 = new(-half, half, half);

    _triangles = new Triangle[12];
    int idx = 0;

    // Front (Z+)
    _triangles[idx++] = new(c4, c5, c6); _triangles[idx++] = new(c4, c6, c7);
    // Back (Z-)
    _triangles[idx++] = new(c1, c0, c3); _triangles[idx++] = new(c1, c3, c2);
    // Right (X+)
    _triangles[idx++] = new(c5, c1, c2); _triangles[idx++] = new(c5, c2, c6);
    // Left (X-)
    _triangles[idx++] = new(c4, c0, c1); _triangles[idx++] = new(c4, c1, c5);
    // Top (Y+)
    _triangles[idx++] = new(c3, c2, c6); _triangles[idx++] = new(c3, c6, c7);
    // Bottom (Y-)
    _triangles[idx++] = new(c0, c4, c5); _triangles[idx++] = new(c0, c5, c1);
  }

  public Triangle[] GetTriangles() => _triangles;

  public Vector3[] GetVertices()
  {
    var verts = new Vector3[36]; // 12 triangles × 3 vertices
    int idx = 0;
    foreach (var t in _triangles)
    {
      verts[idx++] = t.V1;
      verts[idx++] = t.V2;
      verts[idx++] = t.V3;
    }
    return verts;
  }
}
