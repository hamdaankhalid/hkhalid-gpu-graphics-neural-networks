// The Game of Life simulation, as a compute shader. Compare with
// SdlSoftwareRenderer.Step(): the two nested x/y loops are GONE — instead
// this function body runs once per cell, on thousands of cells
// concurrently, each invocation identified by its global_invocation_id.
// Only the neighbor-counting inner loops survive, because those are
// per-cell work.
//
// GRID_W / GRID_H / @workgroup_size are hardcoded literals: keep them in
// sync with WebGpuRendering.GridW / GridH / WorkgroupSize. The C# side
// validates them when loading this file, so drift fails loudly at startup.

@group(0) @binding(0) var<storage, read> src : array<u32>;
@group(0) @binding(1) var<storage, read_write> dst : array<u32>;

const GRID_W : u32 = 240u;
const GRID_H : u32 = 135u;

@compute @workgroup_size(8, 8)
fn step(@builtin(global_invocation_id) id : vec3<u32>) {
  // The dispatch is rounded up to whole workgroups, so threads can land
  // past the grid edge; they must do nothing.
  if (id.x >= GRID_W || id.y >= GRID_H) {
    return;
  }

  // Toroidal neighborhood, exactly as in the CPU version.
  var neighbors : u32 = 0u;
  for (var dy : i32 = -1; dy <= 1; dy = dy + 1) {
    for (var dx : i32 = -1; dx <= 1; dx = dx + 1) {
      if (dx == 0 && dy == 0) {
        continue;
      }
      // id.x and id.y carry the global invocation and from that we know the cell this was kicked off for
      // we do an addition to grid_w and grid_h because the % doesn't deal correctly with negative numbers
      // since dx and dy go from -1, it is possible at a boudnary that we have an out of bound value we are
      // computing that would yield to a negative modulo.
      // we could also handle negative value with branching but this keeps the code branchless..
      let nx = u32((i32(id.x) + dx + i32(GRID_W)) % i32(GRID_W));
      let ny = u32((i32(id.y) + dy + i32(GRID_H)) % i32(GRID_H));
      neighbors = neighbors + src[ny * GRID_W + nx];
    }
  }

  let alive = src[id.y * GRID_W + id.x];
  dst[id.y * GRID_W + id.x] = select(0u, 1u, neighbors == 3u || (alive == 1u && neighbors == 2u));
}
