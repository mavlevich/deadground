"""Software review renders of generated vehicle GLB files, with no Godot or display needed.

Draws flat shaded triangles with matplotlib: material base color times the baked vertex color,
one key light and ambient. Rig nodes can be posed (TurretYaw about +Y, GunPitch about +X), so
review images show traverse and elevation limits. Game camera views use the default RTS pitch
and the client pixel scale (900 px viewport height per camera size, vehicle visual scale 0.75).

Usage from the repository root:
    python3 tools/model-authoring/render_review.py --out <folder>
"""

import argparse
import json
import math
from pathlib import Path
import sys

import numpy as np

_THIS_DIR = Path(__file__).resolve().parent
if str(_THIS_DIR) not in sys.path:
    sys.path.insert(0, str(_THIS_DIR))

import matplotlib  # noqa: E402

matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402

ASSETS = _THIS_DIR.parents[1] / "src/Game.Godot/Assets/Vehicles"
GAME_PITCH = math.atan2(115.0, 80.0)  # OrbitCameraPose.DefaultPitch
VIEWPORT_HEIGHT_PX = 900.0
VISUAL_SCALE = 0.75
LOD_SIZES = (45.0, 95.0)  # VehicleLod thresholds
BACKGROUND = "#3f463b"


def read_glb(path):
    blob = Path(path).read_bytes()
    json_len = int.from_bytes(blob[12:16], "little")
    data = json.loads(blob[20 : 20 + json_len])
    offset = 20 + json_len
    bin_len = int.from_bytes(blob[offset : offset + 4], "little")
    return data, blob[offset + 8 : offset + 8 + bin_len]


def _accessor(data, binary, index):
    accessor = data["accessors"][index]
    view = data["bufferViews"][accessor["bufferView"]]
    width = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}[accessor["type"]]
    dtype = "<f4" if accessor["componentType"] == 5126 else "<u4"
    raw = np.frombuffer(binary[view["byteOffset"] : view["byteOffset"] + view["byteLength"]], dtype=dtype)
    return raw.reshape(-1, width) if width > 1 else raw


def _rot_y(a):
    c, s = math.cos(a), math.sin(a)
    return np.array([[c, 0, s, 0], [0, 1, 0, 0], [-s, 0, c, 0], [0, 0, 0, 1]], dtype=float)


def _rot_x(a):
    c, s = math.cos(a), math.sin(a)
    return np.array([[1, 0, 0, 0], [0, c, -s, 0], [0, s, c, 0], [0, 0, 0, 1]], dtype=float)


def _translate(t):
    m = np.eye(4)
    m[:3, 3] = t
    return m


def load_triangles(path, yaw_deg=0.0, pitch_deg=0.0, root=None):
    """World triangles (n, 3, 3) and linear colors (n, 3) for a GLB in a given launcher pose.

    `root` is an optional 4x4 matrix placing the whole vehicle in the scene.
    """
    data, binary = read_glb(path)
    nodes = data["nodes"]
    pose = {"TurretYaw": _rot_y(math.radians(yaw_deg)), "GunPitch": _rot_x(math.radians(pitch_deg))}
    tris, colors = [], []

    def visit(index, parent_matrix):
        node = nodes[index]
        local = _translate(node.get("translation", [0.0, 0.0, 0.0])) @ pose.get(node["name"], np.eye(4))
        world = parent_matrix @ local
        if "mesh" in node:
            for primitive in data["meshes"][node["mesh"]]["primitives"]:
                attrs = primitive["attributes"]
                pts = _accessor(data, binary, attrs["POSITION"]).astype(float)
                pts = (world[:3, :3] @ pts.T).T + world[:3, 3]
                idx = _accessor(data, binary, primitive["indices"]).reshape(-1, 3)
                material = data["materials"][primitive["material"]]
                base = np.asarray(material["pbrMetallicRoughness"].get("baseColorFactor", [0.5, 0.5, 0.5, 1.0])[:3])
                if "COLOR_0" in attrs:
                    vc = _accessor(data, binary, attrs["COLOR_0"])[:, :3]
                    col = vc[idx].mean(axis=1) * base
                else:
                    col = np.repeat(base[None, :], len(idx), axis=0)
                tris.append(pts[idx])
                colors.append(col)
        for child in node.get("children", []):
            visit(child, world)

    for top in data["scenes"][0]["nodes"]:
        visit(top, root if root is not None else np.eye(4))
    return np.concatenate(tris), np.concatenate(colors)


def _to_srgb(linear):
    linear = np.clip(linear, 0.0, 1.0)
    return np.where(linear <= 0.0031308, linear * 12.92, 1.055 * np.power(linear, 1 / 2.4) - 0.055)


def camera_basis(yaw_deg, pitch_deg):
    """View direction (from camera into the scene), screen right and screen up.

    yaw 0 looks from +Z toward -Z (from behind the vehicle); pitch is the angle below horizontal.
    """
    yaw, pitch = math.radians(yaw_deg), math.radians(pitch_deg)
    d = np.array([-math.sin(yaw) * math.cos(pitch), -math.sin(pitch), -math.cos(yaw) * math.cos(pitch)])
    world_up = np.array([0.0, 1.0, 0.0]) if abs(pitch_deg) < 89.9 else np.array([-math.sin(yaw), 0.0, -math.cos(yaw)])
    right = np.cross(d, world_up)
    right /= np.linalg.norm(right)
    up = np.cross(right, d)
    return d, right, up


def raster(scenes, yaw_deg, pitch_deg, extent=None, size_px=None, ground=True, light=(-0.45, 0.80, -0.40), supersample=2):
    """Z-buffered orthographic rasterization. Returns (sRGB image (H, W, 3), extent).

    `extent` is (x0, x1, y0, y1) in screen meters; without it the view fits the scene. `size_px` is
    the output (width, height); without it the width is 640 px and the height follows the extent.
    """
    d, right, up = camera_basis(yaw_deg, pitch_deg)
    light = np.asarray(light) / np.linalg.norm(light)
    all_tris, all_rgb = [], []
    for tris, colors in scenes:
        normals = np.cross(tris[:, 1] - tris[:, 0], tris[:, 2] - tris[:, 0])
        length = np.linalg.norm(normals, axis=1)
        keep = length > 1e-12
        tris, colors, normals = tris[keep], colors[keep], normals[keep] / length[keep, None]
        normals = np.where((normals @ d)[:, None] > 0, -normals, normals)  # every face is two sided
        shade = 0.42 + 0.68 * np.clip(normals @ light, 0.0, 1.0)
        all_tris.append(tris)
        all_rgb.append(_to_srgb(colors * shade[:, None] * 1.25))
    vehicle = np.concatenate(all_tris)
    rgb = np.concatenate(all_rgb)
    if extent is None:
        pts = np.stack([vehicle.reshape(-1, 3) @ right, vehicle.reshape(-1, 3) @ up], axis=-1)
        lo, hi = pts.min(axis=0), pts.max(axis=0)
        pad = 0.06 * max(hi - lo)
        extent = (lo[0] - pad, hi[0] + pad, lo[1] - pad, hi[1] + pad)
    x0, x1, y0, y1 = extent
    if size_px is None:
        size_px = (640, max(1, round(640 * (y1 - y0) / (x1 - x0))))
    width, height = size_px[0] * supersample, size_px[1] * supersample
    image = np.empty((height, width, 3))
    image[:] = np.asarray(matplotlib.colors.to_rgb(BACKGROUND))
    zbuf = np.full((height, width), np.inf)
    if ground:
        everything = vehicle.reshape(-1, 3)
        glo, ghi = everything.min(axis=0) - 1.5, everything.max(axis=0) + 1.5
        quad = np.array([[[glo[0], 0, glo[2]], [ghi[0], 0, glo[2]], [ghi[0], 0, ghi[2]]], [[glo[0], 0, glo[2]], [ghi[0], 0, ghi[2]], [glo[0], 0, ghi[2]]]])
        vehicle = np.concatenate([quad, vehicle])
        rgb = np.concatenate([np.array([[0.36, 0.39, 0.33]] * 2), rgb])
    flat = vehicle.reshape(-1, 3)
    sx = ((flat @ right) - x0) / (x1 - x0) * width
    sy = (y1 - (flat @ up)) / (y1 - y0) * height
    sz = flat @ d
    sx, sy, sz = sx.reshape(-1, 3), sy.reshape(-1, 3), sz.reshape(-1, 3)
    for i in range(len(sx)):
        tx, ty, tz = sx[i], sy[i], sz[i]
        c0, c1 = max(int(np.floor(tx.min())), 0), min(int(np.ceil(tx.max())), width - 1)
        r0, r1 = max(int(np.floor(ty.min())), 0), min(int(np.ceil(ty.max())), height - 1)
        if c0 > c1 or r0 > r1:
            continue
        px, py = np.meshgrid(np.arange(c0, c1 + 1) + 0.5, np.arange(r0, r1 + 1) + 0.5)
        det = (ty[1] - ty[2]) * (tx[0] - tx[2]) + (tx[2] - tx[1]) * (ty[0] - ty[2])
        if abs(det) < 1e-12:
            continue
        w0 = ((ty[1] - ty[2]) * (px - tx[2]) + (tx[2] - tx[1]) * (py - ty[2])) / det
        w1 = ((ty[2] - ty[0]) * (px - tx[2]) + (tx[0] - tx[2]) * (py - ty[2])) / det
        w2 = 1.0 - w0 - w1
        inside = (w0 >= -1e-6) & (w1 >= -1e-6) & (w2 >= -1e-6)
        if not inside.any():
            continue
        depth = w0 * tz[0] + w1 * tz[1] + w2 * tz[2]
        if i < 2 and ground:
            depth = depth + 0.02  # the ground never covers parts that touch it
        region = zbuf[r0 : r1 + 1, c0 : c1 + 1]
        closer = inside & (depth < region)
        region[closer] = depth[closer]
        image[r0 : r1 + 1, c0 : c1 + 1][closer] = rgb[i]
    image = image.reshape(size_px[1], supersample, size_px[0], supersample, 3).mean(axis=(1, 3))
    return image, extent


def draw(ax, scenes, yaw_deg, pitch_deg, extent=None, ground=True):
    """Rasterize into a matplotlib axis. Returns the extent used."""
    image, extent = raster(scenes, yaw_deg, pitch_deg, extent=extent, ground=ground)
    ax.imshow(image, extent=extent, interpolation="bilinear")
    ax.set_aspect("equal")
    ax.set_xticks([])
    ax.set_yticks([])
    return extent


def _sheet(cells, columns, path, cell_size=(5.0, 3.6), title=None):
    rows = math.ceil(len(cells) / columns)
    fig, axes = plt.subplots(rows, columns, figsize=(cell_size[0] * columns, cell_size[1] * rows), squeeze=False)
    fig.patch.set_facecolor("#20251f")
    for ax in axes.flat:
        ax.axis("off")
    for ax, (label, render) in zip(axes.flat, cells):
        ax.axis("on")
        render(ax)
        ax.set_title(label, color="#e6e8df", fontsize=10)
    if title:
        fig.suptitle(title, color="#e6e8df", fontsize=12)
    fig.tight_layout()
    fig.savefig(path, dpi=110, facecolor=fig.get_facecolor())
    plt.close(fig)


def game_view(path_out, scenes, camera_size, label):
    """One image at the true client pixel scale, plus a 3x nearest-neighbor enlargement."""
    px_per_m = VIEWPORT_HEIGHT_PX / camera_size
    width_m, height_m = 380.0 / px_per_m, 220.0 / px_per_m
    _, right, up = camera_basis(30.0, math.degrees(GAME_PITCH))
    center = np.concatenate([t.reshape(-1, 3) for t, _ in scenes]).mean(axis=0)
    cx, cy = center @ right, center @ up
    extent = (cx - width_m / 2, cx + width_m / 2, cy - height_m / 2, cy + height_m / 2)
    image, _ = raster(scenes, 30.0, math.degrees(GAME_PITCH), extent=extent, size_px=(380, 220))
    native = Path(path_out)
    plt.imsave(native, np.clip(image, 0.0, 1.0))
    big = np.repeat(np.repeat(image, 3, axis=0), 3, axis=1)
    fig = plt.figure(figsize=(11.4, 7.0), dpi=100)
    ax = fig.add_axes([0, 0, 1, 0.94])
    ax.imshow(big, interpolation="nearest")
    ax.axis("off")
    fig.suptitle(label, color="#e6e8df", fontsize=11)
    fig.patch.set_facecolor("#20251f")
    fig.savefig(native.with_name(native.stem + "_x3.png"), dpi=100, facecolor=fig.get_facecolor())
    plt.close(fig)


def scaled(matrix_scale, offset_x=0.0, yaw_deg=0.0):
    m = _translate((offset_x, 0.0, 0.0)) @ _rot_y(math.radians(yaw_deg))
    m[:3, :3] *= matrix_scale
    return m


def render_bm21(out):
    out = Path(out)
    out.mkdir(parents=True, exist_ok=True)
    folder = ASSETS / "BM21"
    manifest = json.loads((folder / "manifest.json").read_text())
    lod_files = [folder / item["file"] for item in manifest["lods"]]
    rig = manifest["rig"]
    left = rig["TurretYaw"]["traverse_left_degrees"]
    right = -rig["TurretYaw"]["traverse_right_degrees"]
    elevation = rig["GunPitch"]["rotation_x_range_degrees"][1]
    rest = [load_triangles(lod_files[0])]

    views = [("Side (right)", 90.0, 0.0), ("Front", 180.0, 0.0), ("Top", 0.0, 90.0), ("Three-quarter", 140.0, 28.0)]
    _sheet([(name, lambda ax, y=y, p=p: draw(ax, rest, y, p)) for name, y, p in views], 2, out / "bm21_views_lod0.png", title="BM-21 LOD0, rest pose")

    lods = [load_triangles(f) for f in lod_files]
    _sheet(
        [(f"LOD{i}: {item['triangles']} triangles", lambda ax, s=[lods[i]]: draw(ax, s, 140.0, 28.0)) for i, item in enumerate(manifest["lods"])],
        3,
        out / "bm21_lods.png",
        title="LOD comparison, three-quarter view",
    )

    poses = [
        (f"Elevation {elevation:.0f} deg, side", 0.0, elevation, 90.0, 0.0),
        (f"Elevation {elevation:.0f} deg, three-quarter", 0.0, elevation, 140.0, 28.0),
        (f"Traverse {left:.0f} deg left, top", left, 0.0, 0.0, 90.0),
        (f"Traverse {-right:.0f} deg right, top", right, 0.0, 0.0, 90.0),
        (f"Left limit at {elevation:.0f} deg", left, elevation, 140.0, 28.0),
        (f"Right limit at {elevation:.0f} deg", right, elevation, 140.0, 28.0),
    ]
    _sheet(
        [(label, lambda ax, yw=yw, pt=pt, cy=cy, cp=cp: draw(ax, [load_triangles(lod_files[0], yw, pt)], cy, cp)) for label, yw, pt, cy, cp in poses],
        2,
        out / "bm21_poses.png",
        title="Launcher limits",
    )

    btr70 = ASSETS / "BTR70" / "btr70.glb"
    compare = [load_triangles(lod_files[0], root=scaled(1.0, -1.8)), load_triangles(btr70, root=scaled(1.0, 1.8))]
    _sheet(
        [
            ("Side: BTR-70 (near) and BM-21 (far), same scale", lambda ax: draw(ax, compare, 90.0, 0.0)),
            ("Top: BM-21 (left) and BTR-70 (right)", lambda ax: draw(ax, compare, 0.0, 90.0)),
        ],
        1,
        out / "bm21_vs_btr70.png",
        cell_size=(9.0, 4.2),
    )

    for size in (35.0, 70.0, 160.0):
        lod = 0 if size < LOD_SIZES[0] else 1 if size < LOD_SIZES[1] else 2
        scenes = [
            load_triangles(lod_files[lod], root=scaled(VISUAL_SCALE, -3.2, 115.0)),
            load_triangles(ASSETS / "BTR70" / ("btr70.glb" if lod == 0 else f"btr70_lod{lod}.glb"), root=scaled(VISUAL_SCALE, 3.2, 115.0)),
        ]
        game_view(out / f"bm21_game_size{int(size)}.png", scenes, size, f"Game camera size {size:.0f} (LOD{lod}), BM-21 left, BTR-70 right, true pixels shown 3x")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    render_bm21(args.out)
    print(f"Review images written to {args.out}")
