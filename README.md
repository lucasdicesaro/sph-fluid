# SPH Fluid

Smoothed Particle Hydrodynamics fluid simulation in Unity, following Sebastian Lague's
[Coding Adventure: Simulating Fluids](https://www.youtube.com/watch?v=rSKMYc1CQHE) from start to finish:
2D on the CPU, 2D on the GPU, and 3D on the GPU.

## What's inside

| Scene | Object | Component | Runs on | Particles |
|---|---|---|---|---|
| `Assets/Scenes/Fluid2D.unity` | `CpuCircle` (disabled by default) | `Circle` | CPU, `Parallel.For` over a dense grid | ~4 000 |
| `Assets/Scenes/Fluid2D.unity` | `GpuCircle` (enabled by default) | `GpuCircle` + `FluidSim2D.compute` | GPU compute shaders | ~20 000 |
| `Assets/Scenes/Fluid3D.unity` | `GpuSphere` | `GpuSphere` + `FluidSim3D.compute` | GPU compute shaders | ~200 000 |

All three run the same physics from the video: predicted positions, density and near density, pressure and near pressure,
viscosity, box collisions, and a neighbour search over a spatial grid. The GPU versions use Lague's spatial hash with a
GPU count sort (from his [Fluid-Sim repository](https://github.com/SebLague/Fluid-Sim)) and keep all particle data on the GPU;
the particle shaders read the position and colour buffers directly. The CPU version is the reference and has the debug
readouts (density under the cursor, density-gradient vector field, grid validation). Particles are coloured by speed.

Surface rendering (marching cubes, ray marching, screen-space fluids) is from Lague's second video and is not part of this project.

## Requirements

- Unity 6 (developed on 6000.3), URP with the 2D Renderer. The custom particle shaders don't need lighting, so no renderer change is needed for the 3D scene.
- A GPU with compute shader support for the GPU scenes.

## How to open

1. Clone the repo
2. **Unity Hub** → **Add project from disk** → select the repo root folder
3. In the Project panel, double-click `Assets/Scenes/Fluid2D.unity` or `Assets/Scenes/Fluid3D.unity` (double-click replaces the open scene; dragging a scene into the Hierarchy opens it additively, which you don't want)
4. Press Play, then Space

No manual setup: the scene files carry all parameters, materials and shader references.

## Controls (Play mode)

| Input | Action |
|---|---|
| `Space` | Run / pause. The simulation starts paused |
| `→` | Advance one frame while paused |
| `R` | Respawn the particles (starts paused). On the GPU objects this also applies inspector changes such as the particle count |
| Left click / drag (2D) | Pull nearby particles toward the cursor. To pick up a ball of water, click inside the pool and drag out slowly |
| Right click / drag (2D; two-finger click on a trackpad) | Push nearby particles away |
| Left drag (3D Game view) | Orbit the camera around the box |
| Scroll wheel (3D Game view) | Zoom |

**Moving the box in 3D:** the `GpuSphere` object's transform is the box. During Play, select it in the Hierarchy and move or rotate
it with the Scene view gizmos; the fluid collides with the moved box, as in the video. Turn off the Scene view's `2D` toggle to
navigate in 3D there.

## 2D: CPU vs GPU

Only one of `CpuCircle` and `GpuCircle` should be enabled; switch in the Hierarchy. Each has its own parameter set:
`Circle` uses Lague's radius-0.35 scene values, `GpuCircle` his dense radius-0.2 scene. The pressure-field background exists on the
CPU object only (its three colours are black by default).

The fluid's area is roughly `particles / (targetDensity − 6/(π·smoothingRadius²))`: more particles at the same settings overfill the box,
so raise the target density (or use the dense parameter set) when raising the count.

### Parameters (`Circle` and `GpuCircle`)

| Parameter | Description | `Circle` | `GpuCircle` |
|---|---|---|---|
| `numParticles` | Number of particles, spawned as a square block | 4032 | 20000 |
| `particleRadius` | Drawn radius and wall margin | 0.05 | 0.02 |
| `spawnGap` | Gap between neighbouring particles in the spawn block | 0.02 | 0.015 |
| `smoothingRadius` | SPH kernel radius, how far a particle "feels" neighbours | 0.35 | 0.2 |
| `targetDensity` | Rest density. For a liquid it must sit near the surface density; a lone particle already reads 6/(π·r²) from itself (15.6 at r 0.35) | 55 | 234 |
| `pressureMultiplier` | Stiffness: pressure = multiplier × (density − target) | 500 | 225 |
| `nearPressureMultiplier` | Extra short-range repulsion from a sharper near density with no target; stops particles pairing | 5 | 18 |
| `viscosityStrength` | Pulls each particle's velocity toward its neighbours' (damps relative motion only) | 0.03 | 0.03 |
| `damping` (`Circle` only) | Linear velocity damping per second | 0 | – |
| `collisionDamping` | Velocity kept after a wall bounce | 0.95 | 0.5 |
| `gravity` | Downward gravity; 0 for a top-down view | 12 | 13 |
| `subSteps` | Simulation steps per frame | 3 | 3 |
| `interactionRadius` / `interactionStrength` | Mouse pull/push ring and strength (gravity is cancelled inside the ring) | 2 / 90 | 2.5 / 75 |
| `speedGradient` / `maxSpeed` | Colour by speed; `maxSpeed` maps to the right end of the gradient | heat map / 5 | heat map / 5 |
| `showDensityMs` (`Circle` only) | Debug overlay: sim time, bounds, density under the cursor | off | – |

Bounds are the camera view: height is the orthographic size × 2, width follows the Game view aspect.

## 3D (`GpuSphere`)

| Parameter | Description | Default |
|---|---|---|
| `spawnDensity` | Particles per unit volume; particles per axis = round(size × ∛density) | 285 (≈195k particles). Lague's scene uses 600 (≈410k); raise it if your GPU keeps up |
| `spawnSize` / `spawnCentre` | Spawn block size and centre (Lague's two 7-unit cubes side by side) | 7×7×14 at (−8.3, −1.3, 0) |
| `jitterStrength` | Random offset per spawned particle | 0.035 |
| `boundsSize` | Box size, centred on the `GpuSphere` transform | 24×10×15 |
| `particleRadius` | Drawn radius | 0.04 |
| `smoothingRadius` / `targetDensity` / `pressureMultiplier` / `nearPressureMultiplier` / `viscosityStrength` | As in 2D, with Lague's 3D values | 0.2 / 630 / 288 / 2.16 / 0 |
| `collisionDamping` / `gravity` / `subSteps` | | 0.95 / 10 / 3 |
| `speedGradient` / `maxSpeed` | | heat map / 8 |

When the GPU can't finish a frame in 1/60 s the simulation advances less time per frame rather than taking coarser steps, so it stays
stable and runs in slow motion.

## Files

| Path | What |
|---|---|
| `Assets/Scripts/Circle.cs` | CPU 2D simulation and reference; shared helpers (spawn block, lines, mouse) |
| `Assets/Scripts/GpuCircle.cs`, `Assets/Shaders/FluidSim2D.compute` | GPU 2D simulation |
| `Assets/Scripts/GpuSphere.cs`, `Assets/Shaders/FluidSim3D.compute` | GPU 3D simulation |
| `Assets/Scripts/OrbitCamera.cs` | Game-view camera for the 3D scene |
| `Assets/Shaders/Circle.shader`, `Assets/Shaders/Sphere.shader` | Instanced particle shaders (2D disc, 3D sphere-shaded billboard) |
| `Assets/Shaders/PressureField.shader` | CPU-only pressure background |

## References

- [Sebastian Lague — Coding Adventure: Simulating Fluids](https://www.youtube.com/watch?v=rSKMYc1CQHE)
- [SebLague/Fluid-Sim](https://github.com/SebLague/Fluid-Sim), the source of the parameter sets and the count sort
