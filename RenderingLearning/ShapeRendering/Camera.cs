using System.Numerics;


public class Camera
{
  public enum Style
  {
    Orbit = 0,
    FreeFly = 1
  }

  private readonly Style _style;

  private readonly float _aspect;
  private readonly Matrix4x4 _proj;

  // Orbit camera state
  private Vector3 _cameraPosition;
  private Vector3 _cameraTarget = Vector3.Zero;
  private float _radius = 5f;
  private float _yaw = 0f;    // Radians, rotates around Y axis
  private float _pitch = 0f;  // Radians, rotates around local X axis

  // Free-fly camera state: position is stored, target is derived each frame
  // as _position + Forward. Reuses _yaw/_pitch, but here they aim the view
  // (yaw 0 looks down -Z) instead of placing the camera on a sphere.
  private Vector3 _position = new(0f, 0f, 5f);
  private const float MoveSpeed = 5f; // world units per second

  // View matrix caching
  private bool _viewDirty = true;
  private Matrix4x4? _cachedView;

  public Camera(float windowWidth, float windowHeight, Camera.Style style = Style.FreeFly)
  {
    _aspect = (float)windowWidth / windowHeight;
    _proj = Matrix4x4.CreatePerspectiveFieldOfView(
        (float)Math.PI / 4f, _aspect, 0.1f, 100f);
    _style = style;

    UpdatePosition();
  }

  // View direction from yaw/pitch (free-fly convention: yaw 0 -> -Z).
  // Pitch is clamped short of +-90 deg, so this is never parallel to UnitY.
  private Vector3 Forward
  {
    get
    {
      float cosPitch = MathF.Cos(_pitch);
      return new Vector3(
          MathF.Sin(_yaw) * cosPitch,
          MathF.Sin(_pitch),
         -MathF.Cos(_yaw) * cosPitch);
    }
  }

  public void OnZoom(float delta)
  {
    if (_style == Style.Orbit)
    {
      // Delta can be positive (zoom in) or negative (zoom out)
      _radius = Math.Max(0.5f, _radius - delta);
      UpdatePosition();
    }
    else
    {
      // Dolly along the view direction; no clamp, so the camera can pass
      // straight through geometry.
      _position += Forward * (delta * 10f);
      _viewDirty = true;
    }
  }

  public void OnRotate(float yawDelta, float pitchDelta)
  {
    _yaw += yawDelta;

    // FreeFly inverts pitch so moving the mouse down looks down (in orbit,
    // moving down raises the camera, which already aims the view down).
    if (_style == Style.FreeFly)
      pitchDelta = -pitchDelta;

    // Clamp pitch to avoid gimbal lock & flipping
    const float PI = (float)Math.PI;
    _pitch = Math.Clamp(_pitch + pitchDelta, -PI / 2.0f + 0.01f, PI / 2.0f - 0.01f);

    if (_style == Style.Orbit)
      UpdatePosition();
    else
      _viewDirty = true;
  }

  // FreeFly only. axis: X = strafe right, Y = rise, Z = forward, each in
  // [-1, 1] from held keys; dtSeconds scales movement to frame time.
  public void OnMove(Vector3 axis, float dtSeconds)
  {
    if (_style != Style.FreeFly || axis == Vector3.Zero)
      return;

    Vector3 forward = Forward;
    Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));

    _position += (right * axis.X + Vector3.UnitY * axis.Y + forward * axis.Z)
                 * (MoveSpeed * dtSeconds);
    _viewDirty = true;
  }

  private void UpdatePosition()
  {
    float cosPitch = (float)Math.Cos(_pitch);

    // Standard spherical to Cartesian for orbit camera
    _cameraPosition = new Vector3(
        x: _radius * (float)Math.Sin(_yaw) * cosPitch,
        y: _radius * (float)Math.Sin(_pitch),
        z: _radius * (float)Math.Cos(_yaw) * cosPitch
    );

    _viewDirty = true;
  }

  public Matrix4x4 Mvp
  {
    get
    {
      if (_viewDirty)
      {
        // Row-major .NET matrices: View * Projection.
        // Same call either way — orbit derives position from the angles and
        // keeps the target fixed; free-fly stores position and derives the
        // target from it.
        _cachedView = _style == Style.Orbit
          ? Matrix4x4.CreateLookAt(_cameraPosition, _cameraTarget, Vector3.UnitY)
          : Matrix4x4.CreateLookAt(_position, _position + Forward, Vector3.UnitY);
        _viewDirty = false;
      }
      return Matrix4x4.Multiply(_cachedView.Value, _proj);
    }
  }

  // Optional: reset to default position for the current style
  public void Reset()
  {
    _radius = 5f;
    _yaw = 0f;
    _pitch = 0f;
    _position = new Vector3(0f, 0f, 5f);
    UpdatePosition();
  }
}