# Changelog

All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](http://keepachangelog.com/en/1.0.0/)
and this project adheres to [Semantic Versioning](http://semver.org/spec/v2.0.0.html).

## [0.3.0] - 2026-07-02

### Added

- SDEF skinning. SDEF vertices are now deformed on the GPU via the new `MMDSDEFSkinner` component and its compute shader instead of being approximated as BDEF2. Each affected `SkinnedMeshRenderer` runs a per-frame compute pass that applies vertex-morph blend shapes and bone skinning (linear blend for BDEF vertices, the SDEF formula for SDEF vertices) into the renderer's output vertex buffer, driven by the owning `MMDTransformManager` after the bone solve.

### Changed

- Async load/bake APIs now return `System.Threading.Tasks.Task<T>` instead of Unity `Awaitable<T>` for Unity 2022.x compatibility. Affects `PMXReader.ReadAsync`, `PMXImporter.BuildUnityObjectsAsync`, `VMDReader.ReadAsync`, `VMDAnimationClipConverter.ConvertAsync`/`ConvertCameraAsync`, and `PMXAnimationPathBuilder.BuildAsync`.
- Lowered the `com.unity.ugui` dependency from `2.0.0` to `1.0.0` to broaden Unity 2022.x compatibility.

## [0.2.1] - 2026-06-30

### Changed

- Minor package updates to meet Unity Asset Store publishing requirements.

## [0.2.0] - 2026-06-29

### Added

- WebAssembly support.
- Face and eye material heuristics for lilToon shadow tuning.

### Changed

- Material transparency detection now runs on the CPU with a Burst job sampling decoded source-file pixels, replacing the GPU compute-shader path.
- Create asynchronous version of most hot path functions for loading models and baking animtions runtime.
- lilToon materials now also set `_ShadowReceive`, `_ShadowBorder`, and `_lilShadowCasterBias`. Faces
  use a 0.3 shadow border, eyes 0.1, everything else 0.5; faces and eyes also get a 0.05 shadow caster
  bias.

### Removed

- Unused P/Invoke entry points `MMDBulletPhysicsSetRigidBodyTransform` and
  `MMDBulletPhysicsGetRigidBodyMotionTransform`. The batched `MMDBulletPhysicsGetRigidBodyMotionTransforms`
  replaces them.

## [0.1.1] - 2026-06-28

### Added

- Per-slot external material overrides. `PMXImportOptions.materialOverrides` supplies a `Material` per
  generated slot instead of a generated one, surfaced in the importer inspector via a new **Materials**
  tab (Standard/Override creation modes, per-slot remap list, and an "Extract Materials..." action).
- Tabbed PMX importer inspector (Model, Rig, Animation, Materials), split into per-tab editors under
  `Editor/Importers/`.
- Camera clip frame-rate selection (30 / 60 / 120 fps) in the VMD Clip Converter. The native 30 fps VMD
  camera timeline is sub-sampled at higher integer multiples, preserving real-time duration and MMD hard
  cuts.
- `MMDConstants.k_VMDNativeFrameRate` (30 fps) constant.

### Changed

- Edge-drawing PMX materials now use lilToon's outline-capable variant (`Hidden/lilToonMultiOutline`);
  the plain `lilToonMulti` shader has no outline pass. URP and built-in fallbacks still drop edges.
- Reorganized editor scripted importers and inspectors into `Editor/Importers/`, with shared VMD
  progress reporting extracted to `VMDClipProgress`.

### Fixed

- lilToon outline width now converts correctly into lilToon's 1 cm slider unit (previously over-scaled),
  and outline color is no longer affected by scene lighting, matching MMD's flat edges.

## [0.1.0] - 2026-06-23

First release of Unity MMD Tools (UMT).

### Added

- PMX 2.0 model import (with PMX 2.1 read compatibility): meshes, materials, bones with bindposes, and
  vertex-morph blend shapes, generated as sub-assets of the imported `.pmx`.
- MMD runtime: `MMDTransformManager` for MMD transform order, constraints, and IK solving, plus an
  optional Bullet-backed `MMDPhysicsManager` for rigid bodies and joints.
- VMD motion conversion to `AnimationClip` with runtime-solved and baked-FK modes, optional physics
  baking, and morph/IK-toggle curves.
- Optional humanoid `Avatar` generation for retargeting.
- Japanese (Kawazu) and Chinese (PinyinNet) name romanization for materials, bones, and morphs.
- BMP and TGA texture decoding in addition to Unity's built-in PNG/JPG support.
- Native physics plugin for Windows x64 and Android arm64-v8a.
- Editor entry points: scripted importers for `.pmx` and `.vmd` assets, plus **Tools ▸ UMT ▸ VMD Clip
  Converter** and **Tools ▸ UMT ▸ Create Default Resources** menu commands.
