# Push the scaffold branch to GitHub and open a PR (run on a machine with git + gh auth).
# Usage:
#   $env:GH_TOKEN = "<github_pat_with_repo_scope>"
#   .\packaging\push-to-github.ps1
$ErrorActionPreference = "Stop"
$Branch = "cursor/phase-0-1-scaffold-d613"
$Repo = "https://github.com/SergeyKV00/AutoProdScripts.git"

git remote remove github 2>$null
git remote add github $Repo
git push -u github $Branch

gh pr create --repo SergeyKV00/AutoProdScripts --base main --head $Branch `
  --title "Phase 0/1 scaffold: WPF .NET 8 AutoProdScripts" `
  --body @"
## Summary
- WPF + .NET 8 desktop scaffold (Russian UI)
- Settings: project path, ADO org/project/repo, PAT (Credential Manager / DPAPI)
- Main IDE: project tree, AvalonEdit SQL editor, toolbar
- Git CLI: fetch, branches, create branch, status, commit, push
- Azure DevOps Create PR + URL dialog
- Portable publish script (\`packaging/publish.ps1\`)

## Test plan
- [ ] \`dotnet build AutoProdScripts.sln\` on Windows
- [ ] First-run settings against a local git repo
- [ ] Create branch + SQL file
- [ ] Create PR with a valid ADO PAT
"@
