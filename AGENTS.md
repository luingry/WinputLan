## Orchestration preference
- Delivery priority: speed

## Releases
- Release notes are written in Brazilian Portuguese. The `CHANGELOG.md` entry of each version is both the GitHub release body (`scripts/release-notes.ps1`) and the in-app "Notas da versão" window, so write every new entry in pt-BR, including section headings (`### Adicionado`, `### Alterado`, `### Corrigido`, `### Removido`). Keep the `## [x.y.z] - YYYY-MM-DD` header format unchanged; the scripts and the app parse it. Older entries stay as they are.
- Merging a `VERSION` bump into `main` publishes `vX.Y.Z` (no manual tag needed); the release carries `winput-lan-X.Y.Z.zip`, which the SwissTechKnife catalog sync picks up on its own. The catalog is English: in the same change add the version's English notes to `releaseNotes` in `src/WinputLan.Module/module.json` (and keep its `version` equal to `VERSION`).
- Keep only the newest GitHub release in the repo (storage quota). The release workflow deletes older releases after publishing; git tags are kept. Never re-create or restore old releases.
