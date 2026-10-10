# AGENTS.md — AutoProdScripts

Краткий гайд для локальных Cursor-агентов. Подробности: [README.md](README.md).

## What this app is / Что это

Windows WPF IDE для ежедневной работы с **ProductionScripts**: выбрать папку репо → ветка от base → SQL → commit/push → **Create PR** в Azure DevOps → ссылка на PR.

Repo: https://github.com/SergeyKV00/AutoProdScripts

## Stack

- **WPF + .NET 8**
- **AvalonEdit** (SQL editor)
- **Git for Windows** — вызов CLI `git` (не LibGit2Sharp-only)
- **Azure DevOps REST** + PAT (MVP)

## Locked decisions

| Тема | Решение |
|------|---------|
| UI | **только русский (RU)** |
| Repos | **один** репозиторий (v1) |
| Distribution | **portable-папка** (не MSI / не forced single-file) |
| Base branch | выбор из local/remote; **persist last selected** (обычно `master`) |
| Auth | PAT в **Windows Credential Manager** / DPAPI |

## Org / paths

- ADO org hint: https://cit-damu.visualstudio.com/
- Пример ProductionScripts: `C:\Users\damumed\source\repos\ProductionScripts`

## Conventions for agents

- **Не коммитить секреты** (PAT, токены, credentials) — ни в код, ни в docs, ни в settings examples.
- UI-строки оставлять **на русском**.
- Тема: читаемость важнее декора — **тёмный UI + приглушённая SQL-подсветка** (см. недавний contrast/SQL theme fix).
- Не расширять scope без запроса (один репо, portable, git CLI, RU UI).

## Build / run (кратко)

```powershell
dotnet restore AutoProdScripts.sln
dotnet run --project src\AutoProdScripts\AutoProdScripts.csproj
```

Portable publish:

```powershell
.\packaging\publish.ps1
# или: dotnet publish ... -r win-x64 --self-contained true -o .\publish\AutoProdScripts
```

Полные команды, структура и Phase TODO — в [README.md](README.md).
