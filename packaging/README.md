# Packaging

Портативная сборка (копируемая папка):

```powershell
.\publish.ps1
```

Эквивалент:

```powershell
dotnet publish ..\src\AutoProdScripts\AutoProdScripts.csproj -c Release -r win-x64 --self-contained true -o ..\publish\AutoProdScripts
```
