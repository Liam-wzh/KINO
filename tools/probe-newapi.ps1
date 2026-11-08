param(
    [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$BaseUrl,
    [string]$ExecutablePath,
    [switch]$UseSavedCredentials
)

$ErrorActionPreference = 'Stop'
# Run against the same .NET Framework networking and DPAPI code as the app.
if ($PSVersionTable.PSEdition -ne 'Desktop') {
    $desktopPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $desktopArguments = @('-NoProfile', '-ExecutionPolicy', 'RemoteSigned', '-File', $PSCommandPath, '-BaseUrl', $BaseUrl)
    if ($ExecutablePath) { $desktopArguments += @('-ExecutablePath', $ExecutablePath) }
    if ($UseSavedCredentials) { $desktopArguments += '-UseSavedCredentials' }
    & $desktopPowerShell @desktopArguments
    exit $LASTEXITCODE
}

if ([string]::IsNullOrEmpty($ExecutablePath)) {
    $ExecutablePath = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\Kino\bin\Release\net48\KINO.exe'
}
$ExecutablePath = [System.IO.Path]::GetFullPath($ExecutablePath)
if (-not (Test-Path -LiteralPath $ExecutablePath)) { throw 'Build the Release executable first.' }
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor [System.Net.SecurityProtocolType]::Tls12
[System.Reflection.Assembly]::LoadFrom($ExecutablePath) | Out-Null

$transport = $null
try {
    $publicSettings = New-Object Kino.Models.AppSettings
    $publicSettings.Provider = [Kino.Models.ProviderKind]::NewApi
    $publicSettings.BaseUrl = $BaseUrl
    # Validate the URL using the app; this placeholder is never sent.
    $publicSettings.Token = 'public-probe-placeholder'
    $publicSettings.Normalize()
    $validation = $publicSettings.ValidateConnection()
    if ($null -ne $validation) { throw 'Provide a valid HTTPS site root.' }
    $expectedEndpoint = [Kino.Services.ApiTransport]::Endpoint($publicSettings, 'api/status')
    $transport = New-Object Kino.Services.ApiTransport -ArgumentList @($null)
    $status = $transport.GetAsync($publicSettings, 'api/status', [System.Threading.CancellationToken]::None, $false).GetAwaiter().GetResult()
    $data = $status.Data
    [pscustomobject]@{
        Mode = 'Public status only'
        Site = $expectedEndpoint.GetLeftPart([System.UriPartial]::Authority)
        SystemName = [string]$data['system_name']
        Version = [string]$data['version']
        QuotaPerUnit = [string]$data['quota_per_unit']
        Currency = [string]$data['quota_display_type']
        DataExportEnabled = [string]$data['enable_data_export']
        RoundTripMs = [Math]::Round($status.ElapsedMs)
    } | ConvertTo-Json

    if ($UseSavedCredentials) {
        $settingsFile = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'KINO\settings.json'
        $store = New-Object Kino.Services.SettingsStore -ArgumentList @($settingsFile)
        $settings = $store.Load()
        if ($store.LoadWarning -or $settings.Provider -ne [Kino.Models.ProviderKind]::NewApi -or $settings.ValidateConnection()) {
            [pscustomobject]@{
                SettingsFound = Test-Path -LiteralPath $store.FilePath
                IsNewApi = $settings.Provider -eq [Kino.Models.ProviderKind]::NewApi
                HasToken = -not [string]::IsNullOrWhiteSpace($settings.Token)
                HasLoadWarning = -not [string]::IsNullOrEmpty($store.LoadWarning)
                ValidationPassed = $null -eq $settings.ValidateConnection()
            } | ConvertTo-Json
            throw 'Save valid New API credentials in the app before running this mode.'
        }
        $savedEndpoint = [Kino.Services.ApiTransport]::Endpoint($settings, 'api/status')
        if ($savedEndpoint.AbsoluteUri -ne $expectedEndpoint.AbsoluteUri) {
            throw 'The saved site does not match BaseUrl; no authenticated request was sent.'
        }
        $client = New-Object Kino.Services.UsageClient -ArgumentList @($transport)
        $snapshot = $client.FetchAsync($settings, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
        # Print field availability only. Never serialize a snapshot or credentials.
        [pscustomobject]@{
            Mode = 'Saved account read-only check'
            Connected = $snapshot.Connected
            BalanceAvailable = $null -ne $snapshot.Balance
            TodaySpendAvailable = $null -ne $snapshot.TodaySpend
            TodayTokensAvailable = $null -ne $snapshot.TodayTokens
            TodayRequestsAvailable = $null -ne $snapshot.TodayRequests
            HourlyPointCount = $snapshot.History.Count
            ModelCount = $snapshot.Models.Count
            RoundTripMs = [Math]::Round($snapshot.LatencyMs)
            Note = $snapshot.Note
        } | ConvertTo-Json
    }
} catch {
    $failure = $_.Exception.GetBaseException()
    if ($failure -is [Kino.Services.ApiException]) {
        [pscustomobject]@{
            HttpStatus = $failure.Status
            KnownErrorCode = $failure.Code
            LooksLikeModelApiKey = $null -ne $settings -and $settings.Token.StartsWith('sk-', [StringComparison]::OrdinalIgnoreCase)
            LooksLikePersonalAccessToken = $null -ne $settings -and $settings.Token.StartsWith('nap_', [StringComparison]::Ordinal)
        } | ConvertTo-Json
    }
    if ($failure -is [Kino.Services.ApiException] -or $failure -is [System.Management.Automation.RuntimeException]) {
        Write-Error $failure.Message -ErrorAction Continue
    } else {
        Write-Error ('Probe failed: ' + $failure.GetType().Name) -ErrorAction Continue
    }
    exit 1
} finally {
    if ($null -ne $transport) { $transport.Dispose() }
}
