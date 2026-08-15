using System.Numerics;
using Silk.NET.Core.Native;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.WebGPU;
using Silk.NET.Windowing;

namespace RenderingLearning.ShapeRendering;

public static unsafe class CubeWebgpuRenderer
{
  private const int WindowWidth = 1920;
  private const int WindowHeight = 1080;

  #region Member variables related to Windowing and native resources

  private static IWindow _Window = null!;
  private static WebGPU wgpu = null!;
  private static Surface* _Surface;

  private static Instance* _Instance;
  private static Adapter* _Adapter;
  private static Device* _Device;
  private static Queue* _Queue;
  private static Silk.NET.WebGPU.Buffer* _buf;

  private static ShaderModule* _RenderingShader;

  #endregion

  #region Scene
  private static Camera _camera = null!;

  private static byte _nextValidObjIdCtr = 3;

  // Scene starts with 3 objects, later more may be added. Keyed by object id;
  // the id will also pick the object's uniform buffer + bind group slot.
  private static Dictionary<byte, SceneObject> _scene = new()
  {
    [0] = new SceneObject(Meshes.Cube, Vector3.Zero, scale: 2f),
    [1] = new SceneObject(Meshes.Triangle, new Vector3(3, 3, 3), scale: 2f),
    [2] = new SceneObject(Meshes.Quad, new Vector3(-3, -3, -3), scale: 2f)
  };

  // Per-object uniform buffer, indexed by object id. Slots for ids not in
  // _scene stay null. wgpu deals in Buffer* handles, so this is an array of
  // pointers, not of (opaque, meaningless-by-value) Buffer structs.
  private static Silk.NET.WebGPU.Buffer*[] _objIdBuffers = new Silk.NET.WebGPU.Buffer*[byte.MaxValue + 1];

  // Per-object bind group wrapping _objIdBuffers[id] — same id-slot scheme.
  // The draw loop attaches _objIdBindGroups[id] before drawing object id.
  private static BindGroup*[] _objIdBindGroups = new BindGroup*[byte.MaxValue + 1];

  #endregion


  #region Input State
  // Held-key state for camera movement (WASD + Q/E), flipped by the OnKey
  // callbacks; WindowOnUpdate integrates it each tick so motion is smooth
  // and frame-rate independent. Same wiring as CubeSdlSoftwareRenderer.
  private static bool _moveFwd, _moveBack, _moveLeft, _moveRight, _moveUp, _moveDown;

  // Previous mouse position; null until the first MouseMove so the initial
  // event doesn't turn into one giant synthetic rotation.
  private static Vector2? _lastMousePos;
  private static BindGroupLayout* _RenderingBindGroupLayout;
  private static PipelineLayout* _RenderingPipelineLayout;
  private static RenderPipeline* _RenderingPipeline;

  #endregion

  public static int Run()
  {
    _camera = new Camera(WindowWidth, WindowHeight, Camera.Style.Orbit);

    WindowOptions options = WindowOptions.Default;
    // GraphicsAPI.None: the windowing layer must NOT create an OpenGL
    // context — WebGPU talks to the OS surface directly.
    options.API = GraphicsAPI.None;
    options.Size = new Vector2D<int>(WindowWidth, WindowHeight);
    options.FramesPerSecond = 60;
    options.UpdatesPerSecond = 60;
    options.Position = new Vector2D<int>(0, 0);
    options.Title = "WebGPU 3D Rendering";
    options.IsVisible = true;
    options.ShouldSwapAutomatically = false;
    options.IsContextControlDisabled = true;

    _Window = Window.Create(options);
    wgpu = WebGPU.GetApi();

    _Window.Load += OnLoad;
    _Window.Closing += OnClose;
    _Window.Render += WindowOnRender;
    _Window.Update += WindowOnUpdate;
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

    SurfaceCapabilities surfaceCapabilities = default;
    wgpu.SurfaceGetCapabilities(_Surface, _Adapter, ref surfaceCapabilities);

    CreateBuffers();
    CreateDrawPipelineAndBindGroups(surfaceCapabilities.Formats[0]);
    CreateSwapchain(ref surfaceCapabilities);

    // Input subscription:
    IInputContext input = _Window.CreateInput();
    IKeyboard kb = input.Keyboards[0];
    IMouse mouse = input.Mice[0];

    kb.KeyDown += (_, key, _) => OnKey(key, down: true);
    kb.KeyUp += (_, key, _) => OnKey(key, down: false);
    mouse.MouseMove += OnMouseMove;   // compute your own delta from last position
    mouse.Scroll += (_, wheel) => _camera.OnZoom(wheel.Y * 0.1f);


    Console.WriteLine("All Ready!");
  }

  private static void CreateDrawPipelineAndBindGroups(TextureFormat surfaceFormat)
  {
    // A render pipeline is a frozen bundle of all the state that shapes a draw: 
    // the two shaders, the vertex buffer layout, primitive topology, cull mode, 
    // blend state, depth config, and the target format. 
    // The rule is: draws that share all of that share a pipeline; anything that 
    // varies per draw must flow through the things you can change between draws 
    // — bind groups, vertex-buffer offsets, and draw arguments.
    // since in this program once the state is changed the state is entirely frozen
    // through the draw call. We only need one Pipeline.

    #region construct Shader module
    string resourceName = "RenderingLearning.Shaders.3DShapeRenderer.wgsl";
    using Stream stream = typeof(CubeWebgpuRenderer).Assembly.GetManifestResourceStream(resourceName)
      ?? throw new InvalidOperationException(
          $"Embedded shader '{resourceName}' not found — create Shaders/3DShapeRenderer.wgsl (the csproj embeds Shaders/*.wgsl).");
    string wgslCode = new StreamReader(stream).ReadToEnd();

    // ShaderModule: the parsed+validated container for the WGSL source. It
    // exists so one module can serve both pipeline stages (the descriptor
    // below names vs_main and fs_main out of it) and so WGSL errors surface
    // here, at module creation — final compilation to native GPU code happens
    // at pipeline creation, once the full signature is known.
    ShaderModuleWGSLDescriptor wgslDescriptor = new ShaderModuleWGSLDescriptor
    {
      Code = (byte*)SilkMarshal.StringToPtr(wgslCode),
      Chain = new ChainedStruct
      {
        SType = SType.ShaderModuleWgslDescriptor
      }
    };

    ShaderModuleDescriptor shaderModuleDescriptor = new ShaderModuleDescriptor
    {
      NextInChain = (ChainedStruct*)(&wgslDescriptor),
    };

    _RenderingShader = wgpu.DeviceCreateShaderModule(_Device, &shaderModuleDescriptor);
    Console.WriteLine($"Created draw shader {(nuint)_RenderingShader:X}");
    #endregion

    // One slot: the per-object MVP uniform. Vertex-only visibility — the
    // fragment shader reads nothing external. MinBindingSize lets validation
    // reject any bind group wrapping a buffer smaller than one Matrix4x4.
    BindGroupLayoutEntry layoutEntry = new BindGroupLayoutEntry
    {
      Binding = 0,
      Visibility = ShaderStage.Vertex,
      Buffer = new BufferBindingLayout
      {
        Type = BufferBindingType.Uniform,
        MinBindingSize = (ulong)sizeof(Matrix4x4)
      }
    };

    // BindGroupLayout: a TYPE, not resources — it declares what a conforming
    // bind group must contain, naming no actual buffer. Created once; all
    // per-object bind groups below are instances of this one type. That's
    // what makes swapping bind groups between draws nearly free: conformance
    // is checked when a bind group is created, not at every draw.
    // EntryCount = 1 says "the Entries pointer points to an array of exactly 1 BindGroupLayoutEntry."
    //  It's the C-style way of passing an array across an FFI boundary: 
    // since a raw pointer carries no length information, the native WebGPU API takes 
    // a pointer + count pair, and the count is how wgpu knows 
    // how many entries to read starting at that address.
    // See WebGpuRendering, there I show how EntryCount is used for passing more than one binding
    BindGroupLayoutDescriptor bindGroupLayoutDescriptor = new BindGroupLayoutDescriptor
    {
      EntryCount = 1,
      Entries = &layoutEntry
    };
    _RenderingBindGroupLayout = wgpu.DeviceCreateBindGroupLayout(_Device, &bindGroupLayoutDescriptor);

    BindGroupLayout* bindGroupLayout = _RenderingBindGroupLayout;

    // PipelineLayout: the pipeline's full argument signature — an ordered
    // array of bind group layouts where index i is WGSL's @group(i). Ours has
    // exactly one argument: the MVP-uniform group type. It exists so the
    // shader compiler can bake @group/@binding references down to hardware
    // descriptor slots at pipeline creation, without ever seeing real buffers.
    PipelineLayoutDescriptor pipelineLayoutDescriptor = new PipelineLayoutDescriptor
    {
      BindGroupLayoutCount = 1,
      BindGroupLayouts = &bindGroupLayout
    };
    _RenderingPipelineLayout = wgpu.DeviceCreatePipelineLayout(_Device, &pipelineLayoutDescriptor);

    // BlendState: how a fragment's output combines with the pixel already in
    // the target. One/Zero/Add = "source replaces destination" — opaque
    // rendering. Blend config is frozen pipeline state, so real transparency
    // later means a second pipeline, not a toggle.
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
      // Must match the format CreateSwapchain configures the surface with,
      // or draws into the acquired surface texture fail validation.
      Format = surfaceFormat,
      Blend = &blendState,
      WriteMask = ColorWriteMask.All
    };

    // FragmentState: the per-pixel half of the program — which entry point
    // colors each covered pixel, and the render target(s) it writes to.
    // Our fs_main takes no bindings; it just returns a constant color.
    FragmentState fragmentState = new FragmentState
    {
      Module = _RenderingShader,
      TargetCount = 1,
      Targets = &colorTargetState,
      EntryPoint = (byte*)SilkMarshal.StringToPtr("fs_main")
    };

    // How the GPU should walk _buf: tightly packed 12-byte positions, one
    // per vertex, feeding the shader's @location(0) vec3<f32> input. This is
    // where the pipeline learns the mesh catalog's memory layout.
    VertexAttribute vertexAttribute = new VertexAttribute
    {
      Format = VertexFormat.Float32x3,
      Offset = 0,
      ShaderLocation = 0
    };

    VertexBufferLayout vertexBufferLayout = new VertexBufferLayout
    {
      ArrayStride = (ulong)sizeof(Vector3),
      StepMode = VertexStepMode.Vertex,
      AttributeCount = 1,
      Attributes = &vertexAttribute
    };

    RenderPipelineDescriptor renderPipelineDescriptor = new RenderPipelineDescriptor
    {
      Layout = _RenderingPipelineLayout,
      Vertex = new VertexState
      {
        Module = _RenderingShader,
        EntryPoint = (byte*)SilkMarshal.StringToPtr("vs_main"),
        BufferCount = 1,
        Buffers = &vertexBufferLayout
      },
      // Every 3 vertices assemble into one independent filled triangle —
      // matches Mesh's triangle-list contract. CullMode.None: draw both
      // faces; backface culling is a later optimization.
      Primitive = new PrimitiveState
      {
        Topology = PrimitiveTopology.TriangleList,
        StripIndexFormat = IndexFormat.Undefined,
        FrontFace = FrontFace.Ccw,
        CullMode = CullMode.None
      },
      // 1 sample per pixel = no MSAA.
      Multisample = new MultisampleState
      {
        Count = 1,
        Mask = ~0u,
        AlphaToCoverageEnabled = false
      },
      Fragment = &fragmentState,
      // No depth buffer yet: overlap is resolved by draw order, not distance.
      DepthStencil = null
    };

    // RenderPipeline: the one frozen, ahead-of-time-compiled draw
    // configuration — shaders + vertex layout + raster/blend state + the
    // argument signature. Everything that varies per draw (which MVP, which
    // mesh slice) was deliberately kept OUT of it; that's why this single
    // pipeline serves every object in the scene.
    _RenderingPipeline = wgpu.DeviceCreateRenderPipeline(_Device, &renderPipelineDescriptor);

    Console.WriteLine($"Created draw pipeline {(nuint)_RenderingPipeline:X}");

    // BindGroups: the INSTANCES of _RenderingBindGroupLayout — one per live
    // object, each plugging that object's uniform buffer into slot 0. The
    // draw loop passes exactly one of these per draw (SetBindGroup = argument
    // passing); objects spawned later get theirs via CreateBindGroupForObject.
    foreach (byte id in _scene.Keys)
    {
      _objIdBindGroups[id] = CreateBindGroupForObject(id);
    }
  }

  // Bind group = pre-baked "this buffer plugs into shader slot 0" object.
  // Requires _RenderingBindGroupLayout and _objIdBuffers[id] to exist first.
  private static BindGroup* CreateBindGroupForObject(byte id)
  {
    BindGroupEntry entry = new BindGroupEntry
    {
      Binding = 0,
      Buffer = _objIdBuffers[id],
      Offset = 0,
      Size = (ulong)sizeof(Matrix4x4)
    };
    BindGroupDescriptor descriptor = new BindGroupDescriptor
    {
      Layout = _RenderingBindGroupLayout,
      EntryCount = 1,
      Entries = &entry
    };
    return wgpu.DeviceCreateBindGroup(_Device, &descriptor);
  }

  private static void WindowOnUpdate(double delta)
  {
    // Update mutates CPU-side world state only — no wgpu calls belong here.
    // Clamp dt so a stall (first frame, window drag, breakpoint resume)
    // can't teleport the camera in one giant step.
    float dt = (float)Math.Min(delta, 0.05);
    _camera.OnMove(BuildMoveAxis(), dt);
  }

  #region Input to Camera movement wiring

  private static void OnKey(Key key, bool down)
  {
    switch (key)
    {
      case Key.W: _moveFwd = down; break;
      case Key.S: _moveBack = down; break;
      case Key.A: _moveLeft = down; break;
      case Key.D: _moveRight = down; break;
      case Key.E: _moveUp = down; break;
      case Key.Q: _moveDown = down; break;
    }
  }

  private static void OnMouseMove(IMouse mouse, Vector2 position)
  {
    // Silk.NET reports absolute position; the camera wants relative motion,
    // so diff against the previous event's position.
    if (_lastMousePos is Vector2 last)
    {
      Vector2 rel = position - last;
      _camera.OnRotate(rel.X * 0.01f, rel.Y * 0.01f);
    }
    _lastMousePos = position;
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

  #endregion
  private static void FramebufferResize(Vector2D<int> d)
  {
    SurfaceCapabilities surfaceCapabilities = default;
    wgpu.SurfaceGetCapabilities(_Surface, _Adapter, ref surfaceCapabilities);
    CreateSwapchain(ref surfaceCapabilities);
  }

  private static void WindowOnRender(double delta)
  {
    // Push state from CPU to VRAM. QueueWriteBuffer guarantees these copies
    // land before the command buffer submitted below executes, so writing
    // uniforms first, then encoding, is race-free within one frame.
    Matrix4x4 vp = _camera.Mvp;
    foreach ((byte id, SceneObject obj) in _scene)
      UpdateUniformBuffer(_objIdBuffers[id], obj.ModelMatrix * vp);

    // Acquire this frame's target from the swapchain. Not always a Success:
    // a resize between frames makes the configured size stale (Outdated), so
    // reconfigure and skip the frame rather than draw into a dead texture.
    SurfaceTexture surfaceTexture = default;
    wgpu.SurfaceGetCurrentTexture(_Surface, ref surfaceTexture);
    if (surfaceTexture.Status != SurfaceGetCurrentTextureStatus.Success)
    {
      if (surfaceTexture.Texture != null)
        wgpu.TextureRelease(surfaceTexture.Texture);
      SurfaceCapabilities surfaceCapabilities = default;
      wgpu.SurfaceGetCapabilities(_Surface, _Adapter, ref surfaceCapabilities);
      CreateSwapchain(ref surfaceCapabilities);
      return;
    }

    // Attachments bind views, not textures: the view fixes format/mip/layer
    // interpretation. For a swapchain texture the default (null-descriptor)
    // view is exactly the whole image.
    TextureView* view = wgpu.TextureCreateView(surfaceTexture.Texture, null);

    // CommandEncoder: records GPU work CPU-side; nothing executes until
    // QueueSubmit. One encoder per frame, thrown away after Finish.
    CommandEncoderDescriptor encoderDescriptor = new CommandEncoderDescriptor();
    CommandEncoder* encoder = wgpu.DeviceCreateCommandEncoder(_Device, &encoderDescriptor);

    // LoadOp.Clear replaces last frame's pixels with the clear color before
    // any draw; StoreOp.Store keeps the result for presentation.
    RenderPassColorAttachment colorAttachment = new RenderPassColorAttachment
    {
      View = view,
      ResolveTarget = null,
      LoadOp = LoadOp.Clear,
      StoreOp = StoreOp.Store,
      ClearValue = new Color(0, 0, 0, 0)
    };

    RenderPassDescriptor renderPassDescriptor = new RenderPassDescriptor
    {
      ColorAttachmentCount = 1,
      ColorAttachments = &colorAttachment
    };

    RenderPassEncoder* pass = wgpu.CommandEncoderBeginRenderPass(encoder, &renderPassDescriptor);

    // Frame-constant state once: the one pipeline, the one packed vertex
    // buffer (slot 0 = VertexState.Buffers[0]).
    wgpu.RenderPassEncoderSetPipeline(pass, _RenderingPipeline);
    wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, _buf, 0, (ulong)Meshes.MeshBufferSizeBytes);

    // Per-object state: bind group = "which MVP", firstVertex = "which mesh
    // slice of _buf". The shader never learns mesh identity — selection is
    // entirely in the draw arguments.
    foreach ((byte id, SceneObject obj) in _scene)
    {
      wgpu.RenderPassEncoderSetBindGroup(pass, 0, _objIdBindGroups[id], 0, null);
      wgpu.RenderPassEncoderDraw(
        pass,
        (uint)obj.Mesh.Vertices.Length,
        1,
        (uint)Meshes.FirstVertexOf(obj.Mesh),
        0);
    }

    wgpu.RenderPassEncoderEnd(pass);

    CommandBufferDescriptor commandBufferDescriptor = new CommandBufferDescriptor();
    CommandBuffer* commandBuffer = wgpu.CommandEncoderFinish(encoder, &commandBufferDescriptor);
    wgpu.QueueSubmit(_Queue, 1, &commandBuffer);

    wgpu.SurfacePresent(_Surface);

    // Per-frame transients; the frame is submitted, the handles are dead.
    wgpu.CommandBufferRelease(commandBuffer);
    wgpu.RenderPassEncoderRelease(pass);
    wgpu.CommandEncoderRelease(encoder);
    wgpu.TextureViewRelease(view);
    wgpu.TextureRelease(surfaceTexture.Texture);
  }

  private static void OnClose()
  {
    // Dispose unmanaged
    foreach (BindGroup* bindGroup in _objIdBindGroups)
    {
      if (bindGroup != null)
        wgpu.BindGroupRelease(bindGroup);
    }
    wgpu.BindGroupLayoutRelease(_RenderingBindGroupLayout);
    wgpu.PipelineLayoutRelease(_RenderingPipelineLayout);
    wgpu.RenderPipelineRelease(_RenderingPipeline);
    wgpu.ShaderModuleRelease(_RenderingShader);
    foreach (Silk.NET.WebGPU.Buffer* uniform in _objIdBuffers)
    {
      if (uniform != null)
        wgpu.BufferRelease(uniform);
    }
    wgpu.BufferRelease(_buf);
    wgpu.QueueRelease(_Queue);
    wgpu.DeviceRelease(_Device);
    wgpu.AdapterRelease(_Adapter);
    wgpu.SurfaceRelease(_Surface);
    wgpu.InstanceRelease(_Instance);

    wgpu.Dispose();
  }

  private static void CreateBuffers()
  {
    // Create a vertex buffer and upload the meshes for triangle, quad, cube in that order
    int byteSize = Meshes.MeshBufferSizeBytes;
    BufferDescriptor desc = new BufferDescriptor
    {
      Usage = BufferUsage.Vertex,
      Size = (ulong)byteSize,
      MappedAtCreation = true
    };

    _buf = wgpu.DeviceCreateBuffer(_Device, &desc);
    void* dst = wgpu.BufferGetMappedRange(_buf, 0, (nuint)byteSize);
    // memcpy vertices into dst (fixed + Buffer.MemoryCopy, like your Present())
    // This is CPU to VRAM transfer in action!
    // We should upload the whole mesh catalog here, and based on whether or not we spawn stuff we will do actual render
    int byteOffset = 0;
    for (int i = 0; i < Meshes.All.Length; i++)
    {
      Mesh mesh = Meshes.All[i];
      Vector3[] vertices = mesh.Vertices;
      int meshBytes = vertices.Length * sizeof(Vector3);
      fixed (Vector3* src = vertices)
      {
        System.Buffer.MemoryCopy(src, (byte*)dst + byteOffset, byteSize - byteOffset, meshBytes);
      }
      byteOffset += meshBytes;
    }

    wgpu.BufferUnmap(_buf);

    // One uniform buffer per object already in the scene. Objects spawned
    // later get theirs at spawn time via CreateUniformBuffer().
    foreach (byte id in _scene.Keys)
    {
      _objIdBuffers[id] = CreateUniformBuffer();
    }
  }

  // One 64-byte uniform buffer (a single Matrix4x4 MVP) for one object.
  // Uniform = bindable in the shader's uniform slot; CopyDst = required for
  // UpdateUniformBuffer's QueueWriteBuffer to target it. Created once per
  // object lifetime — movement never recreates it, only rewrites contents.
  private static Silk.NET.WebGPU.Buffer* CreateUniformBuffer()
  {
    BufferDescriptor desc = new BufferDescriptor
    {
      Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
      Size = (ulong)sizeof(Matrix4x4)
    };
    return wgpu.DeviceCreateBuffer(_Device, &desc);
  }

  // Overwrite a uniform buffer's contents in place (the mailbox model: the
  // buffer's identity — and any bind group pointing at it — never changes).
  // QueueWriteBuffer copies the matrix into driver staging before returning,
  // so handing it the address of this stack parameter is safe; the write is
  // guaranteed to land before the next submitted command buffer executes.
  private static void UpdateUniformBuffer(Silk.NET.WebGPU.Buffer* buffer, Matrix4x4 mvp)
  {
    wgpu.QueueWriteBuffer(_Queue, buffer, 0, &mvp, (nuint)sizeof(Matrix4x4));
  }

  private static void CreateSwapchain(ref SurfaceCapabilities surfaceCapabilities)
  {
    var surfaceConfiguration = new SurfaceConfiguration
    {
      Usage = TextureUsage.RenderAttachment,
      Format = surfaceCapabilities.Formats[0],
      PresentMode = PresentMode.Fifo,
      Device = _Device,
      Width = (uint)_Window.FramebufferSize.X,
      Height = (uint)_Window.FramebufferSize.Y
    };
    wgpu.SurfaceConfigure(_Surface, ref surfaceConfiguration);
  }

  private static void DeviceLost(DeviceLostReason arg0, byte* arg1, void* arg2)
    => Console.WriteLine($"Device lost! Reason: {arg0} Message: {SilkMarshal.PtrToString((nint)arg1)}");

  private static void UncapturedError(ErrorType arg0, byte* arg1, void* arg2)
    => Console.WriteLine($"{arg0}: {SilkMarshal.PtrToString((nint)arg1)}");
}