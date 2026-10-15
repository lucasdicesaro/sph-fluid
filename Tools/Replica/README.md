# Replica

A standalone C# console copy of the 2D simulation (`Assets/Scripts/Circle.cs`, same maths as `FluidSim2D.compute`)
for measuring behaviour without Unity: kinetic energy, centre of mass, floor and surface density, particle spacing,
and the shape of airborne water. It found the liquid parameter regime, the mouse pickup technique, and the viscosity
and near-pressure values used in the scenes. Single-threaded and deterministic; about 10 s of simulated time per
10 s of wall clock at 4000 particles.

```
cd Tools/Replica
dotnet run -c Release -- target=55 k=500 sub=5 visc=0.03 neark=5 sec=10
```

Arguments are `key=value`, all optional:

| Key | Meaning | Default |
|---|---|---|
| `n`, `radius`, `gap` | particles, particle radius, spawn gap (block spawn, 10% jitter) | 4032, 0.05, 0.02 |
| `h`, `target`, `k`, `neark`, `visc` | smoothing radius, target density, pressure, near pressure, viscosity | 0.35, 5.2, 27.44, 0, 0 |
| `g`, `cdamp`, `damping` | gravity, collision damping, velocity damping | 10, 0.95, 0.1 |
| `bw`, `bh` | bounds | 17.78, 10 |
| `fps`, `sub`, `sec` | frame rate, substeps, simulated seconds | 60, 3, 12 |
| `shared` | `lague` = (Pi+Pj)/(2ρj), `sym` = (Pi+Pj)/(ρi+ρj) | lague |
| `jitter` | 1/0 spawn jitter | 1 |
| `predict` | prediction lookahead in seconds | 1/120 |
| `minp` | floor for negative pressure (experiment; not in the Unity code) | none |
| `istr`, `irad`, `imode` | mouse pull strength and radius from `istart` s; `imode=lague` cancels gravity in the ring | 0, 2.5, ours |
| `istart`, `iabove`, `ihold`, `idrag`, `itop`, `irelease` | pull starts at `istart`, cursor `iabove` units above the surface, held `ihold` s, then dragged up at `idrag` u/s to `itop`, released at `irelease` | 4, 1, 1.5, 0, 3.5, never |
| `airfrom`, `airto` | window for the airborne-shape readout | 0.5, 3 |

Output every 0.5 s: kinetic energy per particle, centre-of-mass height, max density, mean density of the lowest 5% and
highest 5% of particles. At the end: centre-of-mass direction reversals, mean nearest-neighbour distance, close pairs,
interior density spread. With a pull: particles inside the ring and their height. Within the air window: airborne
count and anisotropy (about 0.2 to 0.3 is round droplets, 0.45 and up is chains).

Keep it in sync with `Circle.cs` when the simulation maths changes.
