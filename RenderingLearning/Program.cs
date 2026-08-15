// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using RenderingLearning.ShapeRendering;
using Silk.NET.Core;

namespace RenderingLearning;

public static class Program
{
  // ReSharper disable once InconsistentNaming

  public static void Main(string[] args)
  {
    string arg = args.FirstOrDefault() ?? "cube_cpu";

    switch (arg)
    {
      case "cpu":
        SdlSoftwareRenderer.Run(); // game of life
        break;
      case "gpu":
        WebGpuRendering.Run(); // game of life
        break;
      case "cube_cpu":
        CubeSdlSoftwareRenderer.Run(); // cube rendering 
        break;
    }
  }
}
