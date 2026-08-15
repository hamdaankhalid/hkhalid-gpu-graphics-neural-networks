using Silk.NET.Core.Native;
using Silk.NET.Maths;
using Silk.NET.WebGPU;
using Silk.NET.Windowing;
using WgpuBuffer = Silk.NET.WebGPU.Buffer;

namespace RenderingLearning;

// GPU-resident Game of Life — the inverse of SdlSoftwareRenderer's division of
// labor. There, the CPU computed the simulation AND every pixel, and the GPU
// only blitted a finished image. Here the cell grid lives in GPU memory and
// never comes back:
//   - a COMPUTE shader advances the simulation (one GPU thread per cell),
//     reading generation N from one storage buffer and writing N+1 into
//     another, then the two swap roles ("ping-pong" — the GPU equivalent of
//     SdlSoftwareRenderer's (_cells, _nextCells) swap);
//   - a RENDER pipeline draws a single triangle that covers the whole window,
//     and its fragment shader colors each pixel by looking up the cell it
//     falls in. There is no framebuffer array, no per-frame upload, and no
//     "paint" loop — the loop IS the hardware.
// The CPU's entire per-frame job is: accumulate time, record ~20 commands,
// submit. The only CPU->GPU pixel-ish transfer ever is the one-time seed.
public static unsafe class WebGpuRendering
{
  private const int WindowWidth = 1920 / 2;
  private const int WindowHeight = 1080 / 2;

  // Same world shape as SdlSoftwareRenderer: each cell renders as a
  // CellSize x CellSize block of window pixels.
  private const int CellSize = 4;
  private const int GridW = WindowWidth / CellSize;
  private const int GridH = WindowHeight / CellSize;

  // The simulation clock, decoupled from the vsync-paced render loop.
  private const double StepIntervalSeconds = 0.1; // 10 generations/sec

  // Compute shaders launch threads in fixed-size blocks ("workgroups").
  // 8x8 = 64 threads per group is a safe, portable choice.
  private const uint WorkgroupSize = 8;

  private static IWindow _Window = null!;
  private static WebGPU wgpu = null!;
  private static Surface* _Surface;
  private static SurfaceConfiguration _SurfaceConfiguration;
  private static SurfaceCapabilities _SurfaceCapabilities;

  private static Instance* _Instance;
  private static Adapter* _Adapter;
  private static Device* _Device;
  private static Queue* _Queue;

  // The two cell buffers. Which one holds the CURRENT generation alternates
  // every step; _currentIsA tracks it. Each is GridW*GridH u32s — u32 rather
  // than byte because WGSL storage buffers address 4-byte words.
  private static WgpuBuffer* _CellsA;
  private static WgpuBuffer* _CellsB;
  private static bool _currentIsA = true;

  private static ShaderModule* _StepShader;
  private static ShaderModule* _DrawShader;

  private static BindGroupLayout* _StepBindGroupLayout;
  private static BindGroupLayout* _DrawBindGroupLayout;
  private static PipelineLayout* _StepPipelineLayout;
  private static PipelineLayout* _DrawPipelineLayout;
  private static ComputePipeline* _StepPipeline;
  private static RenderPipeline* _DrawPipeline;

  // A bind group is a pre-baked "these buffers plug into these shader slots"
  // object. We need both directions of the ping-pong for stepping, and a
  // read-only view of each buffer for drawing — made once, swapped by choice
  // at encode time instead of being rebuilt per frame.
  private static BindGroup* _StepAtoB;
  private static BindGroup* _StepBtoA;
  private static BindGroup* _DrawA;
  private static BindGroup* _DrawB;

  private static double _stepAccumulator;
  private static double _fpsAccumulator;
  private static int _framesThisWindow;

  // Same seeding contract as SdlSoftwareRenderer.
  public delegate bool Seeder(int x, int y, int gridW, int gridH);

  // The WGSL shaders live in Shaders/*.wgsl (step = the simulation compute
  // shader, draw = the fullscreen-triangle presentation) and are embedded
  // into the assembly at build time — see the csproj — so nothing has to be
  // resolved on disk at runtime. Their grid constants are hardcoded literals
  // in the WGSL; this loader validates them against the C# constants so the
  // two sides can't silently drift apart.
  private static string LoadShaderSource(string fileName)
  {
    string resourceName = $"RenderingLearning.Shaders.{fileName}";
    using Stream? stream = typeof(WebGpuRendering).Assembly.GetManifestResourceStream(resourceName);
    if (stream == null)
    {
      throw new InvalidOperationException(
          $"Embedded shader '{resourceName}' not found — is it listed as an EmbeddedResource in the csproj?");
    }
    using StreamReader reader = new StreamReader(stream);
    string wgsl = reader.ReadToEnd();

    if (!wgsl.Contains($"GRID_W : u32 = {GridW}u") || !wgsl.Contains($"GRID_H : u32 = {GridH}u"))
    {
      throw new InvalidOperationException(
          $"{fileName}: GRID_W/GRID_H don't match the C# grid ({GridW}x{GridH}) — update the constants in the shader.");
    }
    if (wgsl.Contains("@compute") && !wgsl.Contains($"@workgroup_size({WorkgroupSize}, {WorkgroupSize})"))
    {
      throw new InvalidOperationException(
          $"{fileName}: @workgroup_size doesn't match the C# WorkgroupSize ({WorkgroupSize}) — update it in the shader.");
    }

    return wgsl;
  }

  public static int Run()
  {
    WindowOptions options = WindowOptions.Default;
    // GraphicsAPI.None: the windowing layer must NOT create an OpenGL
    // context — WebGPU talks to the OS surface directly.
    options.API = GraphicsAPI.None;
    options.Size = new Vector2D<int>(WindowWidth, WindowHeight);
    options.FramesPerSecond = 60;
    options.UpdatesPerSecond = 60;
    options.Position = new Vector2D<int>(0, 0);
    options.Title = "WebGPU Game of Life";
    options.IsVisible = true;
    options.ShouldSwapAutomatically = false;
    options.IsContextControlDisabled = true;

    _Window = Window.Create(options);
    wgpu = WebGPU.GetApi();

    _Window.Load += OnLoad;
    _Window.Closing += OnClose;
    _Window.Render += WindowOnRender;
    _Window.FramebufferResize += FramebufferResize;

    _Window.Run();

    return 0;
  }

  private static void OnLoad()
  {
    InstanceDescriptor instanceDescriptor = new InstanceDescriptor();
    _Instance = wgpu.CreateInstance(&instanceDescriptor);

    _Surface = _Window.CreateWebGPUSurface(wgpu, _Instance);

    { //Get adapter
      RequestAdapterOptions requestAdapterOptions = new RequestAdapterOptions
      {
        CompatibleSurface = _Surface
      };

      wgpu.InstanceRequestAdapter
      (
          _Instance,
          &requestAdapterOptions,
          new PfnRequestAdapterCallback((_, adapter1, _, _) => _Adapter = adapter1),
          null
      );

      Console.WriteLine($"Got adapter {(nuint)_Adapter:X}");
    } //Get adapter

    PrintAdapterFeatures();

    { //Get device
      DeviceDescriptor deviceDescriptor = new DeviceDescriptor
      {
        DeviceLostCallback = new PfnDeviceLostCallback(DeviceLost),
      };

      wgpu.AdapterRequestDevice
      (
          _Adapter,
          in deviceDescriptor,
          new PfnRequestDeviceCallback((_, device1, _, _) => _Device = device1),
          null
      );

      Console.WriteLine($"Got device {(nuint)_Device:X}");
    } //Get device

    wgpu.DeviceSetUncapturedErrorCallback(_Device, new PfnErrorCallback(UncapturedError), null);
    _Queue = wgpu.DeviceGetQueue(_Device);

    CreateCellBuffers();

    // Start from a random soup at ~25% density, seeded once, on the CPU —
    // after this upload the grid never leaves the GPU.
    var rng = new Random();
    Seed((x, y, w, h) => rng.Next(100) < 25);

    CreateStepPipeline();

    wgpu.SurfaceGetCapabilities(_Surface, _Adapter, ref _SurfaceCapabilities);
    CreateDrawPipeline();

    CreateBindGroups();
    CreateSwapchain();
  }

  private static void CreateCellBuffers()
  {
    BufferDescriptor bufferDescriptor = new BufferDescriptor
    {
      // Storage = readable/writable from shaders; CopyDst = the seed upload
      // may write into it.
      Usage = BufferUsage.Storage | BufferUsage.CopyDst,
      Size = (ulong)(GridW * GridH * sizeof(uint)),
    };

    _CellsA = wgpu.DeviceCreateBuffer(_Device, &bufferDescriptor);
    _CellsB = wgpu.DeviceCreateBuffer(_Device, &bufferDescriptor);
  }

  // Fill the grid from a Seeder's alive/dead decision for every cell, then
  // upload it into buffer A (the starting "current" generation).
  private static void Seed(Seeder seeder)
  {
    uint[] cells = new uint[GridW * GridH];
    for (int y = 0; y < GridH; y++)
    {
      for (int x = 0; x < GridW; x++)
      {
        cells[y * GridW + x] = seeder(x, y, GridW, GridH) ? 1u : 0u;
      }
    }

    fixed (uint* src = cells)
    {
      wgpu.QueueWriteBuffer(_Queue, _CellsA, 0, src, (nuint)(cells.Length * sizeof(uint)));
    }
    _currentIsA = true;
  }

  private static void CreateStepPipeline()
  {
    _StepShader = CreateShaderModule(LoadShaderSource("step.wgsl"));
    Console.WriteLine($"Created step shader {(nuint)_StepShader:X}");

    // The bind group LAYOUT declares the shader's slot signature: slot 0 is
    // a read-only storage buffer, slot 1 a writable one, both compute-only.
    BindGroupLayoutEntry* layoutEntries = stackalloc BindGroupLayoutEntry[2];
    layoutEntries[0] = new BindGroupLayoutEntry
    {
      Binding = 0,
      Visibility = ShaderStage.Compute,
      Buffer = new BufferBindingLayout { Type = BufferBindingType.ReadOnlyStorage }
    };
    layoutEntries[1] = new BindGroupLayoutEntry
    {
      Binding = 1,
      Visibility = ShaderStage.Compute,
      Buffer = new BufferBindingLayout { Type = BufferBindingType.Storage }
    };

    BindGroupLayoutDescriptor bindGroupLayoutDescriptor = new BindGroupLayoutDescriptor
    {
      EntryCount = 2,
      Entries = layoutEntries
    };
    _StepBindGroupLayout = wgpu.DeviceCreateBindGroupLayout(_Device, &bindGroupLayoutDescriptor);

    BindGroupLayout* bindGroupLayout = _StepBindGroupLayout;
    PipelineLayoutDescriptor pipelineLayoutDescriptor = new PipelineLayoutDescriptor
    {
      BindGroupLayoutCount = 1,
      BindGroupLayouts = &bindGroupLayout
    };
    _StepPipelineLayout = wgpu.DeviceCreatePipelineLayout(_Device, &pipelineLayoutDescriptor);

    ComputePipelineDescriptor computePipelineDescriptor = new ComputePipelineDescriptor
    {
      Layout = _StepPipelineLayout,
      Compute = new ProgrammableStageDescriptor
      {
        Module = _StepShader,
        EntryPoint = (byte*)SilkMarshal.StringToPtr("step")
      }
    };
    _StepPipeline = wgpu.DeviceCreateComputePipeline(_Device, &computePipelineDescriptor);

    Console.WriteLine($"Created step pipeline {(nuint)_StepPipeline:X}");
  }

  private static void CreateDrawPipeline()
  {
    _DrawShader = CreateShaderModule(LoadShaderSource("draw.wgsl"));
    Console.WriteLine($"Created draw shader {(nuint)_DrawShader:X}");

    // One slot: the current generation, readable from the fragment stage.
    BindGroupLayoutEntry layoutEntry = new BindGroupLayoutEntry
    {
      Binding = 0,
      Visibility = ShaderStage.Fragment,
      Buffer = new BufferBindingLayout { Type = BufferBindingType.ReadOnlyStorage }
    };

    BindGroupLayoutDescriptor bindGroupLayoutDescriptor = new BindGroupLayoutDescriptor
    {
      EntryCount = 1,
      Entries = &layoutEntry
    };
    _DrawBindGroupLayout = wgpu.DeviceCreateBindGroupLayout(_Device, &bindGroupLayoutDescriptor);

    BindGroupLayout* bindGroupLayout = _DrawBindGroupLayout;
    PipelineLayoutDescriptor pipelineLayoutDescriptor = new PipelineLayoutDescriptor
    {
      BindGroupLayoutCount = 1,
      BindGroupLayouts = &bindGroupLayout
    };
    _DrawPipelineLayout = wgpu.DeviceCreatePipelineLayout(_Device, &pipelineLayoutDescriptor);

    BlendState blendState = new BlendState
    {
      Color = new BlendComponent
      {
        SrcFactor = BlendFactor.One,
        DstFactor = BlendFactor.Zero,
        Operation = BlendOperation.Add
      },
      Alpha = new BlendComponent
      {
        SrcFactor = BlendFactor.One,
        DstFactor = BlendFactor.Zero,
        Operation = BlendOperation.Add
      }
    };

    ColorTargetState colorTargetState = new ColorTargetState
    {
      Format = _SurfaceCapabilities.Formats[0],
      Blend = &blendState,
      WriteMask = ColorWriteMask.All
    };

    FragmentState fragmentState = new FragmentState
    {
      Module = _DrawShader,
      TargetCount = 1,
      Targets = &colorTargetState,
      EntryPoint = (byte*)SilkMarshal.StringToPtr("fs_main")
    };

    RenderPipelineDescriptor renderPipelineDescriptor = new RenderPipelineDescriptor
    {
      Layout = _DrawPipelineLayout,
      Vertex = new VertexState
      {
        Module = _DrawShader,
        EntryPoint = (byte*)SilkMarshal.StringToPtr("vs_main"),
      },
      Primitive = new PrimitiveState
      {
        Topology = PrimitiveTopology.TriangleList,
        StripIndexFormat = IndexFormat.Undefined,
        FrontFace = FrontFace.Ccw,
        CullMode = CullMode.None
      },
      Multisample = new MultisampleState
      {
        Count = 1,
        Mask = ~0u,
        AlphaToCoverageEnabled = false
      },
      Fragment = &fragmentState,
      DepthStencil = null
    };

    _DrawPipeline = wgpu.DeviceCreateRenderPipeline(_Device, &renderPipelineDescriptor);

    Console.WriteLine($"Created draw pipeline {(nuint)_DrawPipeline:X}");
  }

  private static ShaderModule* CreateShaderModule(string wgsl)
  {
    ShaderModuleWGSLDescriptor wgslDescriptor = new ShaderModuleWGSLDescriptor
    {
      Code = (byte*)SilkMarshal.StringToPtr(wgsl),
      Chain = new ChainedStruct
      {
        SType = SType.ShaderModuleWgslDescriptor
      }
    };

    ShaderModuleDescriptor shaderModuleDescriptor = new ShaderModuleDescriptor
    {
      NextInChain = (ChainedStruct*)(&wgslDescriptor),
    };

    return wgpu.DeviceCreateShaderModule(_Device, &shaderModuleDescriptor);
  }

  private static void CreateBindGroups()
  {
    ulong bufferSize = (ulong)(GridW * GridH * sizeof(uint));

    BindGroup* MakeStepGroup(WgpuBuffer* src, WgpuBuffer* dst)
    {
      BindGroupEntry* entries = stackalloc BindGroupEntry[2];
      entries[0] = new BindGroupEntry { Binding = 0, Buffer = src, Offset = 0, Size = bufferSize };
      entries[1] = new BindGroupEntry { Binding = 1, Buffer = dst, Offset = 0, Size = bufferSize };
      BindGroupDescriptor descriptor = new BindGroupDescriptor
      {
        Layout = _StepBindGroupLayout,
        EntryCount = 2,
        Entries = entries
      };
      return wgpu.DeviceCreateBindGroup(_Device, &descriptor);
    }

    BindGroup* MakeDrawGroup(WgpuBuffer* cells)
    {
      BindGroupEntry entry = new BindGroupEntry { Binding = 0, Buffer = cells, Offset = 0, Size = bufferSize };
      BindGroupDescriptor descriptor = new BindGroupDescriptor
      {
        Layout = _DrawBindGroupLayout,
        EntryCount = 1,
        Entries = &entry
      };
      return wgpu.DeviceCreateBindGroup(_Device, &descriptor);
    }

    _StepAtoB = MakeStepGroup(_CellsA, _CellsB);
    _StepBtoA = MakeStepGroup(_CellsB, _CellsA);
    _DrawA = MakeDrawGroup(_CellsA);
    _DrawB = MakeDrawGroup(_CellsB);
  }

  private static void FramebufferResize(Vector2D<int> obj)
  {
    CreateSwapchain();
  }

  private static void CreateSwapchain()
  {
    _SurfaceConfiguration = new SurfaceConfiguration
    {
      Usage = TextureUsage.RenderAttachment,
      Format = _SurfaceCapabilities.Formats[0],
      PresentMode = PresentMode.Fifo,
      Device = _Device,
      Width = (uint)_Window.FramebufferSize.X,
      Height = (uint)_Window.FramebufferSize.Y
    };

    wgpu.SurfaceConfigure(_Surface, ref _SurfaceConfiguration);
  }

  private static void WindowOnRender(double delta)
  {
    // FPS in the title, averaged over one-second windows like the SDL version.
    _fpsAccumulator += delta;
    _framesThisWindow++;
    if (_fpsAccumulator >= 1.0)
    {
      double fps = _framesThisWindow / _fpsAccumulator;
      _Window.Title = $"WebGPU Game of Life — {fps:F1} FPS";
      _fpsAccumulator = 0;
      _framesThisWindow = 0;
    }

    _stepAccumulator += delta;
    bool stepDue = _stepAccumulator >= StepIntervalSeconds;

    SurfaceTexture surfaceTexture;
    wgpu.SurfaceGetCurrentTexture(_Surface, &surfaceTexture);
    switch (surfaceTexture.Status)
    {
      case SurfaceGetCurrentTextureStatus.Timeout:
      case SurfaceGetCurrentTextureStatus.Outdated:
      case SurfaceGetCurrentTextureStatus.Lost:
        // Recreate swapchain,
        wgpu.TextureRelease(surfaceTexture.Texture);
        CreateSwapchain();
        // Skip this frame
        return;
      case SurfaceGetCurrentTextureStatus.OutOfMemory:
      case SurfaceGetCurrentTextureStatus.DeviceLost:
      case SurfaceGetCurrentTextureStatus.Force32:
        throw new Exception($"What is going on bros... {surfaceTexture.Status}");
    }

    TextureView* view = wgpu.TextureCreateView(surfaceTexture.Texture, null);

    CommandEncoderDescriptor commandEncoderDescriptor = new CommandEncoderDescriptor();
    CommandEncoder* encoder = wgpu.DeviceCreateCommandEncoder(_Device, &commandEncoderDescriptor);

    // --- Simulation: one compute dispatch, only when a step is due ---
    // Recording the pass costs the CPU a few calls; the actual work happens
    // on the GPU when the queue executes. The ping-pong swap is just picking
    // the other pre-built bind group and flipping the flag.
    if (stepDue)
    {
      _stepAccumulator = 0;

      ComputePassDescriptor computePassDescriptor = new ComputePassDescriptor();
      ComputePassEncoder* computePass = wgpu.CommandEncoderBeginComputePass(encoder, &computePassDescriptor);
      wgpu.ComputePassEncoderSetPipeline(computePass, _StepPipeline);
      wgpu.ComputePassEncoderSetBindGroup(computePass, 0, _currentIsA ? _StepAtoB : _StepBtoA, 0, null);
      // Enough workgroups to cover the grid, rounding up; the shader masks
      // off the overhang.
      wgpu.ComputePassEncoderDispatchWorkgroups(
          computePass,
          (GridW + WorkgroupSize - 1) / WorkgroupSize,
          (GridH + WorkgroupSize - 1) / WorkgroupSize,
          1);
      wgpu.ComputePassEncoderEnd(computePass);
      wgpu.ComputePassEncoderRelease(computePass);

      _currentIsA = !_currentIsA;
    }

    // --- Presentation: fullscreen triangle reading the current generation ---
    RenderPassColorAttachment colorAttachment = new RenderPassColorAttachment
    {
      View = view,
      ResolveTarget = null,
      LoadOp = LoadOp.Clear,
      StoreOp = StoreOp.Store,
      ClearValue = new Color { R = 0, G = 0, B = 0, A = 1 }
    };

    var renderPassDescriptor = new RenderPassDescriptor
    {
      ColorAttachments = &colorAttachment,
      ColorAttachmentCount = 1,
      DepthStencilAttachment = null
    };

    RenderPassEncoder* renderPass = wgpu.CommandEncoderBeginRenderPass(encoder, &renderPassDescriptor);
    wgpu.RenderPassEncoderSetPipeline(renderPass, _DrawPipeline);
    wgpu.RenderPassEncoderSetBindGroup(renderPass, 0, _currentIsA ? _DrawA : _DrawB, 0, null);
    wgpu.RenderPassEncoderDraw(renderPass, 3, 1, 0, 0);
    wgpu.RenderPassEncoderEnd(renderPass);

    CommandBufferDescriptor cbd = new CommandBufferDescriptor();
    CommandBuffer* commandBuffer = wgpu.CommandEncoderFinish(encoder, &cbd);

    wgpu.QueueSubmit(_Queue, 1, &commandBuffer);
    wgpu.SurfacePresent(_Surface);
    wgpu.CommandBufferRelease(commandBuffer);
    wgpu.RenderPassEncoderRelease(renderPass);
    wgpu.CommandEncoderRelease(encoder);
    wgpu.TextureViewRelease(view);
    wgpu.TextureRelease(surfaceTexture.Texture);
  }

  private static void OnClose()
  {
    wgpu.BindGroupRelease(_StepAtoB);
    wgpu.BindGroupRelease(_StepBtoA);
    wgpu.BindGroupRelease(_DrawA);
    wgpu.BindGroupRelease(_DrawB);
    wgpu.BindGroupLayoutRelease(_StepBindGroupLayout);
    wgpu.BindGroupLayoutRelease(_DrawBindGroupLayout);
    wgpu.PipelineLayoutRelease(_StepPipelineLayout);
    wgpu.PipelineLayoutRelease(_DrawPipelineLayout);
    wgpu.ComputePipelineRelease(_StepPipeline);
    wgpu.RenderPipelineRelease(_DrawPipeline);
    wgpu.ShaderModuleRelease(_StepShader);
    wgpu.ShaderModuleRelease(_DrawShader);
    wgpu.BufferRelease(_CellsA);
    wgpu.BufferRelease(_CellsB);
    wgpu.DeviceRelease(_Device);
    wgpu.AdapterRelease(_Adapter);
    wgpu.SurfaceRelease(_Surface);
    wgpu.InstanceRelease(_Instance);

    wgpu.Dispose();
  }

  private static void PrintAdapterFeatures()
  {
    int count = (int)wgpu.AdapterEnumerateFeatures(_Adapter, null);

    FeatureName* features = stackalloc FeatureName[count];

    wgpu.AdapterEnumerateFeatures(_Adapter, features);

    Console.WriteLine("Adapter features:");

    for (int i = 0; i < count; i++)
    {
      Console.WriteLine($"\t{features[i]}");
    }
  }

  private static void DeviceLost(DeviceLostReason arg0, byte* arg1, void* arg2)
  {
    Console.WriteLine($"Device lost! Reason: {arg0} Message: {SilkMarshal.PtrToString((nint)arg1)}");
  }

  private static void UncapturedError(ErrorType arg0, byte* arg1, void* arg2)
  {
    Console.WriteLine($"{arg0}: {SilkMarshal.PtrToString((nint)arg1)}");
  }
}
