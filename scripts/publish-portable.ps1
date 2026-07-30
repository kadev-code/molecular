[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $repositoryRoot 'src\Molecular.App\Molecular.App.csproj'
$buildDirectory = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'build'))
$expectedBuildDirectory = $repositoryRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

if (-not $buildDirectory.StartsWith($expectedBuildDirectory, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Diretório de saída inválido: $buildDirectory"
}

$stagingDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("molecular-portable-" + [System.Guid]::NewGuid().ToString('N'))
$intermediateBuildDirectory = Join-Path $stagingDirectory 'build'
$publishDirectory = Join-Path $stagingDirectory 'publish'
[System.IO.Directory]::CreateDirectory($stagingDirectory) | Out-Null

try {
    dotnet restore $projectPath -r win-x64
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao restaurar as dependências.' }

    $publishArguments = @(
        'publish',
        $projectPath,
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', 'true',
        '--no-restore',
        '-p:PublishProfile=Portable-win-x64',
        "-p:OutputPath=$intermediateBuildDirectory",
        '-o', $publishDirectory
    )
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao publicar o executável portátil.' }

    $executables = @(Get-ChildItem -LiteralPath $publishDirectory -File -Filter '*.exe')
    if ($executables.Count -ne 1 -or $executables[0].Name -ne 'Molecular.exe') {
        throw "A publicação deveria produzir somente Molecular.exe, mas encontrou $($executables.Count) executável(is)."
    }

    if (Test-Path -LiteralPath $buildDirectory) {
        $resolvedBuildDirectory = (Resolve-Path -LiteralPath $buildDirectory).Path
        if (-not [System.StringComparer]::OrdinalIgnoreCase.Equals($resolvedBuildDirectory, $buildDirectory)) {
            throw "O diretório de build resolvido não corresponde ao destino esperado: $resolvedBuildDirectory"
        }
        Remove-Item -LiteralPath $resolvedBuildDirectory -Recurse -Force
    }

    [System.IO.Directory]::CreateDirectory($buildDirectory) | Out-Null
    Move-Item -LiteralPath $executables[0].FullName -Destination (Join-Path $buildDirectory 'Molecular.exe')

    $result = Get-Item -LiteralPath (Join-Path $buildDirectory 'Molecular.exe')
    $hash = Get-FileHash -LiteralPath $result.FullName -Algorithm SHA256
    Write-Host "Executável portátil criado: $($result.FullName)"
    Write-Host "Tamanho: $([Math]::Round($result.Length / 1MB, 1)) MB"
    Write-Host "SHA-256: $($hash.Hash)"
}
finally {
    $intermediateHost = Join-Path $repositoryRoot 'src\Molecular.App\obj\Release\singlefilehost.exe'
    if (Test-Path -LiteralPath $intermediateHost) {
        Remove-Item -LiteralPath $intermediateHost -Force
    }
    if (Test-Path -LiteralPath $stagingDirectory) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }
}
