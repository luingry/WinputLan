## Orchestration preference
- Delivery priority: speed

## Releases
- Release notes are written in Brazilian Portuguese. The `CHANGELOG.md` entry of each version is both the GitHub release body (`scripts/release-notes.ps1`) and the in-app "Notas da versão" window, so write every new entry in pt-BR, including section headings (`### Adicionado`, `### Alterado`, `### Corrigido`, `### Removido`). Keep the `## [x.y.z] - YYYY-MM-DD` header format unchanged; the scripts and the app parse it. Older entries stay as they are.
- Keep only the newest GitHub release in the repo (storage quota). The release workflow deletes older releases after publishing; git tags are kept. Never re-create or restore old releases.
