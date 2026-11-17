# Project working notes

- Do not launch s&box. The user explicitly requested this on 2026-10-20 because
  their engine checkout is currently broken. `scripts/Test-SboxImporter.ps1`
  compiles the addon against installed assemblies without starting the engine.
- The Rust migration is active. Read `docs/MIGRATION.md` for implemented paths,
  real-data validation, unresolved formats and remaining parity work.
- The original working tree already contained extraction changes when migration
  began. Preserve them. RainbowScimitar owns the C# library compilation;
  RainbowForge.csproj is a compatibility reference.
- Validate Rust with `cargo test --workspace` and
  `cargo clippy --workspace --all-targets -- -D warnings`. Build distributable
  executables with `cargo build --workspace --release`.
- Use synthetic binary fixtures for regressions. Keep game data, exports, DLLs
  and generated binaries out of source control. The `target` directory is ignored.
- Do not claim full migration or complete map reconstruction while parity work
  or export diagnostics remain unresolved.
