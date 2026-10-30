# SpecDD project specific overrides

The selected content root is `gateway/`; The standalone repository wrapper holds public documentation; gateway implementation remains scoped here.
The Operator requested SpecDD adoption and development together, authorizing creation of local specifications and their corresponding scoped implementation artifacts.
Use `.sdd` files for local ownership, behavior and task completion. Keep detailed product requirements in `docs/`; reference rather than duplicate them.
Mark local tasks complete only after their scenarios pass. Keep `docs/PROGRESS.md` as the cross-milestone evidence index.
Existing instructions limiting the earlier documentation-only request describe historical authorization; the later development request supersedes that limit within the gateway scope.
Do not deploy new runtime versions, change ECS configuration or operate phone UI as a side effect of local contract work.
Compiler and test outputs may be generated in ignored `bin/`, `obj/`, `build/` and repository `artifacts/` directories for the selected owned projects; this does not grant authority over unrelated source files.
Use the pinned CLI installed at the repository tools path when available; keep `.specdd/bootstrap.md` immutable.
