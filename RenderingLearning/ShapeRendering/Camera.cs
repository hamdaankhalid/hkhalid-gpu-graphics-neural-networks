using System.Numerics;

public class Camera
{
  private readonly float _aspect;
  private readonly Matrix4x4 _proj;

  // Orbit camera state
  private Vector3 _cameraPosition;
  private Vector3 _cameraTarget = Vector3.Zero;
  private float _radius = 5f;
  private float _yaw = 0f;    // Radians, rotates around Y axis
  private float _pitch = 0f;  // Radians, rotates around local X axis

  // View matrix caching
  private bool _viewDirty = true;
  private Matrix4x4? _cachedView;

  public Camera(float windowWidth, float windowHeight)
  {
    _aspect = (float)windowWidth / windowHeight;
    _proj = Matrix4x4.CreatePerspectiveFieldOfView(
        (float)Math.PI / 4f, _aspect, 0.1f, 100f);

    UpdatePosition();
  }

  public void OnZoom(float delta)
  {
    // Delta can be positive (zoom in) or negative (zoom out)
    _radius = Math.Max(0.5f, _radius - delta);
    UpdatePosition();
  }

  public void OnRotate(float yawDelta, float pitchDelta)
  {
    _yaw += yawDelta;

    // Clamp pitch to avoid gimbal lock & flipping
    const float PI = (float)Math.PI;
    _pitch = Math.Clamp(_pitch + pitchDelta, -PI / 2.0f + 0.01f, PI / 2.0f - 0.01f);

    UpdatePosition();
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
        // Row-major .NET matrices: View * Projection
        _cachedView = Matrix4x4.CreateLookAt(
            _cameraPosition,
            _cameraTarget,
            Vector3.UnitY);
        _viewDirty = false;
      }
      return Matrix4x4.Multiply(_cachedView.Value, _proj);
    }
  }

  // Optional: reset to default orbit position
  public void Reset()
  {
    _radius = 5f;
    _yaw = 0f;
    _pitch = 0f;
    UpdatePosition();
  }
}