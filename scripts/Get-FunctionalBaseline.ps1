param()
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$tracked=@(& git -c "safe.directory=$root" -C $root ls-files -- src)
if($LASTEXITCODE -ne 0){throw 'Cannot enumerate tracked source.'}
$extra=@(& git -c "safe.directory=$root" -C $root ls-files --others --exclude-standard -- src)
if($LASTEXITCODE -ne 0){throw 'Cannot enumerate new source.'}
[string[]]$paths=@($tracked+$extra | Where-Object {$_ -and $_ -cne 'src/PGMS.Desktop/BuildInfo.cs'} | Select-Object -Unique)
[Array]::Sort($paths,[StringComparer]::Ordinal)
$files=@(foreach($path in $paths){
 $full=Join-Path $root $path
 if((Get-Item -LiteralPath $full).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked source is not supported.'}
 [pscustomobject]@{Path=$path;Sha256=(Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash}
})
$canonical=($files | ForEach-Object {"$($_.Path)`t$($_.Sha256)`n"}) -join ''
$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical)))
[pscustomobject]@{SchemaVersion=1;SourceSha256=$hash;FileCount=$files.Count;Files=$files;Excluded='BuildInfo.cs milestone label only; source identity is not binary identity.'}
