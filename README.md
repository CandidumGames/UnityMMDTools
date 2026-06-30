# Unity MMD Tools (UMT)

UMT is a toolkit for maximally reproducing MikuMikuDance (MMD) behaviour in Unity. It supports drag-and-drop import of PMX models, converts VMD motion to `AnimationClip` assets and runs Physics for MMD rigid bodies on a native Bullet solver.

Available on the [Unity Asset Store](https://assetstore.unity.com/packages/package/9648399).

## What it does

- Imports PMX models: skinned meshes, materials, bones with bind poses, and vertex-morph blend shapes, all as sub-assets of the `.pmx`.
- Solve the MMD transforms for transform order, append/inherit constraints, and IKs.
- Physics simulation for rigid bodies and joints. Based on Bullet 2.75 (matches MMD's version).
- Converts Character VMD motion to `AnimationClip` : runtime-solved (sparse curves plus IK toggles) or baked to FK with optional physics baking.
- Converts Camera VMD motion to `AnimationClip`.
- Builds a humanoid `Avatar` (optional).
- Romanizes CJK material, bone, and morph names to ASCII.
- Builds lilToon materials (optional).

## Supported MMD features

### PMX import

- PMX 2.0 (2.1 read compatibility; no 2.1-exclusive features)
- BDEF1, BDEF2, BDEF4 skinning
- Vertex morphs (as blend shapes)
- Group morphs (resolved to their referenced vertex morphs)
- Bones, bind poses, humanoid avatar
- IK (CCD) with iteration/angle limits and per-link axis limits
- Append/inherit rotation constraint
- Append/inherit translation constraint
- Local-axis and inverted constraint influence
- Materials: diffuse color, main texture, double-sided, edge/outline color and width
- Sphere texture (mapped to lilToon matcap)
- Transparency detection
- Textures: PNG, JPG, BMP, TGA
- Up to 4 additional UV channels (parsed)

### MMD physics

- Rigid bodies: sphere, box, capsule shapes
- Rigid-body modes: kinetic (bone-attached), dynamic, dynamic + bone-aligned
- 6DOF joints (spring 6DOF / generic 6DOF)
- Ground collision
- Live physics (play mode) and physics baking (VMD)

### VMD conversion

- Bone animation (runtime-solved sparse curves or baked FK)
- Morph (vertex blend-shape) animation
- IK on/off toggles (stepped curves, sparse mode)
- Camera track (two-node camera rig)

## TODO

- Bone morph
- SDEF deformation (currently imported as BDEF2)
- iOS, Mac OS and Linux support

## Not planned

- Legacy PMD model support
- VPD pose data
- Other morph types
- PMX 2.1 soft body and other exclusive features

## Requirements

Unity 2022.3 or newer, built-in render pipeline or URP. Materials build against lilToon when it's installed, otherwise URP Unlit or built-in Unlit.

Native physics builds for **Windows x64**, **Android arm64-v8a**, and **Web/WebAssembly**. On other platforms you can still import models and convert non-physics motion, but live and baked physics need the native plugin.

## Dependencies

### UMT Native Plugin

[UMTNativePlugin](https://github.com/CandidumGames/UMTNativePlugin) is a C++ based library wrapping **Bullet 2.75** behind a small, flat Cdecl ABI that the managed P/Invoke calls into.

### Unity packages

The Package Manager pulls these in automatically:

- `com.unity.burst`
- `com.unity.collections`
- `com.unity.mathematics`
- `com.unity.nuget.newtonsoft-json`

lilToon ([github.com/lilxyzw/lilToon](https://github.com/lilxyzw/lilToon), 2.0.0+) is optional. It isn't bundled; when it's missing the importer falls back to URP Unlit / built-in Unlit.

## Installation

Add the package through the Unity Package Manager (**Add package from git URL**, or edit `Packages/manifest.json`) and let it resolve the dependencies above.

## Usage

### Import a PMX model

Drop the `.pmx` and its textures into the project and the scripted importer handles it. The generated meshes, materials, bones, avatar, and any converted VMD clips become sub-assets of the `.pmx`.

Select the `.pmx` for importer options: **Create Avatar**, **Generate Debug Data**, and the **VMD Animations** list. With debug data on, the importer also writes `.metadata.json`, `.string-map.json`, and `.import.log` sidecar files.

### Convert a VMD motion

The cleanest path is to convert during PMX import, which keeps each motion tied to the model it was made for:

1. Add the `.vmd` file(s) to the project (they import as `VMDAnimation` assets).
2. Select the imported `.pmx` and open its importer inspector.
3. Add the `.vmd` assets to the **VMD Animations** list and set the frame rate and bake options (**Bake IK To FK**, and when baking, **Bake Physics To FK** with a warm-up duration).
4. Apply to reimport. Each clip is added as a sub-asset of the `.pmx`.

To convert against a model you didn't just import, use the standalone window at **Tools ▸ UMT ▸ VMD Clip Converter**: pick a **Motion** (needs a `PMXModel`) or **Camera** target, choose **IKRuntimeSolved** or **IKBakedToFK** (baked mode can also bake physics to FK), and write out a standalone `.anim` asset.

## License

MIT (see [LICENSE](LICENSE.md)). Bundled third-party components ship under MIT / zlib / NAIST licenses; see [Third Party Notices](<Third Party Notices.md>) for the full attributions.
