[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Assembly,
    [Parameter(Mandatory)][string]$Output
)
$ErrorActionPreference = 'Stop'
$Output = [IO.Path]::GetFullPath($Output)
$manifest = Join-Path $PSScriptRoot '../.config/dotnet-tools.json'
dotnet tool restore --tool-manifest $manifest
if ($LASTEXITCODE) { throw 'Could not restore the pinned obfuscator.' }

# Only types explicitly annotated in source are eligible. XAML, interop and recording contracts keep their names.
$config = [xml]'<Obfuscator><Var name="MarkedOnly" value="true"/><Var name="HideStrings" value="false"/><Var name="RenameProperties" value="false"/><Var name="RenameEvents" value="false"/></Obfuscator>'
function Add-Element([string]$name, [hashtable]$attributes) {
    $node = $config.CreateElement($name)
    foreach ($key in $attributes.Keys) { $node.SetAttribute($key, $attributes[$key]) }
    [void]$config.DocumentElement.AppendChild($node)
}
Add-Element 'Var' @{ name = 'InPath'; value = [IO.Path]::GetDirectoryName($Assembly) }
Add-Element 'Var' @{ name = 'OutPath'; value = $Output }
foreach ($dir in Get-Content -LiteralPath (Join-Path $Output 'references.txt') -Encoding UTF8) {
    if ([IO.Directory]::Exists($dir)) { Add-Element 'AssemblySearchPath' @{ path = $dir } }
}
Add-Element 'Module' @{ file = $Assembly }
$configPath = Join-Path $Output 'obfuscar.xml'
$config.Save($configPath)

dotnet tool run obfuscar.console -- $configPath
if ($LASTEXITCODE) { throw 'Obfuscation failed.' }
$protected = Join-Path $Output ([IO.Path]::GetFileName($Assembly))
if (!(Test-Path -LiteralPath $protected -PathType Leaf)) { throw 'Protected assembly is missing.' }
$map = [IO.File]::ReadAllText((Join-Path $Output 'Mapping.txt'))
if ($map -notmatch '(?m)^\[[^\]]+\]Pulse\.[^\s]+ -> \[[^\]]+\][^\s]+') {
    throw 'The rename map contains no protected application types.'
}
