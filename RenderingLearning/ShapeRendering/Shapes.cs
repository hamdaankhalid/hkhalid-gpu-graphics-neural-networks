using System.Drawing;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace RenderingLearning.ShapeRendering;

// Flyweight split:
//   Mesh        = intrinsic state — local-space geometry, immutable, shared by
//                 reference between every object that renders it.
//   Meshes      = the flyweight factory — owns the canonical instance of each
//                 shape, so the same geometry can never exist twice.
//   SceneObject = extrinsic state — which mesh + where it is. Position/rotation/
//                 scale live ONLY here (applied via the model matrix), never in
//                 the vertices. That keeps meshes shareable on the CPU renderer
//                 and upload-once on the GPU renderer.

public sealed class Mesh
{
  // Local space, centered on the mesh's own origin. Triangle list: every 3
  // consecutive entries form one triangle.
  public readonly Vector3[] Vertices;

  public Mesh(Vector3[] vertices)
  {
    if (vertices.Length % 3 != 0)
      throw new ArgumentException("Vertex count must be a multiple of 3 (triangle list).", nameof(vertices));
    Vertices = vertices;
  }

  public int TriangleCount => Vertices.Length / 3;
}

public static class Meshes
{
  // Unit-sized (extent 1, i.e. corners at +-0.5), centered on the local
  // origin. Instance size comes from SceneObject.Scale, instance placement
  // from SceneObject.Position — a scale-2 object spans -1..1, matching the
  // old scale: 2 shapes.
  public static readonly Mesh Triangle = BuildTriangle();
  public static readonly Mesh Quad = BuildQuad();
  public static readonly Mesh Cube = BuildCube();

  // The canonical catalog, in upload order. Register new meshes HERE and
  // MeshBufferSizeBytes (and any loop uploading the catalog) picks them up —
  // one point of truth instead of hand-summed mesh lists.
  public static readonly Mesh[] All = { Triangle, Quad, Cube };

  // Vertex index where each mesh's slice begins inside the packed catalog
  // buffer — the same running sum CreateBuffers() walks in bytes, kept here
  // in vertices. This is the CPU-side wiring that turns SceneObject.Mesh
  // into Draw()'s firstVertex; the shader never learns mesh identity.
  // Declared after All: C# runs static initializers in declaration order.
  private static readonly Dictionary<Mesh, int> _firstVertex = BuildFirstVertexTable();

  public static int FirstVertexOf(Mesh mesh) => _firstVertex[mesh];

  private static Dictionary<Mesh, int> BuildFirstVertexTable()
  {
    Dictionary<Mesh, int> table = new();
    int running = 0;
    foreach (Mesh mesh in All)
    {
      table[mesh] = running;
      running += mesh.Vertices.Length;
    }
    return table;
  }

  // Bytes needed to store every mesh in All, rounded up to the 4-byte
  // multiple WebGPU requires (MappedAtCreation size, QueueWriteBuffer size).
  // 12-byte Vector3 vertices always land 4-aligned already; the AlignUp is
  // armor for a future vertex layout whose stride isn't a multiple of 4.
  public static int MeshBufferSizeBytes
  {
    get
    {
      int bytes = 0;
      foreach (Mesh mesh in All)
        bytes += mesh.Vertices.Length * Unsafe.SizeOf<Vector3>();
      return AlignUp(bytes, 4);
    }
  }

  // Round value up to the next multiple of alignment (power of two only).
  private static int AlignUp(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);

  private static Mesh BuildTriangle()
  {
    const float half = 0.5f;
    return new Mesh(new Vector3[]
    {
      new(-half, -half, 0f),
      new( half, -half, 0f),
      new( half,  half, 0f),
    });
  }

  private static Mesh BuildQuad()
  {
    const float half = 0.5f;
    // Split along the bottom-left to top-right diagonal
    return new Mesh(new Vector3[]
    {
      new(-half, -half, 0f), new( half, -half, 0f), new( half,  half, 0f),
      new(-half, -half, 0f), new( half,  half, 0f), new(-half,  half, 0f),
    });
  }

  private static Mesh BuildCube()
  {
    const float half = 0.5f;

    Vector3 c0 = new(-half, -half, -half);
    Vector3 c1 = new( half, -half, -half);
    Vector3 c2 = new( half,  half, -half);
    Vector3 c3 = new(-half,  half, -half);
    Vector3 c4 = new(-half, -half,  half);
    Vector3 c5 = new( half, -half,  half);
    Vector3 c6 = new( half,  half,  half);
    Vector3 c7 = new(-half,  half,  half);

    // 6 faces x 2 triangles = 12 triangles, 36 vertices
    return new Mesh(new Vector3[]
    {
      // Front (Z+)
      c4, c5, c6,  c4, c6, c7,
      // Back (Z-)
      c1, c0, c3,  c1, c3, c2,
      // Right (X+)
      c5, c1, c2,  c5, c2, c6,
      // Left (X-)
      c4, c0, c1,  c4, c1, c5,
      // Top (Y+)
      c3, c2, c6,  c3, c6, c7,
      // Bottom (Y-)
      c0, c4, c5,  c0, c5, c1,
    });
  }
}