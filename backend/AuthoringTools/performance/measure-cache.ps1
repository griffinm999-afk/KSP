param([string]$SessionPath = 'C:\Users\griff\.codex\sessions\2026\10\01\rollout-2026-10-01T14-26-27-01a0f8b7-c2b2-7971-863b-e431e162e0a0.jsonl')
$latest = $null
Get-Content -LiteralPath $SessionPath | ForEach-Object {
    try {
        $record = $_ | ConvertFrom-Json
        if ($record.type -eq 'event_msg' -and $record.payload.type -eq 'token_count' -and $null -ne $record.payload.info) { $latest = $record }
    } catch {}
}
if ($null -eq $latest) { throw 'No token usage available.' }
$usage = $latest.payload.info.total_token_usage
$reportPath = Join-Path $PSScriptRoot '..\outputs\cache-usage.csv'
$previous = if (Test-Path -LiteralPath $reportPath) { @(Import-Csv -LiteralPath $reportPath) | Select-Object -Last 1 } else { $null }
$deltaInput = if ($previous) { $usage.input_tokens - [long]$previous.InputTokens } else { $usage.input_tokens }
$deltaCached = if ($previous) { $usage.cached_input_tokens - [long]$previous.CachedInputTokens } else { $usage.cached_input_tokens }
$snapshot = [pscustomobject]@{
    RecordedAtUTC = $latest.timestamp
    InputTokens = $usage.input_tokens
    CachedInputTokens = $usage.cached_input_tokens
    CachedPercent = [math]::Round(100 * $usage.cached_input_tokens / $usage.input_tokens, 2)
    InputSincePrevious = $deltaInput
    CachedSincePrevious = $deltaCached
    CachedPercentSincePrevious = if ($deltaInput -gt 0) { [math]::Round(100 * $deltaCached / $deltaInput, 2) } else { $null }
    OutputTokens = $usage.output_tokens
}
if (-not $previous -or $previous.RecordedAtUTC -ne $snapshot.RecordedAtUTC) { $snapshot | Export-Csv -LiteralPath $reportPath -NoTypeInformation -Append }
$snapshot | ConvertTo-Json -Compress
