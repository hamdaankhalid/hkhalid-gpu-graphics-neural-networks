using System.Numerics;

namespace RenderingLearning.ShapeRendering;

public sealed class SceneObject
{
  public Mesh Mesh;
  public Vector3 Position;
  public float Yaw, Pitch, Roll; // Radians
  public float Scale = 1f;

  public SceneObject(Mesh mesh, Vector3 position = default, float scale = 1f)
  {
    Mesh = mesh;
    Position = position;
    Scale = scale;
  }

  // Row-major .NET matrices: scale, then rotate, then translate.
  public Matrix4x4 ModelMatrix =>
      Matrix4x4.CreateScale(Scale)
    * Matrix4x4.CreateFromYawPitchRoll(Yaw, Pitch, Roll)
    * Matrix4x4.CreateTranslation(Position);
}
