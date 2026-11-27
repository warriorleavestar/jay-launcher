# Shared by test scripts that write logs: hides join credentials before a line reaches a log file.
# Room codes, invite codes, sealed offers, passphrases and replies let anyone with the log join a room,
# so the whole value is replaced with a fixed placeholder, never shortened to a prefix.

$script:RedactedPlaceholder = "<hidden>"

# Output lines from Amber.Cli whose whole value is a credential.
$script:CredentialLinePattern = '^(\s*(?:ROOM|INVITE|SEALED|PASSPHRASE|REPLY)\s).*$'
# Credential codes anywhere in a line. Case-sensitive: codes are printed in upper case, and this keeps
# ordinary words such as "Jay-launcher" or "AMBER_..." intact.
$script:CredentialCodePatterns = @(
    '\bAMB(?:-|DIRECT|REPLY)[0-9A-Za-z*~$=+/_.\-]*',
    '\bJAY[0-9A-Z]*-[0-9A-Za-z*~$=+/_.\-]+'
)
# EasyTier's own secret, which is derived from the room code and so never matches it as text. Same forms as
# NetDiagnostics.SecretConfigPattern: network_secret = "x", network_secret: Some("x"), {"network_secret":"x"}, unquoted.
$script:SecretValuePattern = '(?i)(network[_-]?secret["'']?\s*[:=]\s*(?:Some\(\s*)?["'']?)[^"''\s,;)}]+'
# The command-line form, as NetDiagnostics.SecretArgPattern.
$script:SecretArgPattern = '(?i)(--network-secret\s+)\S+'

function Hide-Credentials([string]$line, [string[]]$known = @()) {
    if ($null -eq $line) { return $line }
    foreach ($secret in $known) {
        if ($secret -and $secret.Length -ge 6) { $line = $line.Replace($secret, $script:RedactedPlaceholder) }
    }
    $line = $line -creplace $script:CredentialLinePattern, ('$1' + $script:RedactedPlaceholder)
    foreach ($pattern in $script:CredentialCodePatterns) { $line = $line -creplace $pattern, $script:RedactedPlaceholder }
    $line = $line -replace $script:SecretArgPattern, ('$1' + $script:RedactedPlaceholder)
    return $line -replace $script:SecretValuePattern, ('$1' + $script:RedactedPlaceholder)
}

# Appends a captured output file to a log, one redacted line at a time.
function Add-RedactedFile([string]$source, [string]$log, [string[]]$known = @(), [string]$indent = "  ") {
    if (-not (Test-Path $source)) { return }
    Get-Content $source -Encoding UTF8 | ForEach-Object { Add-Content -Path $log -Value ($indent + (Hide-Credentials $_ $known)) -Encoding UTF8 }
}

# Removes a folder of raw process output, retrying while just-stopped processes still hold files open.
# Returns $true when the folder is gone.
function Remove-RawOutput([string]$folder, [int]$attempts = 10) {
    for ($i = 0; $i -lt $attempts; $i++) {
        if (-not (Test-Path $folder)) { return $true }
        Remove-Item -Recurse -Force $folder -ErrorAction SilentlyContinue
        if (-not (Test-Path $folder)) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return -not (Test-Path $folder)
}
