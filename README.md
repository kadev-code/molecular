# Molecular

Mixer pessoal de áudio para Windows, construído com WPF e Core Audio.

## Estado do MVP

- Controle de volume, mute e solo por aplicativo.
- Medição de nível em tempo real.
- Canais rápidos e expandidos persistentes.
- Adição, ocultação, remoção e restauração de canais.
- Controles de mídia quando disponibilizados pelo Windows.
- Proteção sonora com teto global.
- Instância única e operação pela bandeja do Windows.

O planejamento e os critérios de aceite estão em [ROADMAP.md](ROADMAP.md).

## Requisitos

- Windows 10 (19041) ou mais recente.
- .NET 8 SDK para compilar.

## Compilar

```powershell
dotnet restore Molecular.sln
dotnet build src\Molecular.App\Molecular.App.csproj -c Debug --no-restore -p:UseAppHost=true -p:OutputPath=build\
```

O executável oficial é sempre `build\Molecular.exe`. A pasta `build` não é versionada.

## Testes

```powershell
dotnet run --project src\Molecular.Core.Tests\Molecular.Core.Tests.csproj -c Debug --no-restore
```

## Dados locais

Perfis e diagnósticos são armazenados em `%LOCALAPPDATA%\Molecular`. Nenhum título de mídia é registrado nos logs de falha.
