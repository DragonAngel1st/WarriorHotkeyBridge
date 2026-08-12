<#
.SYNOPSIS
    Sends an F13-F24 keystroke, for testing the bridge without a Stream Deck.

.DESCRIPTION
    Generates a real keyboard event locally, so Windows delivers it through the same
    RegisterHotKey path a Stream Deck button uses. Because the keystroke is produced on this
    machine, it works over Chrome Remote Desktop or any other remote session - the remote
    protocol is never in the path.

    Uses keybd_event rather than [System.Windows.Forms.SendKeys], which only understands
    F1-F16 and rejects "{F17}" and above with "Keyword F17 is not valid".

.PARAMETER Number
    Function key number, 13 to 24.

.PARAMETER Force
    Required to send F13-F22. Those are bound to live trading actions; F23 and F24 are the
    safe Test and Diagnostics actions and need no confirmation.

.EXAMPLE
    .\Send-FKey.ps1 23
    Runs the full targeting pipeline and reports timing. Dispatches nothing into the page.

.EXAMPLE
    .\Send-FKey.ps1 13 -Force
    Sends whatever F13 is bound to. On the default mapping that places an order.

.NOTES
    If an elevated window has focus, Windows blocks synthetic input from this script and you
    will see "Access is denied". That is User Interface Privilege Isolation, not a fault -
    click a normal window and try again.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateRange(13, 24)]
    [int] $Number,

    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# F23 and F24 carry the non-dispatching Test and Diagnostics actions in the shipped defaults.
$safeKeys = 23, 24

if ($Number -notin $safeKeys -and -not $Force) {
    throw ("F$Number is bound to a live trading action on the default mapping. " +
           "Re-run with -Force if that is what you want, or use F23 (Test) / F24 (Diagnostics), " +
           "which dispatch nothing.")
}

if (-not ('VK.Kb' -as [type])) {
    Add-Type -Name Kb -Namespace VK -MemberDefinition @'
[DllImport("user32.dll")]
public static extern void keybd_event(byte vk, byte scan, uint flags, System.UIntPtr extra);
'@
}

# VK_F13 = 0x7C through VK_F24 = 0x87, contiguous.
$virtualKey = [byte](0x7C + ($Number - 13))
$keyEventKeyUp = 2

[VK.Kb]::keybd_event($virtualKey, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 30
[VK.Kb]::keybd_event($virtualKey, 0, $keyEventKeyUp, [UIntPtr]::Zero)

Write-Host "Sent F$Number (virtual key 0x$('{0:X2}' -f $virtualKey))."
