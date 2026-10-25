# Ustye Map Preview Benchmark Results

Measured with unconstrained frame rate (`--benchmark`), VSync set to Disabled, and FPS limit set to 0.
Monotonic timestamps (`Stopwatch.GetTimestamp`) record wall clock frame intervals.
Warmup period of 3.0 real seconds per angle was applied before recording 600 consecutive frame intervals.

## Hardware & Environment

- **OS / Platform**: macOS (15.7.4) arm64
- **Engine**: Godot 4.7.2-stable (official)
- **Backend / API**: Metal 3.2
- **GPU**: Apple Apple M1 (Apple7)
- **Window Size**: 1280 x 800
- **Render Viewport**: 1440 x 900
- **VSync**: Disabled
- **Max FPS**: Unlimited (0)
- **MSAA 3D**: Msaa4X
- **Load Time**: 4545 ms
- **Memory**: Static 65.05 MiB, Managed 6.33 MiB

> [!NOTE]
> Platform pacing consideration: on macOS systems with ProMotion displays, CVDisplayLink or the system compositor may pace presentation at the display refresh rate (e.g. 120 Hz / 8.33 ms). Uncapped benchmark reports wall clock frame intervals and CPU process time.

## Measurements (600 consecutive frames per viewpoint)

| Metric | Overview View (Size 4600 m) | Riverside Town View (Size 380 m) |
|---|---|---|
| **Mean Frame Interval** | 11.08 ms | 8.33 ms |
| **p50 (Median)** | 11.00 ms | 8.31 ms |
| **p95** | 12.05 ms | 9.04 ms |
| **p99** | 12.50 ms | 9.44 ms |
| **Max Frame Interval** | 12.65 ms | 9.96 ms |
| **Mean CPU Process Time** | 0.064 ms | 0.065 ms |
| **Throughput (Mean / Median)** | 90 / 91 FPS | 120 / 120 FPS |
| **Draw Calls** | 457 | 864 |
| **Objects Rendered** | 691 | 1095 |
| **Primitives (Triangles)** | 943,664 | 1,035,604 |

Spatial chunking partitions the vegetation into 10 x 10 chunks matching terrain, enabling frustum and shadow culling when zoomed into local sites like Town.
