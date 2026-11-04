# Changelog

## Unreleased

### Fixed
- A solution whose project files were written while MSBuild was loading it is reloaded once more, so a half-written file no longer leaves a broken compilation.

## 0.2.0 - 2026-11-01

### Changed
- `forget_solution` refuses an exact name shared by several solutions, and asks for the full path.
- The version is declared once, in `Directory.Build.props`, and the server reports it.

### Fixed
- A late event of a superseded HEAD watcher cannot release the newer registration.
