# Produces a new per-title snapshot. Sources run from lowest to highest priority.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Sources,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$BackupDirectory = (Join-Path 'F:/UWPDumps' ('catalog-reconcile-' + [Guid]::NewGuid().ToString('N')))
)
$ErrorActionPreference = 'Stop'
$taskArguments = @((Join-Path $PSScriptRoot 'event_catalog.py'), 'reconcile', $Sources[0], $OutputDirectory, '--backup-dir', $BackupDirectory)
if ($Sources.Count -gt 1) { $taskArguments += @('--sources') + $Sources[1..($Sources.Count - 1)] }
& python @taskArguments
if ($LASTEXITCODE -ne 0) { throw 'Catalog reconciliation failed; source directories were not modified.' }
