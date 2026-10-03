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
3. In the Project panel, double-click `Assets/Scenes/SampleScene.unity` ← required on first open; Unity remembers it after that
4. Press Play

No manual setup needed — the scene file already has all parameters, materials, and shader assignments saved.

## Controls (Play mode)

| Key | Action |
|---|---|
| `R` | Restart the simulation (starts paused) |
| `Space` | Run / pause — the simulation starts paused |
| `→` | Advance one frame while paused |
| Left click / drag | Pull nearby particles toward the cursor |
| Right click / drag (two-finger click on a trackpad) | Push nearby particles away |

## Inspector parameters (Circle component)

| Parameter | Description |
|---|---|
| `numParticles` | Number of fluid particles |
| `particleRadius` | Visual and collision radius of each particle |
| `spawnGap` | Gap between neighbouring particles in the initial block |
| `smoothingRadius` | SPH kernel radius — how far each particle "feels" neighbors |
| `particleMass` | Mass per particle |
| `targetDensity` | Rest density the fluid tries to reach |
| `pressureMultiplier` | Stiffness — pressure = multiplier × (density − target), higher = stronger resistance to compression |
| `damping` | Linear velocity damping per second |
| `collisionDamping` | Energy retained after a wall bounce (0 = absorb all, 1 = elastic) |
| `subSteps` | Simulation steps per frame — more = more stable at high speeds |
| `gravity` | Downward gravity (set to 0 for top-down view) |
| `interactionRadius` | Radius around the cursor affected by mouse pull/push |
| `interactionStrength` | Strength of the mouse pull/push |
| `speedGradient` | Particle colour by speed, slow on the left, fast on the right (default heat map: blue → cyan → yellow → red) |
| `maxSpeed` | Speed that maps to the right end of the gradient |

## References

- [Sebastian Lague — Coding Adventure: Simulating Fluids](https://www.youtube.com/watch?v=rSKMYc1CQHE)
