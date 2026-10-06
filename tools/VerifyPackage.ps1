param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [string[]]$VectorPath,
    [int[]]$ExpectedMisses,
    [string]$SymbolPackagePath,
    [string]$SourceRoot,
    [string]$ExpectedCommit,
    [switch]$VerifyHostedSources
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($VectorPath -and ($null -eq $ExpectedMisses -or $VectorPath.Count -ne $ExpectedMisses.Count)) {
    throw 'Supply an expected miss count for each corpus.'
}
if ($SymbolPackagePath -and (!$SourceRoot -or $ExpectedCommit -notmatch '^[0-9a-fA-F]{40}$')) {
    throw 'Symbol verification requires a source Git checkout and its full expected commit.'
}
if (!$SymbolPackagePath -and ($SourceRoot -or $ExpectedCommit -or $VerifyHostedSources)) {
    throw 'Supply SymbolPackagePath for source and revision verification.'
}

function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE." }
}

$packageFile = (Resolve-Path -LiteralPath $PackagePath).Path
$archive = [IO.Compression.ZipFile]::OpenRead($packageFile)
try {
    $metadataEntries = @($archive.Entries | Where-Object { $_.FullName -like '*.nuspec' })
    if ($metadataEntries.Count -ne 1) { throw 'Expected one package manifest.' }
    $reader = [IO.StreamReader]::new($metadataEntries[0].Open())
    try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    if ($manifest.package.metadata.id -ne 'Lokad.Jq') { throw 'Expected the Lokad.Jq package.' }
    if ($manifest.package.metadata.projectUrl -ne 'https://github.com/lokad/Jq' -or
        $manifest.package.metadata.repository.url -ne 'https://github.com/lokad/Jq') {
        throw 'Unexpected public repository metadata.'
    }
    if ($SymbolPackagePath -and $manifest.package.metadata.repository.commit -ne $ExpectedCommit.ToLowerInvariant()) {
        throw 'The package repository revision does not match the expected commit.'
    }
    $version = [string]$manifest.package.metadata.version
    if ($version -notmatch '^[0-9A-Za-z.+-]+$') { throw 'Invalid package version.' }
    $entries = @($archive.Entries | ForEach-Object { $_.FullName })
    foreach ($requiredEntry in @('lib/net10.0/Lokad.Jq.dll', 'lib/net10.0/Lokad.Jq.xml', 'README.md', 'CHANGELOG.md', 'LICENSE.txt', 'icon.png')) {
        if ($requiredEntry -notin $entries) { throw "Missing package entry: $requiredEntry" }
    }
    if (@($entries | Where-Object { $_ -match '(^|/)(PLAN\.md|external|tmp|tests|benchmarks|tools)(/|$)' }).Count -ne 0) {
        throw 'Unexpected development files in the package.'
    }
    $dependencies = @($manifest.package.metadata.dependencies.group.dependency | ForEach-Object { [string]$_.id })
    if ((($dependencies | Sort-Object) -join ',') -ne 'Lokad.Utf8Regex.Pcre2') {
        throw 'Unexpected production dependencies or leaked build tooling.'
    }
} finally {
    $archive.Dispose()
}

# Preserve this isolated workspace for inspection; never reuse the global package cache.
$verificationRoot = Join-Path ([IO.Path]::GetTempPath()) ('lokad-jq-package-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $verificationRoot
$project = Join-Path $verificationRoot 'PackageSmoke.csproj'
$packages = Join-Path $verificationRoot 'packages'
$configuration = Join-Path $verificationRoot 'NuGet.Config'
$template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'PackageSmoke/PackageSmoke.csproj.template') -Raw
$template.Replace('__PACKAGE_VERSION__', $version) | Set-Content -LiteralPath $project -Encoding utf8
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PackageSmoke/Program.cs') -Destination (Join-Path $verificationRoot 'Program.cs')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PackageSmoke/Symbols.cs') -Destination (Join-Path $verificationRoot 'Symbols.cs')
$feed = [Security.SecurityElement]::Escape([IO.Path]::GetDirectoryName($packageFile))
@"
<configuration><packageSources><clear />
<add key="package-under-test" value="$feed" />
<add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
</packageSources></configuration>
"@ | Set-Content -LiteralPath $configuration -Encoding utf8
$isolation = @('-p:ImportDirectoryBuildProps=false', '-p:ImportDirectoryBuildTargets=false', '-p:ManagePackageVersionsCentrally=false', "-p:RestorePackagesPath=$packages")
Invoke-DotNet (@('restore', $project, '--configfile', $configuration) + $isolation)

$hashAlgorithm = [Security.Cryptography.SHA512]::Create()
$packageStream = [IO.File]::OpenRead($packageFile)
try { $contentHash = [Convert]::ToBase64String($hashAlgorithm.ComputeHash($packageStream)) }
finally { $packageStream.Dispose(); $hashAlgorithm.Dispose() }
$lock = Get-Content -LiteralPath (Join-Path $verificationRoot 'packages.lock.json') -Raw | ConvertFrom-Json
$lockedPackage = $lock.dependencies.'net10.0'.'Lokad.Jq'
if ($lockedPackage.resolved -ne $version -or $lockedPackage.contentHash -ne $contentHash) {
    throw 'The consumer lock does not identify the packed artifact.'
}
$runtimePackages = @($lock.dependencies.'net10.0'.PSObject.Properties.Name | Sort-Object)
if (($runtimePackages -join ',') -ne 'Lokad.Jq,Lokad.Utf8Regex,Lokad.Utf8Regex.Pcre2') {
    throw 'Unexpected transitive runtime packages.'
}
foreach ($regexId in @('Lokad.Utf8Regex', 'Lokad.Utf8Regex.Pcre2')) {
    $regexPackage = $lock.dependencies.'net10.0'.$regexId
    if ($regexPackage.resolved -ne '0.3.0') { throw "Unexpected regex version: $regexId" }
    $archivePath = Join-Path $packages ($regexId.ToLowerInvariant() + '/0.3.0/' + $regexId.ToLowerInvariant() + '.0.3.0.nupkg')
    $regexArchive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        if (@($regexArchive.Entries | Where-Object { $_.FullName -match '(^|/)(runtimes|native)(/|$)|\.(so|dylib|exe)$' }).Count -ne 0) {
            throw "Unexpected native assets: $regexId"
        }
    } finally { $regexArchive.Dispose() }
}
Write-Host 'Runtime graph: Lokad.Jq -> Lokad.Utf8Regex.Pcre2 0.3.0 -> Lokad.Utf8Regex 0.3.0; no native assets.'
Invoke-DotNet (@('restore', $project, '--locked-mode', '--configfile', $configuration) + $isolation)
Invoke-DotNet (@('run', '--project', $project, '-c', 'Release', '--no-restore') + $isolation)
if ($SymbolPackagePath) {
    $symbols = (Resolve-Path -LiteralPath $SymbolPackagePath).Path
    $sources = (Resolve-Path -LiteralPath $SourceRoot).Path
    $symbolMode = if ($VerifyHostedSources) { '--symbols-hosted' } else { '--symbols' }
    Invoke-DotNet (@('run', '--project', $project, '-c', 'Release', '--no-build', '--no-restore') + $isolation + @('--', $symbolMode, $symbols, $sources, $ExpectedCommit.ToLowerInvariant()))
}
if ($VectorPath) {
    for ($index = 0; $index -lt $VectorPath.Count; $index++) {
        $vectors = (Resolve-Path -LiteralPath $VectorPath[$index]).Path
        Invoke-DotNet (@('run', '--project', $project, '-c', 'Release', '--no-build', '--no-restore') + $isolation + @('--', '--vectors', $vectors, [string]$ExpectedMisses[$index]))
    }
}
Write-Host "Verified Lokad.Jq $version from its packed artifact; isolated workspace: $verificationRoot"
