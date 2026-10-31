param([switch]$SessionsOnly, [switch]$PlansOnly, [switch]$ExportOnly, [switch]$ActivityOnly, [switch]$GlobalBackupOnly, [switch]$ColumnTypesOnly, [switch]$IndexMaintenanceOnly, [switch]$IndexExpressionsOnly, [switch]$PartitionTablesOnly, [switch]$DesktopOnly, [switch]$CascadeJournalOnly, [switch]$CascadeRoutinesOnly, [switch]$CascadeRollbackOnly, [switch]$RevokePreviewOnly, [switch]$GrantorConcurrencyOnly, [switch]$ExplicitGrantorOnly, [switch]$GrantorCatalogOnly, [switch]$DefaultProcessOnly, [switch]$DefaultDelegationOnly, [switch]$DefaultSchemasOnly, [switch]$DefaultTypesOnly, [switch]$DefaultRoutinesOnly, [switch]$DefaultSequencesOnly, [switch]$DefaultTablesOnly, [switch]$DefaultCancellationOnly, [switch]$DefaultUiOnly, [switch]$DefaultJournalOnly, [switch]$DefaultPrivilegesOnly, [switch]$RoutineBoundariesOnly, [switch]$RoutineProcessOnly, [switch]$RoutineConcurrencyOnly, [switch]$RoutinePrivilegesOnly, [string]$PostgresBin = 'C:\Program Files\PostgreSQL\17\bin', [switch]$BackupFormatsOnly, [switch]$IdentityColumnsOnly, [switch]$RestoreOwnershipOnly, [switch]$UnicodeBackupOnly, [switch]$PrivilegeJournalOnly, [switch]$MaintainPrivilegeOnly, [switch]$PrivilegeCommitOnly, [switch]$UnicodePasswordOnly, [switch]$MembershipOptionsOnly, [switch]$PrivilegeIdentityOnly, [switch]$ColumnPrivilegesOnly, [switch]$PrivilegeCancellationOnly, [switch]$EffectivePrivilegesOnly, [switch]$PrivilegeCommandsOnly, [switch]$SequencePrivilegesOnly, [switch]$CopyOnly, [switch]$AdvancedIndexesOnly, [switch]$SequencesOnly, [switch]$SequenceStateOnly, [switch]$SequenceOwnerOnly, [switch]$AccessibilityOnly, [switch]$ConnectionOnly, [switch]$StreamingOnly, [switch]$ExplorerOnly, [switch]$PropertiesOnly, [switch]$AcceptanceOnly)
$ErrorActionPreference = 'Stop'
if ((@($SessionsOnly, $PlansOnly, $ExportOnly, $ActivityOnly, $GlobalBackupOnly, $ColumnTypesOnly, $IndexMaintenanceOnly, $IndexExpressionsOnly, $PartitionTablesOnly, $CascadeJournalOnly, $CascadeRoutinesOnly, $CascadeRollbackOnly, $RevokePreviewOnly, $GrantorConcurrencyOnly, $ExplicitGrantorOnly, $GrantorCatalogOnly, $DefaultProcessOnly, $DefaultDelegationOnly, $DefaultSchemasOnly, $DefaultTypesOnly, $DefaultRoutinesOnly, $DefaultSequencesOnly, $DefaultTablesOnly, $DefaultCancellationOnly, $DefaultUiOnly, $DefaultJournalOnly, $DefaultPrivilegesOnly, $RoutineBoundariesOnly, $RoutineProcessOnly, $RoutineConcurrencyOnly, $RoutinePrivilegesOnly, $BackupFormatsOnly, $IdentityColumnsOnly, $RestoreOwnershipOnly, $UnicodeBackupOnly, $PrivilegeJournalOnly, $MaintainPrivilegeOnly, $PrivilegeCommitOnly, $UnicodePasswordOnly, $MembershipOptionsOnly, $PrivilegeIdentityOnly, $ColumnPrivilegesOnly, $PrivilegeCancellationOnly, $EffectivePrivilegesOnly, $PrivilegeCommandsOnly, $SequencePrivilegesOnly, $CopyOnly, $AdvancedIndexesOnly, $SequencesOnly, $SequenceStateOnly, $SequenceOwnerOnly, $AccessibilityOnly, $ConnectionOnly, $StreamingOnly, $ExplorerOnly, $PropertiesOnly, $AcceptanceOnly) | Where-Object { $_ }).Count -gt 1) { throw 'Choose only one focused suite.' }
if ($DesktopOnly -and ($CascadeRoutinesOnly -or $CascadeRollbackOnly -or $RevokePreviewOnly -or $GrantorConcurrencyOnly -or $ExplicitGrantorOnly -or $GrantorCatalogOnly -or $DefaultDelegationOnly -or $DefaultSchemasOnly -or $DefaultTypesOnly -or $DefaultRoutinesOnly -or $DefaultSequencesOnly -or $DefaultTablesOnly -or $DefaultPrivilegesOnly -or $RoutineConcurrencyOnly -or $PrivilegeIdentityOnly -or $AcceptanceOnly -or $UnicodeBackupOnly)) { throw 'The selected focused suite has no desktop checks.' }
$projectRoot = Split-Path $PSScriptRoot -Parent
$testRoot = Join-Path $projectRoot 'artifacts\postgres-test'
$pgCtl = Join-Path $PostgresBin 'pg_ctl.exe'
$initDb = Join-Path $PostgresBin 'initdb.exe'
if (!(Test-Path $initDb)) { throw "PostgreSQL tools not found: $PostgresBin" }
# Data directories are not compatible across major versions: keep one isolated cluster per major.
$pgMajor = (Get-Item $initDb).VersionInfo.ProductMajorPart
$dataPath = Join-Path $testRoot $(if ($pgMajor -eq 17) { 'data' } else { "data-pg$pgMajor" })
$serverLog = Join-Path $testRoot $(if ($pgMajor -eq 17) { 'server.log' } else { "server-pg$pgMajor.log" })
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
if (!(Test-Path (Join-Path $dataPath 'PG_VERSION'))) {
    & $initDb -D $dataPath -U pgms_test --auth=trust --encoding=UTF8 --locale=C
    if ($LASTEXITCODE -ne 0) { throw 'initdb failed' }
}
# Give the isolated cluster a local-only test certificate so password changes can be exercised over TLS.
$rsa = [System.Security.Cryptography.RSA]::Create(2048)
try {
    $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=127.0.0.1', $rsa, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $san = [System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
    $san.AddIpAddress([System.Net.IPAddress]::Loopback)
    $request.CertificateExtensions.Add($san.Build())
    $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddDays(30))
    $certPath = Join-Path $dataPath 'server.crt'
    $keyPath = Join-Path $dataPath 'server.key'
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($certPath, $certificate.ExportCertificatePem(), $utf8NoBom)
    [System.IO.File]::WriteAllText($keyPath, $rsa.ExportPkcs8PrivateKeyPem(), $utf8NoBom)
} finally { if ($certificate) { $certificate.Dispose() }; $rsa.Dispose() }
# Fail if anything already uses the test port; never attach to an unrelated server.
$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 55439)
try { $listener.Start() } finally { $listener.Stop() }
try {
    $pgOptions = '-h 127.0.0.1 -p 55439 -c ssl=on'
    # The server inherits pg_ctl's handles: keep them on files so a redirected caller never blocks.
    $start = Start-Process -FilePath $pgCtl -ArgumentList @('-D', "`"$dataPath`"", '-l', "`"$serverLog`"", '-o', "`"$pgOptions`"", '-w', 'start') -NoNewWindow -PassThru -RedirectStandardOutput (Join-Path $testRoot 'pg_ctl-start.log') -RedirectStandardError (Join-Path $testRoot 'pg_ctl-start.err')
    $null = $start.Handle
    $start.WaitForExit()
    if ($start.ExitCode -ne 0) { throw 'Test server failed to start' }
    Push-Location $projectRoot
    try {
        $env:PGMS_TEST_POSTGRES_BIN = $PostgresBin
        $env:PGMS_TEST_POSTGRES_DATA = $dataPath
        $testArgs = @(); if ($AdvancedIndexesOnly) { $testArgs = @("--", "--advanced-index") }; if ($SequencesOnly) { $testArgs = @("--", "--sequences") }
        if ($SessionsOnly) { $testArgs = @("--", "--sessions") }
        if ($PlansOnly) { $testArgs = @("--", "--plans") }
        if ($ExportOnly) { $testArgs = @("--", "--export-roundtrip") }
        if ($ActivityOnly) { $testArgs = @("--", "--activity") }
        if ($GlobalBackupOnly) { $testArgs = @("--", "--global-backup") }
        if ($ColumnTypesOnly) { $testArgs = @("--", "--column-types") }
        if ($IndexMaintenanceOnly) { $testArgs = @("--", "--index-maintenance") }
        if ($IndexExpressionsOnly) { $testArgs = @("--", "--index-expressions") }
        if ($PartitionTablesOnly) { $testArgs = @("--", "--partition-tables") }
        if ($CascadeJournalOnly) { $testArgs = @("--", "--cascade-journal") }
        if ($CascadeRoutinesOnly) { $testArgs = @("--", "--cascade-routines") }
        if ($CascadeRollbackOnly) { $testArgs = @("--", "--cascade-rollback") }
        if ($RevokePreviewOnly) { $testArgs = @("--", "--revoke-preview") }
        if ($GrantorConcurrencyOnly) { $testArgs = @("--", "--grantor-concurrency") }
        if ($ExplicitGrantorOnly) { $testArgs = @("--", "--explicit-grantor") }
        if ($GrantorCatalogOnly) { $testArgs = @("--", "--grantor-catalog") }
        if ($DefaultProcessOnly) { $testArgs = @("--", "--default-process") }
        if ($DefaultDelegationOnly) { $testArgs = @("--", "--default-delegation") }
        if ($DefaultSchemasOnly) { $testArgs = @("--", "--default-schemas") }
        if ($DefaultTypesOnly) { $testArgs = @("--", "--default-types") }
        if ($DefaultRoutinesOnly) { $testArgs = @("--", "--default-routines") }
        if ($DefaultSequencesOnly) { $testArgs = @("--", "--default-sequences") }
        if ($DefaultTablesOnly) { $testArgs = @("--", "--default-tables") }
        if ($DefaultCancellationOnly) { $testArgs = @("--", "--default-cancel") }
        if ($DefaultUiOnly) { $testArgs = @("--", "--default-ui") }
        if ($DefaultJournalOnly) { $testArgs = @("--", "--default-journal") }
        if ($DefaultPrivilegesOnly) { $testArgs = @("--", "--default-privileges") }
        if ($RoutineBoundariesOnly) { $testArgs = @("--", "--routine-boundaries") }
        if ($RoutineProcessOnly) { $testArgs = @("--", "--routine-process") }
        if ($RoutineConcurrencyOnly) { $testArgs = @("--", "--routine-concurrency") }
        if ($RoutinePrivilegesOnly) { $testArgs = @("--", "--routine-privileges") }
        if ($BackupFormatsOnly) { $testArgs = @("--", "--backup-formats") }
        if ($IdentityColumnsOnly) { $testArgs = @("--", "--identity-columns") }
        if ($RestoreOwnershipOnly) { $testArgs = @("--", "--restore-ownership") }
        if ($UnicodeBackupOnly) { $testArgs = @("--", "--unicode-backup") }
        if ($PrivilegeJournalOnly) { $testArgs = @("--", "--privilege-journal") }
        if ($MaintainPrivilegeOnly) { $testArgs = @("--", "--maintain-privilege") }
        if ($PrivilegeCommitOnly) { $testArgs = @("--", "--privilege-commit") }
        if ($UnicodePasswordOnly) { $testArgs = @("--", "--unicode-password") }
        if ($PrivilegeIdentityOnly) { $testArgs = @("--", "--privilege-identity") }
        if ($MembershipOptionsOnly) { $testArgs = @("--", "--membership-options") }
        if ($ColumnPrivilegesOnly) { $testArgs = @("--", "--column-privileges") }
        if ($PrivilegeCancellationOnly) { $testArgs = @("--", "--privilege-cancellation") }
        if ($EffectivePrivilegesOnly) { $testArgs = @("--", "--effective-privileges") }
        if ($PrivilegeCommandsOnly) { $testArgs = @("--", "--privilege-commands") }
        if ($SequencePrivilegesOnly) { $testArgs = @("--", "--sequence-privileges") }
        if ($CopyOnly) { $testArgs = @("--", "--copy") }
        if ($SequenceOwnerOnly) { $testArgs = @("--", "--sequence-owner") }
        if ($SequenceStateOnly) { $testArgs = @("--", "--sequence-state") }
        if ($AccessibilityOnly) { $testArgs = @("--", "--accessibility") }
        if ($StreamingOnly) { $testArgs = @("--", "--streaming") }
        if ($ConnectionOnly) { $testArgs = @("--", "--connection") }
        if ($AcceptanceOnly) { $testArgs = @("--", "--acceptance") }
        if ($PropertiesOnly) { $testArgs = @("--", "--properties") }
        if ($ExplorerOnly) { $testArgs = @("--", "--explorer") }
        if (!$CascadeJournalOnly -and !$DefaultProcessOnly -and !$DefaultCancellationOnly -and !$DefaultUiOnly -and !$DefaultJournalOnly -and !$RoutineBoundariesOnly -and !$RoutineProcessOnly -and !$DesktopOnly -and !$PrivilegeJournalOnly -and !$CopyOnly -and !$AccessibilityOnly -and !$ConnectionOnly -and !$ExplorerOnly) {
            dotnet run --project tests\PGMS.IntegrationTests\PGMS.IntegrationTests.csproj -c Release @testArgs
            if ($LASTEXITCODE -ne 0) { throw 'Integration checks failed' }
        }
        if (!$CascadeRoutinesOnly -and !$CascadeRollbackOnly -and !$RevokePreviewOnly -and !$GrantorConcurrencyOnly -and !$ExplicitGrantorOnly -and !$GrantorCatalogOnly -and !$DefaultDelegationOnly -and !$DefaultSchemasOnly -and !$DefaultTypesOnly -and !$DefaultRoutinesOnly -and !$DefaultSequencesOnly -and !$DefaultTablesOnly -and !$DefaultPrivilegesOnly -and !$RoutineConcurrencyOnly -and !$AcceptanceOnly -and !$PrivilegeIdentityOnly -and !$UnicodeBackupOnly) {
        dotnet run --project tests\PGMS.DesktopTests\PGMS.DesktopTests.csproj -c Release @testArgs
        if ($LASTEXITCODE -ne 0) { throw 'Desktop checks failed' }
        }
    } finally { Pop-Location }
} finally {
    & $pgCtl -D $dataPath -m fast -w stop
}
