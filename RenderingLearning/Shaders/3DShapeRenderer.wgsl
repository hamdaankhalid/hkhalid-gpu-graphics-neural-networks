// buffer space for uniform

@group(0) @binding(0) var<uniform> mvp : mat4x4<f32>;

struct VsOut {
  @builtin(position) clipPos : vec4<f32>,
};

@vertex
fn vs_main(@location(0) pos : vec3<f32>) -> VsOut {
  var out : VsOut;
  out.clipPos = mvp * vec4<f32>(pos, 1.0);
  return out;
}

@fragment
fn fs_main() -> @location(0) vec4<f32> {
  return vec4<f32>(1.0, 0.5, 0.2, 1.0);  // any RGBA you like
}
