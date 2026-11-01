# Changelog

## 0.2.0 - 2026-11-01

### Changed
- `forget_solution` refuses an exact name shared by several solutions, and asks for the full path.
- The version is declared once, in `Directory.Build.props`, and the server reports it.

### Fixed
- A late event of a superseded HEAD watcher cannot release the newer registration.
