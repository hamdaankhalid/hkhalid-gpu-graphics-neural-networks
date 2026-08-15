// The presentation, replacing SdlSoftwareRenderer.PaintGridToFrameBuffer.
// The vertex shader emits one oversized triangle whose corners are (-1,-1),
// (3,-1), (-1,3) in clip space — the GPU clips it to the window, so 3
// vertices cover every pixel (cheaper and simpler than a two-triangle
// quad). The fragment shader then runs once per covered pixel, in parallel:
// map the pixel's uv to a cell, read the cell, pick a color. The
// px/CellSize scaling from the CPU version falls out of the uv -> grid
// mapping.
//
// GRID_W / GRID_H are hardcoded literals: keep them in sync with
// WebGpuRendering.GridW / GridH. The C# side validates them when loading
// this file, so drift fails loudly at startup.

@group(0) @binding(0) var<storage, read> cells : array<u32>;

const GRID_W : u32 = 240u;
const GRID_H : u32 = 135u;

struct VsOut {
  @builtin(position) pos : vec4<f32>,
  @location(0) uv : vec2<f32>,
}

@vertex
fn vs_main(@builtin(vertex_index) vi : u32) -> VsOut {
  // vi = 0,1,2  ->  x = -1,3,-1  and  y = -1,-1,3.
  // this is semantically same as definidng an array and indexing but cheaper for GPU to execute
  // harder to read this fuckery though
  let x = f32(vi & 1u) * 4.0 - 1.0;
  let y = f32(vi & 2u) * 2.0 - 1.0;
  var out : VsOut;
  out.pos = vec4<f32>(x, y, 0.0, 1.0);
  // Clip space is y-up; flip so uv (0,0) is the window's top-left, matching
  // the row-0-at-top layout the seeder writes.
  out.uv = vec2<f32>((x + 1.0) * 0.5, (1.0 - y) * 0.5);
  return out;
}

@fragment
fn fs_main(in : VsOut) -> @location(0) vec4<f32> {
  let cx = min(u32(in.uv.x * f32(GRID_W)), GRID_W - 1u);
  let cy = min(u32(in.uv.y * f32(GRID_H)), GRID_H - 1u);
  if (cells[cy * GRID_W + cx] == 1u) {
    return vec4<f32>(0.0, 1.0, 0.4, 1.0); // alive: green
  }
  return vec4<f32>(0.063, 0.063, 0.063, 1.0); // dead: near-black
}
