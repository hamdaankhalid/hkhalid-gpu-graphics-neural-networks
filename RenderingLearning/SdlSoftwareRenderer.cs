using Silk.NET.SDL;

namespace RenderingLearning;

// CPU fills an int[] framebuffer each frame; SDL streams it to the window via a streaming texture.
public static unsafe class SdlSoftwareRenderer
{
  private const int WindowWidth = 1920 / 2;
  private const int WindowHeight = 1080 / 2;

  // --- Game of Life configuration ---
  // Each cell is drawn as a CellSize x CellSize block of pixels; the grid is
  // therefore coarser than the framebuffer, which keeps the simulation cheap
  // and the cells visible.
  private const int CellSize = 4;
  private const int GridW = WindowWidth / CellSize;
  private const int GridH = WindowHeight / CellSize;

  // The simulation advances on its own clock, independent of the render loop:
  // vsync paces frames at the monitor's refresh rate (60/120/144 Hz depending
  // on the machine), which is the wrong clock for the world to evolve on.
  private const ulong StepIntervalMs = 100; // 10 generations/sec

  private static Sdl _sdl = null!;
  private static int[] _frameBuffer = null!;
  private static Window* _window;
  private static Renderer* _renderer;
  private static Texture* _texture;

  // Double-buffered cell grids: Step() reads _cells (the current generation)
  // while writing _nextCells, then swaps them — the rules need the whole
  // previous generation intact while computing the next one.
  private static byte[] _cells = null!;
  private static byte[] _nextCells = null!;
  private static ulong _lastStepMs;

  // A seeder decides which cells start alive, given the cell's coordinates
  // and the grid dimensions.
  public delegate bool Seeder(int x, int y, int gridW, int gridH);

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

    _cells = new byte[GridW * GridH];
    _nextCells = new byte[GridW * GridH];

    // Start from a random soup at ~25% density. Swap in any other Seeder
    // (or stamp a known pattern into _cells) to change the starting point.
    var rng = new Random();
    Seed((x, y, w, h) => rng.Next(100) < 25);

    // Paint the initial generation so the window isn't black until the
    // first simulation step fires.
    PaintGridToFrameBuffer();

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

    // FPS tracking: count frames and report once a second. Measuring over a
    // whole second (rather than 1/frame-time each frame) averages out the
    // per-frame jitter that would make the number flicker unreadably.
    ulong fpsWindowStart = _sdl.GetTicks64();
    int framesThisWindow = 0;

    // The classic game loop: read input, advance the animation (here: compute
    // the frame's pixels on the CPU), then hand the finished frame to the GPU.
    while (PollEvents())
    {
      Render(_sdl.GetTicks64());
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
    }

    return true;
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

  // // "Software rendering": compute every pixel of the frame on the CPU.
  // // This writes a moving interference pattern into _frameBuffer:
  // //   i*i + j*j  makes concentric rings around the top-left corner, and
  // //   + ticks    (milliseconds since SDL init) shifts the value each frame,
  // //              which animates the rings outward.
  // // The | 0xFF000000 forces the alpha byte (the 'A' in Abgr8888) to 255 =
  // // fully opaque; the lower 3 bytes land in the B, G, R channels.
  // private static void Render(ulong ticks)
  // {
  //   for (int i = 0, c = 0; i < WindowHeight; i++)
  //   {
  //     for (int j = 0; j < WindowWidth; j++, c++)
  //     {
  //       _frameBuffer[c] = (int)(i * i + j * j + (long)ticks) | unchecked((int)0xFF000000);
  //     }
  //   }
  // }

  // Game of Life: advance the simulation when its step interval has elapsed,
  // and repaint the framebuffer only then — between steps the grid hasn't
  // changed, so Present() just re-uploads the same pixels.
  private static void Render(ulong ticks)
  {
    if (ticks - _lastStepMs >= StepIntervalMs)
    {
      Step();
      _lastStepMs = ticks;
      PaintGridToFrameBuffer();
    }
  }

  // Compute the next generation from the current one. The grid is toroidal:
  // neighbor lookups wrap around the edges, so gliders re-enter on the
  // opposite side instead of dying at the border.
  private static void Step()
  {
    for (int y = 0; y < GridH; y++)
    {
      for (int x = 0; x < GridW; x++)
      {
        int neighbors = 0;
        for (int dy = -1; dy <= 1; dy++)
        {
          for (int dx = -1; dx <= 1; dx++)
          {
            if (dx == 0 && dy == 0) continue;
            int nx = (x + dx + GridW) % GridW;
            int ny = (y + dy + GridH) % GridH;
            neighbors += _cells[ny * GridW + nx];
          }
        }
        byte alive = _cells[y * GridW + x];
        _nextCells[y * GridW + x] = (byte)(neighbors == 3 || (alive == 1 && neighbors == 2) ? 1 : 0);
      }
    }
    (_cells, _nextCells) = (_nextCells, _cells);
  }

  // Draw the current generation into the framebuffer, expanding each cell to
  // a CellSize x CellSize block. Colors are packed to match Abgr8888: in a
  // little-endian int the bytes are A, B, G, R from high to low.
  private static void PaintGridToFrameBuffer()
  {
    const int aliveColor = unchecked((int)0xFF00FF66);
    const int deadColor = unchecked((int)0xFF101010);
    for (int py = 0; py < WindowHeight; py++)
    {
      int rowBase = py * WindowWidth;
      int gridRow = (py / CellSize) * GridW;
      for (int px = 0; px < WindowWidth; px++)
      {
        _frameBuffer[rowBase + px] = _cells[gridRow + px / CellSize] == 1 ? aliveColor : deadColor;
      }
    }
  }

  // Fill the grid from a Seeder's alive/dead decision for every cell.
  private static void Seed(Seeder seeder)
  {
    for (int y = 0; y < GridH; y++)
    {
      for (int x = 0; x < GridW; x++)
      {
        _cells[y * GridW + x] = (byte)(seeder(x, y, GridW, GridH) ? 1 : 0);
      }
    }
  }
}
