# AutoProdScripts

Windows-приложение (WPF, .NET 8) для ежедневной работы с репозиторием продакшн-SQL-скриптов: ветка → SQL → commit/push → Create PR в Azure DevOps.

Интерфейс: **только русский**.

Репозиторий: https://github.com/SergeyKV00/AutoProdScripts

## Требования

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (для сборки)
- [Git for Windows](https://git-scm.com/download/win) в `PATH`
- PAT Azure DevOps с правами на Code (Read & Write) / Pull Requests

## Сборка и запуск (разработка)

```powershell
git clone https://github.com/SergeyKV00/AutoProdScripts.git
cd AutoProdScripts
dotnet restore AutoProdScripts.sln
dotnet build AutoProdScripts.sln -c Debug
dotnet run --project src\AutoProdScripts\AutoProdScripts.csproj
```

Либо откройте `AutoProdScripts.sln` в Visual Studio 2022.

## Portable-публикация (копируемая папка)

Self-contained папка `win-x64` (не MSI, не обязательный single-file):

```powershell
dotnet publish src\AutoProdScripts\AutoProdScripts.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -o .\publish\AutoProdScripts
```

Или скрипт:

```powershell
.\packaging\publish.ps1
```

Скопируйте папку `publish\AutoProdScripts` на любой ПК и запустите `AutoProdScripts.exe`.

## Первый запуск

1. Укажите путь к репозиторию (например `C:\Users\...\ProductionScripts`).
2. Заполните Azure DevOps: Org/URL (`https://cit-damu.visualstudio.com/`), Project, Repo, PAT.
3. Выберите базовую ветку из списка (обычно `master`) — выбор сохраняется.
4. **Сохранить и открыть**.

Настройки: `%AppData%\AutoProdScripts\settings.json`  
PAT: Windows Credential Manager и/или DPAPI-файл в той же папке.

## Возможности Phase 0/1 (каркас MVP)

| Действие | Статус |
|----------|--------|
| Настройки: путь, ADO org/project/repo, PAT, base branch | ✅ |
| Дерево проекта + редактор SQL (AvalonEdit) | ✅ |
| Sync (`git fetch`) | ✅ |
| Новая ветка (ручное / авто `base_yyyyMMddHHmm`) | ✅ |
| Новый `.sql` в выбранной папке | ✅ |
| `git status` / commit / push | ✅ |
| Create PR (ADO REST) + показ URL | ✅ (нужен валидный PAT) |
| Лог в UI + файл | ✅ |

## Структура

```
AutoProdScripts/
├── AutoProdScripts.sln
├── src/AutoProdScripts/          # WPF UI + сервисы
│   ├── Services/Git/             # вызов CLI git
│   ├── Services/AzureDevOps/     # REST Create PR / проверка PAT
│   ├── Services/Settings/        # JSON + DPAPI / Credential Manager
│   └── Views/                    # настройки, диалоги
└── packaging/publish.ps1
```

## TODO (следующие фазы)

- Шаблоны PR title/description и SQL header (расширение)
- Политика encoding/BOM
- Запрет работы напрямую в base branch (жёстче)
- Reviewers / Work Items
- OAuth вместо PAT (опционально)
