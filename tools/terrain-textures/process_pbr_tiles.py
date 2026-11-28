#!/usr/bin/env python3
"""Processes raw terrain/road albedo source tiles into guaranteed-seamless
512x512 PBR albedo + tangent-space normal maps, plus periodic water normal,
RGBA macro variation, and rain streak textures for Godot 4.

Usage:
    python3 tools/terrain-textures/process_pbr_tiles.py <raw_dir> <out_dir>
    python3 tools/terrain-textures/process_pbr_tiles.py --self-test
"""

import math
import os
import sys
import numpy as np
from PIL import Image, ImageFilter

TILE_SIZE = 512
OVERLAP_MARGIN = 128
SOURCE_SIZE = TILE_SIZE + OVERLAP_MARGIN


def periodic_gaussian_blur(field: np.ndarray, sigma: float) -> np.ndarray:
    """Applies an exact periodic (toroidal) Gaussian blur via 2D FFT."""
    h, w = field.shape
    fy = np.fft.fftfreq(h).astype(np.float32)
    fx = np.fft.fftfreq(w).astype(np.float32)
    yy, xx = np.meshgrid(fy, fx, indexing="ij")
    kernel = np.exp(-2.0 * (math.pi ** 2) * (sigma ** 2) * (xx * xx + yy * yy))
    blurred = np.fft.ifft2(np.fft.fft2(field) * kernel).real
    return blurred.astype(np.float32)


def make_seamless_overlap(arr: np.ndarray, tile_size: int = TILE_SIZE) -> np.ndarray:
    """Builds a tile_size x tile_size seamless tile from a larger (tile_size + margin)
    source image by cross-fading the genuine right/bottom continuation margins onto the
    left/top edges using an exact partition-of-unity raised cosine.

    Properties:
    - Exact unity weights (w + (1 - w) == 1 everywhere): flat inputs stay flat with zero dark points.
    - Zero edge discontinuity across x=tile_size-1 <-> x=0 and y=tile_size-1 <-> y=0.
    - Zero artificial half-tile duplication: full tile_size x tile_size area remains unique.
    """
    h, w, c = arr.shape
    margin_y = h - tile_size
    margin_x = w - tile_size
    if margin_y < 16 or margin_x < 16:
        raise ValueError(f"Expected input larger than {tile_size}x{tile_size} by at least 16px, got {w}x{h}")

    # 1. Horizontal seam blend using genuine right-side continuation pixels
    tx = 0.5 * (1.0 + np.cos(math.pi * np.linspace(0.0, 1.0, margin_x, endpoint=False, dtype=np.float32)))
    wx = tx[None, :, None]
    h_blended = arr[:, :tile_size, :].copy()
    h_blended[:, :margin_x, :] = (
        arr[:, tile_size:tile_size + margin_x, :] * wx
        + arr[:, :margin_x, :] * (1.0 - wx)
    )

    # 2. Vertical seam blend using genuine bottom continuation pixels (already horizontally seamless)
    ty = 0.5 * (1.0 + np.cos(math.pi * np.linspace(0.0, 1.0, margin_y, endpoint=False, dtype=np.float32)))
    wy = ty[:, None, None]
    out = h_blended[:tile_size, :, :].copy()
    out[:margin_y, :, :] = (
        h_blended[tile_size:tile_size + margin_y, :, :] * wy
        + h_blended[:margin_y, :, :] * (1.0 - wy)
    )
    return out


def flatten_low_freq_lighting(arr: np.ndarray, blur_radius: float = 40.0, strength: float = 0.78) -> np.ndarray:
    """Removes uneven broad lighting/vignette gradients while preserving constant inputs exactly."""
    u8 = np.clip(np.round(arr * 255.0), 0, 255).astype(np.uint8)
    img = Image.fromarray(u8, mode="RGB")
    blurred = np.asarray(img.filter(ImageFilter.GaussianBlur(radius=blur_radius)), dtype=np.float32) / 255.0
    mean_rgb = arr.mean(axis=(0, 1), keepdims=True)
    flattened = arr - (blurred - mean_rgb) * strength
    return np.clip(flattened, 0.02, 0.96)


def harmonize_palette(arr: np.ndarray, target_rgb: tuple[float, float, float], contrast: float = 1.0, sat_scale: float = 0.85) -> np.ndarray:
    """Calibrates albedo tile to restrained Cold War European palette targets."""
    mean_rgb = arr.mean(axis=(0, 1), keepdims=True)
    out = mean_rgb + (arr - mean_rgb) * contrast
    lum = out[..., 0:1] * 0.299 + out[..., 1:2] * 0.587 + out[..., 2:3] * 0.114
    out = lum + (out - lum) * sat_scale
    cur_mean = np.maximum(out.mean(axis=(0, 1), keepdims=True), 1e-4)
    tgt = np.array(target_rgb, dtype=np.float32).reshape(1, 1, 3)
    out = out * (tgt / cur_mean)
    return np.clip(out, 0.02, 0.92)


def structural_height_from_albedo(arr: np.ndarray) -> np.ndarray:
    """Extracts a periodic multi-band high-pass structural micro-relief heightfield so broad
    color patches do not turn into false pillowy bumps."""
    lum = (arr[..., 0] * 0.299 + arr[..., 1] * 0.587 + arr[..., 2] * 0.114).astype(np.float32)
    hp_fine = lum - periodic_gaussian_blur(lum, sigma=2.4)
    hp_mid = lum - periodic_gaussian_blur(lum, sigma=7.5)
    height = hp_fine * 0.68 + hp_mid * 0.32
    return height


def height_to_normal(height: np.ndarray, strength: float = 2.4) -> np.ndarray:
    """Computes a periodic tangent-space normal map from a tileable heightfield."""
    h_l = np.roll(height, 1, axis=1)
    h_r = np.roll(height, -1, axis=1)
    h_u = np.roll(height, 1, axis=0)
    h_d = np.roll(height, -1, axis=0)
    h_ul = np.roll(h_u, 1, axis=1)
    h_ur = np.roll(h_u, -1, axis=1)
    h_dl = np.roll(h_d, 1, axis=1)
    h_dr = np.roll(h_d, -1, axis=1)

    dx = (h_ur + 2.0 * h_r + h_dr) - (h_ul + 2.0 * h_l + h_dl)
    dy = (h_dl + 2.0 * h_d + h_dr) - (h_ul + 2.0 * h_u + h_ur)

    nx = -dx * strength
    ny = -dy * strength
    nz = np.ones_like(height, dtype=np.float32)
    inv_len = 1.0 / np.sqrt(nx * nx + ny * ny + nz * nz + 1e-8)
    nx *= inv_len
    ny *= inv_len
    nz *= inv_len

    normal_rgb = np.stack([nx * 0.5 + 0.5, ny * 0.5 + 0.5, nz * 0.5 + 0.5], axis=-1)
    return np.clip(np.round(normal_rgb * 255.0), 0, 255).astype(np.uint8)


def periodic_fbm(size: int, seed: int, octaves: list[tuple[int, float]]) -> np.ndarray:
    """Generates a guaranteed 100% periodic 2D smooth noise field via integer-frequency Fourier synthesis."""
    rng = np.random.default_rng(seed)
    grid = np.linspace(0.0, 2.0 * math.pi, size, endpoint=False, dtype=np.float32)
    xx, yy = np.meshgrid(grid, grid)
    field = np.zeros((size, size), dtype=np.float32)
    amp_sum = 0.0
    for freq, amp in octaves:
        for _ in range(4):
            angle = rng.uniform(0.0, math.pi)
            kx = max(1, int(round(math.cos(angle) * freq))) if abs(math.cos(angle)) > 0.15 else 0
            ky = max(1, int(round(math.sin(angle) * freq))) if abs(math.sin(angle)) > 0.15 else 0
            if kx == 0 and ky == 0:
                kx = freq
            if rng.random() < 0.5:
                kx = -kx
            phase = rng.uniform(0.0, 2.0 * math.pi)
            field += np.cos(kx * xx + ky * yy + phase) * amp
            amp_sum += amp
    field /= max(amp_sum, 1e-5)
    fmin, fmax = float(field.min()), float(field.max())
    return (field - fmin) / max(fmax - fmin, 1e-5)


def verify_tile_quality(arr: np.ndarray, label: str) -> None:
    """Verifies no artificial half-tile duplication and no crushed black outlier artifacts."""
    h, w, _ = arr.shape
    assert h == TILE_SIZE and w == TILE_SIZE, f"{label}: unexpected size {w}x{h}"

    # Check half-period diagonal shift correlation on luminance (catches half-period duplication bugs
    # without cross-channel R/G/B hue bias)
    lum = arr[..., 0] * 0.299 + arr[..., 1] * 0.587 + arr[..., 2] * 0.114
    shifted_lum = np.roll(np.roll(lum, h // 2, axis=0), w // 2, axis=1)
    corr = float(np.corrcoef(lum.reshape(-1), shifted_lum.reshape(-1))[0, 1])
    if corr > 0.35:
        raise AssertionError(f"{label}: artificial half-tile duplication detected (diag lum corr={corr:.3f})")

    # Check edge wrap continuity compared to interior neighbor step
    seam_x = float(np.abs(arr[:, 0, :] - arr[:, -1, :]).mean())
    interior_x = float(np.abs(arr[:, 1:, :] - arr[:, :-1, :]).mean())
    seam_y = float(np.abs(arr[0, :, :] - arr[-1, :, :]).mean())
    interior_y = float(np.abs(arr[1:, :, :] - arr[:-1, :, :]).mean())
    if seam_x > interior_x * 2.5 + 0.01 or seam_y > interior_y * 2.5 + 0.01:
        raise AssertionError(
            f"{label}: visible seam detected (seam_x={seam_x:.4f} vs {interior_x:.4f}, "
            f"seam_y={seam_y:.4f} vs {interior_y:.4f})"
        )


def run_self_tests() -> None:
    """Validates constant-field preservation, wrap continuity, and absence of half-period artifacts."""
    # 1. Constant 0.5 field must remain exactly 0.5 everywhere (zero black dots)
    const_in = np.full((SOURCE_SIZE, SOURCE_SIZE, 3), 0.5, dtype=np.float32)
    const_out = make_seamless_overlap(const_in, TILE_SIZE)
    max_err = float(np.abs(const_out - 0.5).max())
    if max_err > 1e-6:
        raise AssertionError(f"Constant field test failed: max_err={max_err}")

    # Flat albedo must yield flat (128, 128, 255) normal map
    const_norm = height_to_normal(structural_height_from_albedo(const_out), strength=2.4)
    if int(const_norm[..., 2].min()) < 254:
        raise AssertionError(f"Flat normal test failed: min nz={const_norm[..., 2].min()}")

    # 2. Random smooth non-periodic input must have near-zero diagonal half-period correlation
    rng = np.random.default_rng(42)
    noise_in = rng.uniform(0.2, 0.8, size=(SOURCE_SIZE, SOURCE_SIZE, 3)).astype(np.float32)
    noise_out = make_seamless_overlap(noise_in, TILE_SIZE)
    verify_tile_quality(noise_out, "self_test_noise")
    print("SELF_TEST_OK: constant_preservation=exact, half_period_duplication=none, wrap_continuity=ok")


def process_tile(
    raw_path: str,
    out_albedo: str,
    out_normal: str,
    target_rgb: tuple[float, float, float],
    contrast: float,
    sat_scale: float,
    normal_strength: float,
) -> None:
    img = Image.open(raw_path).convert("RGB")
    w, h = img.size
    side = min(w, h)
    left = (w - side) // 2
    top = (h - side) // 2
    img = img.crop((left, top, left + side, top + side)).resize((SOURCE_SIZE, SOURCE_SIZE), Image.Resampling.LANCZOS)

    arr = np.asarray(img, dtype=np.float32) / 255.0
    arr = flatten_low_freq_lighting(arr, blur_radius=40.0, strength=0.78)
    arr = make_seamless_overlap(arr, TILE_SIZE)
    arr = harmonize_palette(arr, target_rgb=target_rgb, contrast=contrast, sat_scale=sat_scale)
    verify_tile_quality(arr, os.path.basename(out_albedo))

    albedo_u8 = np.clip(np.round(arr * 255.0), 0, 255).astype(np.uint8)
    Image.fromarray(albedo_u8, mode="RGB").save(out_albedo, optimize=True)

    height = structural_height_from_albedo(arr)
    normal_u8 = height_to_normal(height, strength=normal_strength)
    Image.fromarray(normal_u8, mode="RGB").save(out_normal, optimize=True)
    print(f"Baked {os.path.basename(out_albedo)} + {os.path.basename(out_normal)}")


def generate_utility_textures(out_dir: str) -> None:
    # 1. Seamless organic river wave normal map (periodic multi-directional capillary + swell ripples)
    wave_h = periodic_fbm(
        TILE_SIZE,
        seed=1982,
        octaves=[
            (3, 0.30),
            (5, 0.28),
            (9, 0.22),
            (16, 0.14),
            (28, 0.08),
            (46, 0.04),
        ],
    )
    water_norm = height_to_normal(wave_h, strength=1.65)
    water_path = os.path.join(out_dir, "water_normal.png")
    Image.fromarray(water_norm, mode="RGB").save(water_path, optimize=True)
    print(f"Baked {os.path.basename(water_path)}")

    # 2. Seamless RGBA macro variation + organic puddle map (1 texture fetch replaces 40 ALU hashes)
    r_macro = periodic_fbm(TILE_SIZE, seed=101, octaves=[(1, 0.45), (2, 0.35), (4, 0.20)])
    g_patch = periodic_fbm(TILE_SIZE, seed=202, octaves=[(3, 0.40), (6, 0.35), (12, 0.25)])
    warp_x = periodic_fbm(TILE_SIZE, seed=303, octaves=[(2, 0.6), (5, 0.4)])
    warp_y = periodic_fbm(TILE_SIZE, seed=404, octaves=[(2, 0.6), (5, 0.4)])
    base_puddle = periodic_fbm(TILE_SIZE, seed=505, octaves=[(4, 0.50), (8, 0.32), (16, 0.18)])
    yy, xx = np.meshgrid(np.arange(TILE_SIZE), np.arange(TILE_SIZE), indexing="ij")
    sx = (xx + ((warp_x - 0.5) * 64.0).astype(np.int32)) % TILE_SIZE
    sy = (yy + ((warp_y - 0.5) * 64.0).astype(np.int32)) % TILE_SIZE
    b_puddle = base_puddle[sy, sx]
    a_fine = periodic_fbm(TILE_SIZE, seed=606, octaves=[(8, 0.45), (16, 0.35), (32, 0.20)])

    macro_rgba = np.stack([r_macro, g_patch, b_puddle, a_fine], axis=-1)
    macro_u8 = np.clip(np.round(macro_rgba * 255.0), 0, 255).astype(np.uint8)
    macro_path = os.path.join(out_dir, "macro_noise.png")
    Image.fromarray(macro_u8, mode="RGBA").save(macro_path, optimize=True)
    print(f"Baked {os.path.basename(macro_path)}")

    # 3. Seamless 256x256 soft rain streak texture for zero-loop, zero-FPS-drop storm drizzle
    rain_size = 256
    rng = np.random.default_rng(1984)
    rain_buf = np.zeros((rain_size, rain_size), dtype=np.float32)
    yy_r, xx_r = np.meshgrid(np.arange(rain_size, dtype=np.float32), np.arange(rain_size, dtype=np.float32), indexing="ij")
    for _ in range(135):
        cx = rng.uniform(0.0, rain_size)
        cy = rng.uniform(0.0, rain_size)
        length = rng.uniform(14.0, 30.0)
        width = rng.uniform(0.75, 1.35)
        intensity = rng.uniform(0.28, 0.78)
        # Wrapped distances on 256x256 torus with subtle wind slant
        dx = ((xx_r - cx + rain_size * 0.5) % rain_size) - rain_size * 0.5
        dy = ((yy_r - cy + rain_size * 0.5) % rain_size) - rain_size * 0.5
        # Rotate slightly by ~10 deg wind slant
        u_rot = dx * 0.985 - dy * 0.174
        v_rot = dx * 0.174 + dy * 0.985
        across = np.exp(-0.5 * (u_rot / width) ** 2)
        along = np.clip(1.0 - np.abs(v_rot) / (length * 0.5), 0.0, 1.0)
        along = along * along * (3.0 - 2.0 * along)
        # Slight teardrop brighter head at bottom of streak
        head = np.clip(0.65 + 0.55 * (v_rot / (length * 0.5)), 0.35, 1.15)
        rain_buf = np.maximum(rain_buf, across * along * head * intensity)

    rain_u8 = np.clip(np.round(rain_buf * 255.0), 0, 255).astype(np.uint8)
    rain_rgba = np.stack([
        np.full_like(rain_u8, 224),
        np.full_like(rain_u8, 232),
        np.full_like(rain_u8, 240),
        rain_u8,
    ], axis=-1)
    rain_path = os.path.join(out_dir, "rain_streaks.png")
    Image.fromarray(rain_rgba, mode="RGBA").save(rain_path, optimize=True)
    print(f"Baked {os.path.basename(rain_path)}")


def main() -> int:
    if len(sys.argv) == 2 and sys.argv[1] == "--self-test":
        run_self_tests()
        return 0

    if len(sys.argv) != 3:
        print("Usage: process_pbr_tiles.py <raw_dir> <out_dir> | --self-test")
        return 1

    run_self_tests()
    raw_dir, out_dir = sys.argv[1], sys.argv[2]
    os.makedirs(out_dir, exist_ok=True)

    specs = [
        # (raw_file, out_prefix, target_rgb, contrast, sat_scale, normal_strength)
        ("meadow_grass_raw.png", "meadow_grass", (0.31, 0.37, 0.22), 1.04, 0.80, 2.6),
        ("dry_field_raw.png", "dry_field", (0.42, 0.39, 0.26), 1.04, 0.78, 2.8),
        ("slope_soil_raw.png", "slope_soil", (0.33, 0.30, 0.24), 1.05, 0.75, 3.0),
        ("asphalt_raw.png", "asphalt", (0.26, 0.27, 0.28), 1.06, 0.65, 2.5),
        ("gravel_raw.png", "gravel", (0.41, 0.38, 0.32), 1.06, 0.75, 2.8),
    ]

    for raw_name, prefix, target_rgb, contrast, sat_scale, n_str in specs:
        raw_path = os.path.join(raw_dir, raw_name)
        if not os.path.exists(raw_path):
            print(f"ERROR: Missing raw tile {raw_path}", file=sys.stderr)
            return 2
        process_tile(
            raw_path,
            os.path.join(out_dir, f"{prefix}_albedo.png"),
            os.path.join(out_dir, f"{prefix}_normal.png"),
            target_rgb,
            contrast,
            sat_scale,
            n_str,
        )

    generate_utility_textures(out_dir)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
