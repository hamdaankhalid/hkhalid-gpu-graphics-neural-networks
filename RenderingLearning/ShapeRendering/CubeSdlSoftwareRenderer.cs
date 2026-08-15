using System.Numerics;
using System.Runtime.CompilerServices;
using Silk.NET.SDL;

namespace RenderingLearning.ShapeRendering;

public static unsafe class CubeSdlSoftwareRenderer
{
  private const int WindowWidth = 1920; // / 2;
  private const int WindowHeight = 1080; /// 2;

  private const int wireColor = unchecked((int)0xFF00FF66);
  private const int bgColor = unchecked((int)0xFF101010);

  // The simulation advances on its own clock, independent of the render loop:
  // vsync paces frames at the monitor's refresh rate (60/120/144 Hz depending
  // on the machine), which is the wrong clock for the world to evolve on.
  private const ulong StepIntervalMs = 100; 

  private static Sdl _sdl = null!;
  private static int[] _frameBuffer = null!;
  private static Window* _window;
  private static Renderer* _renderer;
  private static Texture* _texture;

  private static Camera _camera = null!;

  // Held-key state for free-fly movement (WASD + Q/E). Keydown/Keyup events
  // set these; the main loop turns them into a per-frame move axis so motion
  // is smooth and frame-rate independent instead of stepping per key repeat.
  private static bool _moveFwd, _moveBack, _moveLeft, _moveRight, _moveUp, _moveDown;

  // Extrinsic state only: each entry says WHICH mesh and WHERE. The meshes
  // themselves are the shared flyweights in Meshes — spawning another cube
  // here would reuse Meshes.Cube's vertices, not copy them.
  private static SceneObject[] _scene = new SceneObject[3]
  {
       new(Meshes.Cube, Vector3.Zero, scale: 2f),
       new(Meshes.Triangle, new Vector3(3, 3, 3), scale: 2f),
       new(Meshes.Quad, new Vector3(-3, -3, -3), scale: 2f)
  };

  public static int Run()
  {
    _sdl = Sdl.GetApi();

    if (_sdl.Init(Sdl.InitVideo | Sdl.InitEvents) < 0)
    {
      return -1;
    }

    // The framebuffer lives in ordinary system RAM and is written by the CPU.
    // One int per pixel: 4 bytes holding A, B, G, R channels (see Render below).
    _frameBuffer = new int[WindowWidth * WindowHeight];

    _window = _sdl.CreateWindow(
      "SDL2 window",
      // WindowposUndefined = "I don't care where the OS places the window".
      // (Alternatives: WindowposCentered, or explicit x/y coordinates.)
      Sdl.WindowposUndefined, Sdl.WindowposUndefined,
      WindowWidth, WindowHeight,
      // Shown = make the window visible immediately. Other flags could add
      // Resizable, Fullscreen, Borderless, etc. (they OR together).
      (uint)WindowFlags.Shown);

    // The renderer is our handle to the GPU: it owns the window's back buffer
    // and all textures, and performs blits/present on the graphics hardware.
    //   -1          = let SDL pick the first driver that supports our flags
    //                 (Metal on macOS, Direct3D on Windows, OpenGL elsewhere).
    //   Accelerated = require a hardware (GPU) backend; without it SDL may
    //                 fall back to its slow pure-software renderer.
    _renderer = _sdl.CreateRenderer(_window, -1, (uint)(RendererFlags.Accelerated | RendererFlags.Presentvsync));

    RendererInfo rinfo = default;
    if (_sdl.GetRendererInfo(_renderer, ref rinfo) == 0)
    {
      RendererFlags flags = (RendererFlags)rinfo.Flags;
      bool isSoftware = (flags & RendererFlags.Software) != 0;
      bool isAccelerated = (flags & RendererFlags.Accelerated) != 0;
      Console.WriteLine($"Is Software? {isSoftware} | Is Accelerated? {isAccelerated} ");
    }


    // The texture is a pixel buffer that lives in GPU memory.
    //   Abgr8888  = 32-bit pixels laid out so that in a little-endian int the
    //               low byte is R and the high byte is A — this matches how
    //               Render() packs its ints, so no channel swizzling is needed.
    //   Streaming = we intend to rewrite the whole texture every frame from
    //               the CPU; this makes it lockable (see LockTexture below)
    //               and tells the driver to place it in memory optimized for
    //               frequent CPU->GPU uploads. (Static would be for upload-once
    //               sprites; Target for textures the GPU renders into.)
    _texture = _sdl.CreateTexture(
      _renderer,
      (uint)PixelFormatEnum.Abgr8888,
      (int)TextureAccess.Streaming,
      WindowWidth, WindowHeight);

    if (_window == null || _renderer == null || _texture == null)
    {
      return -1;
    }

    // Style.Orbit = mouse orbits a fixed target, wheel zooms.
    // Style.FreeFly = mouse looks, WASD flies, Q/E descend/rise, wheel dollies.
    _camera = new Camera(WindowWidth, WindowHeight, Camera.Style.FreeFly);

    // FPS tracking: count frames and report once a second. Measuring over a
    // whole second (rather than 1/frame-time each frame) averages out the
    // per-frame jitter that would make the number flicker unreadably.
    ulong fpsWindowStart = _sdl.GetTicks64();
    int framesThisWindow = 0;


    ulong lastFrameMs = _sdl.GetTicks64();

    // The classic game loop: read input, advance the animation (here: compute
    // the frame's pixels on the CPU), then hand the finished frame to the GPU.
    while (PollEvents())
    {
      ulong frameStart = _sdl.GetTicks64();
      _camera.OnMove(BuildMoveAxis(), (frameStart - lastFrameMs) / 1000f);
      lastFrameMs = frameStart;

      Render(frameStart);
      Present();

      framesThisWindow++;
      ulong now = _sdl.GetTicks64(); // returns milliseconds unlike dotnet's ticks
      if (now - fpsWindowStart >= 1000) // Every second we calc average fps
      {
        double fps = framesThisWindow * 1000.0 / (now - fpsWindowStart);
        _sdl.SetWindowTitle(_window, $"SDL2 window — {fps:F1} FPS");
        fpsWindowStart = now;
        framesThisWindow = 0;
      }
    }

    _sdl.DestroyTexture(_texture);
    _sdl.DestroyRenderer(_renderer);
    _sdl.DestroyWindow(_window);
    _sdl.Quit();

    return 0;
  }

  // Drain this frame's input. Returns false when the user asked to quit
  // (window close or Escape), which ends the main loop.
  private static bool PollEvents()
  {
    Event e;
    while (_sdl.PollEvent(&e) != 0)
    {
      if (e.Type == (uint)EventType.Quit)
      {
        return false;
      }
      if (e.Type == (uint)EventType.Keyup && e.Key.Keysym.Sym == (int)KeyCode.KEscape)
      {
        return false;
      }
      if (e.Type == (uint)EventType.Mousemotion)
      {
        _camera.OnRotate((float)e.Motion.Xrel * 0.01f, (float)e.Motion.Yrel * 0.01f);
      }
      else if (e.Type == (uint)EventType.Mousewheel)
      {
        // SDL2 mouse wheel DX/DY or X/Y depending on version.
        // In Silk.NET.SDL, it's usually e.Wheel.X or e.Wheel.DX for horizontal, e.Wheel.Y or e.Wheel.DY for vertical.
        _camera.OnZoom((float)e.Wheel.Y * 0.1f);
      }
      else if (e.Type == (uint)EventType.Keydown || e.Type == (uint)EventType.Keyup)
      {
        bool down = e.Type == (uint)EventType.Keydown;
        switch (e.Key.Keysym.Sym)
        {
          case (int)KeyCode.KW: _moveFwd = down; break;
          case (int)KeyCode.KS: _moveBack = down; break;
          case (int)KeyCode.KA: _moveLeft = down; break;
          case (int)KeyCode.KD: _moveRight = down; break;
          case (int)KeyCode.KE: _moveUp = down; break;
          case (int)KeyCode.KQ: _moveDown = down; break;
        }
      }
    }

    return true;
  }

  // Collapse held keys into a single move axis for Camera.OnMove:
  // X = strafe (A/D), Y = rise (Q/E), Z = forward (W/S). Opposing keys cancel.
  private static Vector3 BuildMoveAxis()
  {
    return new Vector3(
      (_moveRight ? 1f : 0f) - (_moveLeft ? 1f : 0f),
      (_moveUp ? 1f : 0f) - (_moveDown ? 1f : 0f),
      (_moveFwd ? 1f : 0f) - (_moveBack ? 1f : 0f));
  }

  // Display the frame that Render() just finished: upload the framebuffer to
  // the GPU texture, draw it to the back buffer, and flip it to the screen.
  private static void Present()
  {
    // --- Upload the CPU framebuffer to the GPU texture ---
    // The CPU (where Render() computed the pixels, in _frameBuffer) and the
    // GPU (where the texture lives) have separate memory. LockTexture bridges
    // them: it gives us a raw pointer (pix) we can write the new frame into,
    // and guarantees the GPU isn't reading the texture while we do — without
    // the lock we could scribble over a texture mid-draw and get torn/corrupt
    // frames, or race the driver's own bookkeeping.
    void* pix;
    int pitch;

    // pitch = bytes per row of the locked texture. The GPU may pad each row
    // for alignment, so pitch can be larger than WindowWidth * 4 — which is
    // why we copy row by row instead of one big copy.
    _sdl.LockTexture(_texture, null, &pix, &pitch);

    fixed (int* src = _frameBuffer)
    {
      // dp walks the framebuffer in ints (WindowWidth per row);
      // sp walks the texture in bytes (pitch per row).
      for (int i = 0, sp = 0, dp = 0; i < WindowHeight; i++, dp += WindowWidth, sp += pitch)
      {
        Buffer.MemoryCopy(src + dp, (byte*)pix + sp, WindowWidth * 4, WindowWidth * 4);
      }
    }

    // Unlock commits our writes: the driver uploads the pixels to GPU memory
    // and the pointer from LockTexture becomes invalid. From here on, the
    // frame is the GPU's job.
    _sdl.UnlockTexture(_texture);

    // Draw the texture onto the window's back buffer. The two nulls mean
    // "entire texture" -> "entire window" (the GPU scales if sizes differ).
    _sdl.RenderCopy(_renderer, _texture, null, null);

    // Flip the back buffer to the screen, making the frame visible. With
    // Presentvsync this call also blocks until the display's next refresh,
    // which is what paces the whole loop.
    _sdl.RenderPresent(_renderer);
  }

  private static void Render(ulong ticks)
  {
    // 1. Clear framebuffer to background color
    Array.Fill(_frameBuffer, bgColor);

    Matrix4x4 viewProj = _camera.Mvp;

    foreach (SceneObject obj in _scene)
    {
      // Extrinsic state folds in here: the object's model matrix goes in
      // front of view * projection (row-major .NET order), so the shared
      // local-space vertices are placed per object during the multiply.
      Matrix4x4 mvp = obj.ModelMatrix * viewProj;

      Vector3[] verts = obj.Mesh.Vertices;
      for (int i = 0; i < verts.Length; i += 3)
      {
        if (ProjectTriangle(in verts[i], in verts[i + 1], in verts[i + 2], mvp,
              out Vector3 s0, out Vector3 s1, out Vector3 s2))
        {
          RasterizeTriangle(ref s0, ref s1, ref s2); // Draw to _frameBuffer
        }
      }
    }
  }

  private static bool ProjectTriangle(in Vector3 a, in Vector3 b, in Vector3 c, Matrix4x4 mvp,
      out Vector3 screenA, out Vector3 screenB, out Vector3 screenC)
  {
    var v0 = TransformClip(in a, mvp);
    var v1 = TransformClip(in b, mvp);
    var v2 = TransformClip(in c, mvp);

    // Perspective divide & backface/culling check
    if (v0.W <= 0 || v1.W <= 0 || v2.W <= 0)
    {
      screenA = default;
      screenB = default;
      screenC = default;
      return false;
    }

    // Convert to normalized device coordinates [-1, 1]
    Vector3 ndc0 = new(v0.X / v0.W, v0.Y / v0.W, v0.Z / v0.W);
    Vector3 ndc1 = new(v1.X / v1.W, v1.Y / v1.W, v1.Z / v1.W);
    Vector3 ndc2 = new(v2.X / v2.W, v2.Y / v2.W, v2.Z / v2.W);

    // Convert NDC to screen space (Y flips because FBs top-left origin)
    screenA = new(
      (int)((ndc0.X * 0.5f + 0.5f) * WindowWidth),
      (int)((-ndc0.Y * 0.5f + 0.5f) * WindowHeight),
      ndc0.Z);

    screenB = new(
      (int)((ndc1.X * 0.5f + 0.5f) * WindowWidth),
      (int)((-ndc1.Y * 0.5f + 0.5f) * WindowHeight),
      ndc1.Z);

    screenC = new(
      (int)((ndc2.X * 0.5f + 0.5f) * WindowWidth),
      (int)((-ndc2.Y * 0.5f + 0.5f) * WindowHeight),
      ndc2.Z);

    return true;
  }

  private static Vector4 TransformClip(in Vector3 v, Matrix4x4 mvp)
  {
    // Manual MVP multiply (avoids matrix-vector overhead in tight loops)
    float x = v.X * mvp.M11 + v.Y * mvp.M21 + v.Z * mvp.M31 + mvp.M41;
    float y = v.X * mvp.M12 + v.Y * mvp.M22 + v.Z * mvp.M32 + mvp.M42;
    float z = v.X * mvp.M13 + v.Y * mvp.M23 + v.Z * mvp.M33 + mvp.M43;
    float w = v.X * mvp.M14 + v.Y * mvp.M24 + v.Z * mvp.M34 + mvp.M44;
    return new Vector4(x, y, z, w);
  }

  private static void RasterizeTriangle(ref Vector3 a, ref Vector3 b, ref Vector3 c)
  {
    // Simple wireframe draw (replace with scanline fill later if needed)
    DrawLine(ref a, ref b, wireColor);
    DrawLine(ref b, ref c, wireColor);
    DrawLine(ref c, ref a, wireColor);
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static void DrawLine(ref Vector3 p1, ref Vector3 p2, int color)
  {
    int x0 = (int)p1.X, y0 = (int)p1.Y;
    int x1 = (int)p2.X, y1 = (int)p2.Y;

    int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
    int dy = Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
    int err = dx - dy;

    // Bounds check to prevent segfaults
    while (true)
    {
      if (x0 >= 0 && x0 < WindowWidth && y0 >= 0 && y0 < WindowHeight)
        _frameBuffer[y0 * WindowWidth + x0] = color;

      if (x0 == x1 && y0 == y1) break;
      int e2 = 2 * err;
      if (e2 > -dy) { err -= dy; x0 += sx; }
      if (e2 < dx) { err += dx; y0 += sy; }
    }
  }
}
