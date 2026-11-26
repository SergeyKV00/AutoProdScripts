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

**Быстрое добавление.** Кнопка на панели «Файлы» открывает диалог: несколько SQL-скриптов, папка по первому `USE` из файла `mapping/use-folders.yaml` рядом с программой (папка копируется в сборку; не секрет, только имя БД и относительный путь). Справа — дерево назначения; папку можно выбрать вручную. «Сохранить» только записывает файлы, «Создать PR» после записи вызывает обычный Create PR. Меню «Файл → Проанализировать папки и USE» читает только папку `2026` репозитория скриптов, показывает, где в одной папке смешаны разные `USE`, и может дописать в mapping приложения только однозначные пары.

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
├── src/AutoProdScripts.Core/     # раздел «Запросы»: подключения, проверка SQL, выполнение
├── tests/AutoProdScripts.Core.Tests/
├── src/AutoProdScripts/          # WPF UI + сервисы
│   ├── Services/Git/             # вызов CLI git
│   ├── Services/AzureDevOps/     # REST Create PR / проверка PAT
│   ├── Services/Settings/        # JSON + DPAPI / Credential Manager
│   └── Views/                    # настройки, диалоги
└── packaging/publish.ps1
```

## Раздел «Запросы»

Переключатель «Репозиторий / Запросы» на панели. Раздел не связан с git и PR: это выполнение SQL на настроенных подключениях к MS SQL Server.

**Подключения:** «Настройки → Подключения к базам» или кнопка «Подключения…». Строку подключения (или целый блок `<connectionStrings>`, или пары из `appsettings.json`) можно вставить — поля заполнятся сами. Список лежит в `%AppData%\AutoProdScripts\connections.json`, пароли — только в Windows Credential Manager. У каждого подключения есть метка среды; новое по умолчанию «БОЙ».

**Только чтение.** Скрипт разбирается парсером `ScriptDom`; разрешено лишь явно перечисленное: `SELECT`, переменные, `IF`/`WHILE`/`TRY`, `USE`, создание и изменение временных таблиц `#…` и табличных переменных. `EXEC`, управление транзакциями, изменение обычных таблиц, `OPENQUERY`/`OPENROWSET`, блокирующие подсказки и всё незнакомое отклоняется. Скрипт выполняется в транзакции, которая всегда откатывается. Это защита приложения, а не сервера: надёжнее всего логин с правами только на чтение.

**Запуск:** F5 (выделенный фрагмент выполняется отдельно). Переменные без `DECLARE` становятся параметрами; для сравнения с колонками `varchar` выбирайте тип «Строка (varchar)», иначе сервер может не использовать индекс. Есть лимит строк (после него выполнение останавливается), отмена, копирование и экспорт в CSV.

Ограничения: `#таблица`, созданная в пакете с параметрами, не видна после `GO`; чтение через linked server внутри транзакции может потребовать MSDTC.

**Параметры.** `WHERE Id = @id` без `DECLARE` — справа появится поле `@id`. Значение вводится без кавычек.

**Библиотека** (панель слева). «Сохранить» (Ctrl+S) записывает скрипт: название, группа (вложенность через `/`), теги, описание, подключение, подписи, типы и значения по умолчанию параметров. Щелчок (или Enter) открывает скрипт. Поиск идёт по названию, описанию, группе, тегам, подключению, базе и тексту SQL; есть фильтры по подключению и тегу, избранное и недавно запущенные (правая кнопка мыши — меню скрипта). Ctrl+S сохраняет текст и типы параметров; подключение скрипта меняется только в «Свойствах», чтобы временный запуск на другом сервере не запоминался.

Файлы: `%AppData%\AutoProdScripts\library\scripts\*.json` (по файлу на скрипт), избранное и даты запуска — `library\state.json`. Введённые при запуске значения не сохраняются; значение по умолчанию сохраняется только если задано в «Свойствах».

Цепочки, обмен файлами и запуск по регионам — следующие этапы.

## Тесты

```powershell
dotnet test AutoProdScripts.sln
```

Часть тестов выполняется на LocalDB и пропускается, если он не установлен.

## TODO (следующие фазы)

- Шаблоны PR title/description и SQL header (расширение)
- Политика encoding/BOM
- Запрет работы напрямую в base branch (жёстче)
- Reviewers / Work Items
- OAuth вместо PAT (опционально)
