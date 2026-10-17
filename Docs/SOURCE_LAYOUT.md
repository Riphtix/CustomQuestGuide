# Source layout

The project is organized by responsibility instead of a flat source directory.

- `Core/` — configuration and shared domain models.
- `Domain/` — static target/set/job/duty catalogs and curated acquisition knowledge.
- `Data/` — indexes, caches, ownership/location lookup, and external/community data adapters.
- `Resolution/` — acquisition resolution pipeline, policies, fallbacks, and action guidance.
- `UI/` — Dalamud plugin shell and UI/presentation partials.
- `Infrastructure/` — generic scheduling, network bookkeeping, CSV/reflection helpers, and fingerprints.
- `Docs/` — architecture/release/validation documentation.

Small policy/catalog types that change together are intentionally co-located in cohesive files; large provider/index classes remain separate. This reduces file sprawl without creating new god classes solely to lower the file count.
