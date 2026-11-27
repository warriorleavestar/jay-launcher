<#
Amber 联机节点（Windows，适合一直开着的电脑或 Windows 服务器）
Amber relay node for Windows (an always-on PC or Windows Server)

下载 Jay 加固版 EasyTier 的固定版本并校验 SHA-256（源码 https://github.com/Yumao-Feather/jay-easytier），注册一个开机运行的计划任务（只做中继，不建虚拟网卡），
在 Windows 防火墙放行 TCP/UDP 端口，最后打印节点地址和 Amber 节点分享码。
用管理员 PowerShell 运行 / run in an elevated PowerShell:
  powershell -ExecutionPolicy Bypass -File install-windows.ps1 [-Port 11010] [-Relay amber|all] [-NodeName 名字] [-PublicHost 公网IP]
卸载 / uninstall:
  powershell -ExecutionPolicy Bypass -File install-windows.ps1 -Uninstall
#>
param(
    [int]$Port = 11010,
    [ValidateSet("amber", "all")][string]$Relay = "amber",
    [string]$NodeName = "",
    [string]$PublicHost = "",
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"
# Same pins as src/Amber.Core/Multiplayer/EasyTier.cs.
$Version = "2.6.4"
$Tag = "jay-v2.6.4-3"
$Commit = "9407746b1ce1"
$Sha256 = "cdb113651de1f37244f16d405447806a76cd9012780a4e519ed7eba062306888"
$Dir = Join-Path $env:ProgramData "AmberNode"
$Task = "Amber relay node"
$Rule = "Amber relay node"

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw "请用管理员身份运行 PowerShell / run as administrator" }

# The scheduled task runs this folder's exe at every boot. ProgramData lets ordinary users create folders and write files,
# so the folder is locked to SYSTEM and Administrators (LOCAL SERVICE may only read and run it), whatever was in it is
# removed, and a folder that someone else created (or a link to somewhere else) stops the install.
function Initialize-NodeDir {
    $inherit = [Security.AccessControl.InheritanceFlags]"ContainerInherit, ObjectInherit"
    $none = [Security.AccessControl.PropagationFlags]::None
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($rule in @(@("S-1-5-18", "FullControl"), @("S-1-5-32-544", "FullControl"), @("S-1-5-19", "ReadAndExecute"))) {
        $sid = New-Object Security.Principal.SecurityIdentifier $rule[0]
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid, $rule[1], $inherit, $none, "Allow")))
    }
    if (Test-Path -LiteralPath $Dir) {
        # Reinstalling: the running node holds its exe open.
        Stop-ScheduledTask -TaskName $Task -ErrorAction SilentlyContinue
        Get-Process easytier-core -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($Dir, [StringComparison]::OrdinalIgnoreCase) } | Stop-Process -Force -PassThru | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
    }
    else {
        # Created with the locked ACL in one step, so there is no moment when users can write into it.
        if ($PSVersionTable.PSEdition -eq "Core") { [IO.FileSystemAclExtensions]::Create((New-Object IO.DirectoryInfo $Dir), $acl) }
        else { [IO.Directory]::CreateDirectory($Dir, $acl) | Out-Null }
    }
    # Checked even when it was just created: someone may have made it first.
    $item = Get-Item -LiteralPath $Dir -Force
    if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "$Dir 不是普通文件夹，已停止；请检查后删掉它再装 / $Dir is not a plain folder; check it, remove it and run again"
    }
    $trusted = @("S-1-5-18", "S-1-5-32-544", [Security.Principal.WindowsIdentity]::GetCurrent().User.Value)
    $owner = (Get-Acl -LiteralPath $Dir).GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($trusted -notcontains $owner) {
        throw "$Dir 不是管理员建的（所有者 $owner），已停止；请检查后删掉它再装 / $Dir was not created by an administrator (owner $owner); check it, remove it and run again"
    }
    Set-Acl -LiteralPath $Dir -AclObject $acl
    Get-ChildItem -LiteralPath $Dir -Force | Remove-Item -Recurse -Force
}

if ($Uninstall) {
    Unregister-ScheduledTask -TaskName $Task -Confirm:$false -ErrorAction SilentlyContinue
    Get-Process easytier-core -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($Dir, [StringComparison]::OrdinalIgnoreCase) } | Stop-Process -Force
    Get-NetFirewallRule -DisplayName $Rule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    Remove-Item -Recurse -Force $Dir -ErrorAction SilentlyContinue
    Write-Host "已卸载 / uninstalled"
    return
}

if ($Port -lt 1024 -or $Port -gt 65535) { throw "Port 要在 1024-65535 之间" }
$asset = "easytier-windows-x86_64-v$Version-jay-$Commit.zip"
$url = "https://github.com/Yumao-Feather/jay-easytier/releases/download/$Tag/$asset"
$tmp = Join-Path $env:TEMP ("amber-node-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $tmp | Out-Null
try {
    Write-Host "下载 / downloading $url"
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile (Join-Path $tmp $asset)
    $hash = (Get-FileHash -Algorithm SHA256 (Join-Path $tmp $asset)).Hash.ToLowerInvariant()
    if ($hash -ne $Sha256) { throw "SHA-256 不对，已停止 / checksum mismatch: $hash" }
    Expand-Archive -Force (Join-Path $tmp $asset) (Join-Path $tmp "x")
    Initialize-NodeDir
    Get-ChildItem -Recurse (Join-Path $tmp "x") -Include "easytier-core.exe", "easytier-cli.exe", "*.dll" | Copy-Item -Destination $Dir -Force
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}

$bytes = New-Object byte[] 18
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$secret = -join ($bytes | ForEach-Object { $_.ToString("x2") })
$net = "amber-node-" + $secret.Substring(0, 12)
# Jay names invite rooms, their lobbies and extra paths jay-…, and rooms opened with a room code amber-….
# The list is one argument: EasyTier splits it at spaces.
$whitelist = if ($Relay -eq "amber") { "jay-* amber-*" } else { "*" }
$arguments = "--network-name $net --network-secret $secret --no-tun --listeners tcp://0.0.0.0:$Port udp://0.0.0.0:$Port " +
             "--relay-network-whitelist `"$whitelist`" --hostname amber-node --rpc-portal 127.0.0.1:15888"

# 用权限最低的内置账户 LOCAL SERVICE 运行，开机启动，挂了自动重启。
$action = New-ScheduledTaskAction -Execute (Join-Path $Dir "easytier-core.exe") -Argument $arguments -WorkingDirectory $Dir
$trigger = New-ScheduledTaskTrigger -AtStartup
$principal = New-ScheduledTaskPrincipal -UserId "NT AUTHORITY\LOCAL SERVICE" -LogonType ServiceAccount
$settings = New-ScheduledTaskSettingsSet -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName $Task -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null

Get-NetFirewallRule -DisplayName $Rule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName $Rule -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port -Program (Join-Path $Dir "easytier-core.exe") | Out-Null
New-NetFirewallRule -DisplayName $Rule -Direction Inbound -Action Allow -Protocol UDP -LocalPort $Port -Program (Join-Path $Dir "easytier-core.exe") | Out-Null
Start-ScheduledTask -TaskName $Task

if (-not $PublicHost) {
    $PublicHost = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.PrefixOrigin -ne "WellKnown" -and $_.IPAddress -notlike "169.254.*" } | Select-Object -First 1).IPAddress
    Write-Host "注意：猜到的地址是 $PublicHost；在家里的电脑上一般要用路由器的公网 IP，并在路由器上做端口转发。可以用 -PublicHost 指定。"
}
# A share code names a node by domain only, never by IP (Jay refuses plain codes with an IP in them).
$parsedIp = $null
$code = $null
if (-not [System.Net.IPAddress]::TryParse($PublicHost.Trim('[', ']'), [ref]$parsedIp)) {
    $json = '{"v":1'
    if ($NodeName) { $json += ',"name":' + (ConvertTo-Json $NodeName) }
    $json += ',"peers":["tcp://' + $PublicHost + ':' + $Port + '","udp://' + $PublicHost + ':' + $Port + '"]}'
    $code = "AMBNODE-" + ([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json)).TrimEnd("=").Replace("+", "-").Replace("/", "_"))
}

Write-Host ""
Write-Host "节点已运行 / node is running（计划任务 / scheduled task: $Task）"
Write-Host "节点地址 / node address:  tcp://${PublicHost}:$Port   udp://${PublicHost}:$Port"
if ($code) {
    Write-Host "Jay 节点分享码 / share code:"
    Write-Host $code
} else {
    Write-Host "这个节点用的是 IP 地址，分享码不能直接写 IP。在 Jay 联机设置的“中转服务器”里添加上面的地址，再点【导出】，Jay 会生成加密的分享码和口令。有域名时用 -PublicHost <域名> 重新运行。"
    Write-Host "This node goes by an IP address, and share codes never carry one. Add the address above in Jay (Multiplayer settings, relay servers) and press Export: Jay makes a sealed code and a passphrase. With a domain, re-run with -PublicHost <domain>."
}
