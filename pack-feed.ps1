# Packs the V12 stack into ./feed so anything using <PackageReference>
# can float to the newest build ("latest from the one repository").
#
#   .\pack-feed.ps1
#
# Package IDs: Finite.V12 (declared in V12.csproj), V12.Basic, V12.Monogame.
# Feed usage:  <PackageReference Include="Finite.V12" Version="*" />
#   Version="*"  -> newest package in ./feed at every restore.
#
# Two steps per project on purpose:
#   1. dotnet build  (generates restore + compile)
#   2. dotnet pack --no-build --no-restore
# A bare `dotnet pack` skips producing the Release assembly here (NU5026).
# We stamp PackageVersion, not Version: -p:Version also feeds AssemblyVersion
# (parts must be <= 65535, so yyMMdd-style numbers break the C# compiler),
# and NuGet's own version segments are signed 32-bit, so a single yyMMddHHmm
# segment overflows and silently fails restore - hence yyMMdd.HHmm.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$feed = Join-Path $root "feed"
$ver = "1.0." + (Get-Date -Format "yyMMdd.HHmm")

# Full dependency closure - nuspecs declare ProjectReferences as package
# dependencies, so every referenced project must land in the feed too.
# One global PackageVersion keeps the dependency versions aligned.
$projects = @(
    "libs\V12.Monogame\libs\V12.Basic\libs\Contract\Contract.Compiler.Abstractions\Contract.Compiler.Abstractions.csproj",
    "libs\V12.Monogame\libs\V12.Basic\libs\Contract\Contract.Compiler\Contract.Compiler.csproj",
    "libs\V12.Monogame\libs\V12.Basic\libs\Contract\libs\Objekt-RT\src\ObjectRT.Abstractions\ObjectRT.Abstractions.csproj",
    "libs\V12.Monogame\libs\V12.Basic\libs\Contract\libs\Objekt-RT\src\ObjectRT.VM\ObjectRT.VM.csproj",
    "libs\V12.Monogame\libs\V12.Basic\libs\Contract\libs\Objekt-RT\src\ObjectRT.Runtime\ObjectRT.Runtime.csproj",
    "libs\V12.Monogame\libs\V12.Basic\libs\Contract\libs\ObjektRT.Core\ObjektRT.Core.csproj",
    "libs\V12.Monogame\libs\V12.Basic\libs\Contract\libs\ObjektRT.Stdlib\ObjektRT.Stdlib.csproj",
    "libs\V12.Monogame\libs\V12.Basic\libs\Contract\Contract.Runtime\Contract.Runtime.csproj",
    "libs\V12.Monogame\libs\V12.Basic\libs\V12\V12.csproj",
    "libs\V12.Monogame\libs\V12.Basic\V12.Basic.csproj",
    "libs\V12.Monogame\libs\V12.Monogame\V12.Monogame\V12.Monogame.csproj"
)

foreach ($p in $projects) {
    $full = Join-Path $root $p
    if (-not (Test-Path $full)) { Write-Warning "missing project: $p"; continue }

    dotnet build $full -c Release "-p:PackageVersion=$ver" --nologo
    if ($LASTEXITCODE -ne 0) { throw "build failed: $p" }

    dotnet pack $full -c Release --no-build --no-restore "-p:PackageVersion=$ver" -o $feed --nologo
    if ($LASTEXITCODE -ne 0) { throw "pack failed: $p" }
}

Write-Host "Packed $ver into $feed"

