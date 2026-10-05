# Fluid 2D

A 2D SPH (Smoothed Particle Hydrodynamics) fluid simulation built in Unity, based on Sebastian Lague's fluid simulation video tutorials.

## What it does

- Simulates fluid behavior using SPH — each particle interacts with its neighbors via density and pressure forces
- Renders particles with GPU instancing (`DrawMeshInstancedProcedural`) for performance
- Visualizes a pressure field in the background (blue = negative/sparse, red = positive/dense, grey = equilibrium)
- Density gradient vector field for debugging

## Requirements

- Unity 2022.3 or later (URP / 2D renderer)

## How to open

1. Clone the repo
2. Open **Unity Hub** → **Add project from disk** → select the repo root folder
3. In the Project panel, double-click `Assets/Scenes/Fluid2D.unity` ← required on first open; Unity remembers it after that
4. Press Play

No manual setup needed — the scene file already has all parameters, materials, and shader assignments saved.

## CPU and GPU simulations

The scene has two simulation objects. Only one should be enabled at a time; switch in the Hierarchy:

| GameObject | Component | Runs on | Notes |
|---|---|---|---|
| `GpuCircle` (enabled by default) | `GpuCircle` | GPU (`FluidSim2D.compute`) | Same physics as compute shader kernels; neighbour search via spatial hash + bitonic merge sort, as in Lague's video. Scales to tens of thousands of particles |
| `CpuCircle` (disabled by default) | `Circle` | CPU (`Parallel.For`, dense grid) | Reference version. Has the debug readouts: density at cursor, vector field, grid validation |

Both share the same controls, mouse interaction and particle shader; each has its own parameter set. `Circle` uses Lague's radius-0.35 scene values for ~4000 particles; `GpuCircle` uses his dense radius-0.2 scene (target density 234, pressure 225, near pressure 18) for ~20000. The fluid's area is roughly particles / (targetDensity − 6/(π·radius²)), so more particles at the same settings overfill the box. The pressure-field background is CPU only.

## 3D scene

`Assets/Scenes/Fluid3D.unity` runs the 3D version from the end of Lague's video: `GpuSphere` + `FluidSim3D.compute`, the GPU pipeline with 3D kernels and a 27-cell spatial hash inside an axis-aligned box, particles drawn as camera-facing billboards shaded as spheres. Particles spawn as a block at `spawnDensity` per unit volume (default 286 in a 7×7×14 block ≈ 195k particles in a 24×10×15 box; Lague's scene uses 600 ≈ 410k, raise `spawnDensity` if your GPU keeps up, then press R to respawn). Defaults are Lague's 3D scene values (smoothing radius 0.2, target density 630, pressure 288, near pressure 2.16). Same keyboard controls. In the Game view, left-drag orbits the camera and the scroll wheel zooms. The `GpuSphere` object's transform is the box: select it in the Hierarchy during Play and move or rotate it with the Scene view gizmos to tilt the fluid, as Lague does (turn off the Scene view's `2D` toggle to navigate in 3D). No mouse pull/push in 3D. Surface rendering (marching cubes, ray marching) is from his second video and not part of this project.

## Controls (Play mode)

| Key | Action |
|---|---|
| `R` | Restart the simulation (starts paused) |
| `Space` | Run / pause — the simulation starts paused |
| `→` | Advance one frame while paused |
| Left click / drag | Pull nearby particles toward the cursor. To pick up a ball of water, click inside the pool and drag out slowly |
| Right click / drag (two-finger click on a trackpad) | Push nearby particles away |

## Inspector parameters (Circle component)

| Parameter | Description |
|---|---|
| `numParticles` | Number of fluid particles |
| `particleRadius` | Visual and collision radius of each particle |
| `spawnGap` | Gap between neighbouring particles in the initial block |
| `smoothingRadius` | SPH kernel radius — how far each particle "feels" neighbors |
| `particleMass` | Mass per particle |
| `targetDensity` | Rest density the fluid tries to reach. For a liquid it must sit near the surface density, which at `smoothingRadius` 0.35 is ~55; a lone particle already reads 6/(π·r²) ≈ 15.6 from itself |
| `pressureMultiplier` | Stiffness — pressure = multiplier × (density − target), higher = stronger resistance to compression |
| `nearPressureMultiplier` | Extra short-range repulsion from a sharper 'near density' with no target; stops particles pairing or clumping |
| `damping` | Linear velocity damping per second |
| `viscosityStrength` | Pulls each particle's velocity toward its neighbours' (damps relative motion only) |
| `collisionDamping` | Energy retained after a wall bounce (0 = absorb all, 1 = elastic) |
| `subSteps` | Simulation steps per frame — more = more stable at high speeds |
| `gravity` | Downward gravity (default 12; set to 0 for top-down view) |
| `interactionRadius` | Radius around the cursor affected by mouse pull/push |
| `interactionStrength` | Strength of the mouse pull/push |
| `speedGradient` | Particle colour by speed, slow on the left, fast on the right (default heat map: blue → cyan → yellow → red) |
| `maxSpeed` | Speed that maps to the right end of the gradient |

## References

- [Sebastian Lague — Coding Adventure: Simulating Fluids](https://www.youtube.com/watch?v=rSKMYc1CQHE)
