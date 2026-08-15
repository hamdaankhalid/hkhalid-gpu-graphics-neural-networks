# GPU Compute

## Workgroup Sizing and Dispatch Counts

Two numbers, decided in two places, that multiply together to cover the problem:

1. **`@workgroup_size(x, y, z)`** in the shader — how many threads (invocations)
   are in ONE workgroup. Baked into the pipeline at shader compile time.
2. **`ComputePassEncoderDispatchWorkgroups(countX, countY, countZ)`** on the CPU —
   how many workgroups to launch. Chosen fresh at every dispatch.

Total threads launched = `(x * countX) * (y * countY) * (z * countZ)`.
The shader picks the *shape of the team*; the dispatch picks *how many teams*.

### Deciding @workgroup_size

The hardware constraints (WebGPU guaranteed minimums — actual limits are queryable
and usually higher):

- Max invocations per workgroup: **256** (x * y * z <= 256)
- Max per dimension: 256 for x/y, 64 for z

The rules of thumb, in priority order:

**1. Make the total a multiple of the hardware SIMD width (32, or 64 on AMD).**
Threads execute in lockstep bundles of 32 ("warps"/"subgroups"). A workgroup of
25 threads still occupies a whole 32-lane warp — 7 lanes permanently idle, ~22%
of the machine thrown away. 32, 64, 128, 256 are all safe totals.

**2. Match the workgroup's shape to the data's shape.**
- 1D data (flat array): `@workgroup_size(64)` or `(256)`, dispatch `(ceil(n/64), 1, 1)`.
- 2D data (image, grid): a square tile like `(8, 8)` = 64 or `(16, 16)` = 256.
  Square tiles matter when neighbors are read (blur, Game of Life): a tile's
  perimeter-to-area ratio determines how much redundant edge data it touches.
- 3D data (volume): `(4, 4, 4)` = 64, etc.

**3. Keep threadIds along x contiguous in memory.**
The x dimension varies fastest across a warp, so adjacent x-threads should read
adjacent addresses — then the hardware merges 32 loads into a few wide
transactions ("coalescing"). `src[y * WIDTH + x]` (row-major, x innermost) does
this; swapping x/y in the indexing can be 10x slower with zero logic change.

**4. Default to 64; go bigger only for a reason.**
64 is a multiple of both 32 and 64, small enough that the per-SM register/shared
-memory budget rarely limits how many workgroups run concurrently ("occupancy").
Reasons to go to 128/256: heavy use of `var<workgroup>` shared memory where a
bigger tile amortizes more halo loads, or reductions that want more threads
cooperating per barrier. When in doubt: benchmark — the "right" size is
hardware-dependent and this is one of the few knobs people genuinely tune by
measurement.

This project: `@workgroup_size(8, 8)` = 64 threads — multiple of 32, square tile
for a 2D grid with neighbor reads, x-contiguous indexing. Textbook.

### Deciding countX / countY / countZ

There's no freedom here — it's derived: **enough workgroups to cover the data,
rounded UP.**

```
count = ceil(problemSize / workgroupSize)
      = (problemSize + workgroupSize - 1) / workgroupSize   // integer trick
```

From WebGpuRendering.cs:

```csharp
wgpu.ComputePassEncoderDispatchWorkgroups(
    computePass,
    (GridW + WorkgroupSize - 1) / WorkgroupSize,   // ceil(240/8) = 30
    (GridH + WorkgroupSize - 1) / WorkgroupSize,   // ceil(135/8) = 17
    1);                                            // 2D problem: z count is 1
```

30 * 17 = 510 workgroups * 64 threads = 32,640 threads for 240 * 135 = 32,400
cells.

**Rounding up creates overhang threads** whenever the grid isn't an exact
multiple of the workgroup size (here: 17 * 8 = 136 rows > 135). Those threads
get valid ids past the edge and MUST self-mask, always as the first thing in
the shader:

```wgsl
if (id.x >= GRID_W || id.y >= GRID_H) { return; }
```

This pair — ceil-divide on the CPU, bounds-check in the shader — is the
standard idiom; both halves are required, they're two ends of one contract.
(Masked-off threads still occupy warp lanes, but only the edge workgroups
diverge, which is negligible.)

**Why not round down and size the grid to fit?** Sometimes you can (pick
grid dims that are multiples of 8). But the dispatch side must handle arbitrary
sizes the moment anything is user-configurable, so the idiom is worth having
everywhere by default.

**countZ** is 1 unless the problem is genuinely 3D (volumes) — or used as a
"batch" axis, e.g. processing N images in one dispatch with `id.z` selecting
the image.

### The id builtins, and which one to use

- `global_invocation_id` = `workgroup_id * workgroup_size + local_invocation_id`
  — "which cell of the whole problem am I?" Use this 95% of the time.
- `local_invocation_id` — position *within* my workgroup; needed when indexing
  `var<workgroup>` shared memory tiles.
- `workgroup_id` — which workgroup; needed for computing a tile's base offset.

### Keeping shader and CPU in sync

`@workgroup_size` is a literal in WGSL and the ceil-divide on the CPU must use
the same number — drift means silently uncovered cells (too few workgroups) or
wasted work. This project validates at shader-load time that the WGSL literal
matches the C# `WorkgroupSize` constant, so drift fails loudly at startup
instead of rendering a subtly wrong simulation.
