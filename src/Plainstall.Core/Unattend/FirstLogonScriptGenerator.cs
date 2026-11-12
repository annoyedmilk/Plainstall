using System.Globalization;
using System.Text;
using System.Text.Json;
using Plainstall.Core.Catalog;
using Plainstall.Core.Pipeline;

namespace Plainstall.Core.Unattend;

/// <summary>
///     One generated Windows PowerShell 5.1 script and where it belongs inside the Windows image.
/// </summary>
/// <param name="ImageRelativePath">
///     Path relative to the root of the mounted image, for example
///     <c>Windows\Setup\Scripts\Plainstall\User.ps1</c>.
/// </param>
/// <param name="Text">The script source, with CRLF line ends. Always 7-bit ASCII.</param>
public sealed record GeneratedScript(string ImageRelativePath, string Text)
{
    /// <summary>
    ///     The bytes to write into the image: UTF-8 with a byte order mark.
    ///     <para>
    ///         Without a byte order mark, Windows PowerShell 5.1 reads a script in the ANSI code page of the PC.
    ///         That code page is different from country to country. With the mark, it reads UTF-8. The generator
    ///         also writes each character outside printable ASCII as its code point (see
    ///         <see cref="PowerShellText.Literal" />). Thus the text is plain ASCII, and it reads the same if a tool
    ///         removes the mark later. The mark stays: it tells editors and future template text the encoding.
    ///     </para>
    /// </summary>
    /// <exception cref="PlainstallException">Internal: the text is not ASCII, which would be a bug in the generator.</exception>
    public byte[] ToBytes()
    {
        if (Text.Any(c => c > 0x7E || (c < 0x20 && c is not ('\r' or '\n'))))
            throw new PlainstallException(ErrorKind.Internal,
                $"The generated script {ImageRelativePath} contains a character outside printable ASCII. This is a bug in Plainstall.");
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.ASCII.GetBytes(Text)];
    }
}

/// <summary>
///     The two first sign-in scripts. A null member means there is nothing to do for that scope, and no file is
///     written.
/// </summary>
public sealed record FirstLogonScripts(GeneratedScript? Machine, GeneratedScript? User);

/// <summary>
///     Generates the scripts that do on the installed PC the work that cannot be done inside the image.
///     <list type="bullet">
///         <item>
///             <b>Machine.ps1</b> runs once, elevated, at the first sign-in of the first account.
///             <c>FirstLogonCommands</c> in the answer file starts it. It holds the machine-scope
///             <c>firstLogon.command</c> actions and the winget installs.
///         </item>
///         <item>
///             <b>User.ps1</b> runs once for each new account, as that user and without elevation. A
///             <c>RunOnce</c> value in the Default user's registry hive starts it. Each new account gets that value.
///             It holds the <c>firstLogon.registry</c> actions and the user-scope commands.
///         </item>
///     </list>
///     <para>
///         <b>Rules for both scripts</b>
///     </para>
///     <list type="bullet">
///         <item>
///             They never restart the PC and never sign the user out. Windows starts both at the same time.
///             A restart from one script would stop the other one before it finishes. Another tool had this fault.
///         </item>
///         <item>
///             They download nothing. Only winget uses the network, for the package ids that the user listed.
///             A script that downloads and runs files at first sign-in cannot check what it got.
///         </item>
///         <item>Each action runs in its own try/catch, and the log shows its result. One failure never stops the rest.</item>
///         <item>
///             They run once. The last step creates an empty marker file, also when some actions failed. The start
///             of the script checks for it. A test harness also uses the marker to see that the work is done.
///         </item>
///         <item>They never wait for the user. Nothing asks a question or needs a key press.</item>
///     </list>
///     <para>
///         <b>Data is never code</b>
///     </para>
///     <para>
///         Registry keys, value names, value data, titles and winget ids come from the catalog and the profile.
///         They go into the script only through <see cref="PowerShellText.Literal" />, as string values. Titles go
///         into strings, never into comments, because a line break ends a comment. The registry work uses the
///         .NET registry classes, not the PowerShell registry provider. Thus a key name is never read as a path
///         with wildcards. winget ids and Store ids must match the patterns of the profile schema before they are
///         written. The script checks the patterns again before it uses an id. The pattern that an id matches
///         sets its winget source: msstore for a Store id, winget for all other ids.
///     </para>
///     <para>
///         <c>firstLogon.command</c> is the one place where catalog text is code on purpose. But the text is not
///         pasted into the file. It is written as a string. Inside the try/catch of that action,
///         <c>[scriptblock]::Create</c> makes a script block from it. Thus a command with a syntax error or an
///         unbalanced brace fails alone, and the log shows its error. Without this, PowerShell could not read the
///         full file and would skip all other actions without a message.
///     </para>
/// </summary>
public static class FirstLogonScriptGenerator
{
    /// <summary>Folder of the scripts, relative to the root of the image.</summary>
    public const string ImageFolder = @"Windows\Setup\Scripts\Plainstall";

    public const string MachineScriptName = "Machine.ps1";
    public const string UserScriptName = "User.ps1";

    public const string MachineScriptImagePath = ImageFolder + @"\" + MachineScriptName;
    public const string UserScriptImagePath = ImageFolder + @"\" + UserScriptName;

    /// <summary>
    ///     Folder of Machine.ps1's log, relative to the root of the image. The build creates it, and the script
    ///     creates it again when it is missing. Machine.log and the marker <see cref="MachineDoneFileName" /> are
    ///     written here.
    /// </summary>
    public const string LogFolderImagePath = ImageFolder + @"\Logs";

    /// <summary>
    ///     Name of the empty file Machine.ps1 creates in its log folder as its very last step, whether or
    ///     not every action worked. Its presence means "the machine part has finished".
    /// </summary>
    public const string MachineDoneFileName = "Machine.done";

    private const string ScriptFolderUnderSystemRoot = @"\Setup\Scripts\Plainstall\";

    /// <summary>
    ///     The <c>FirstLogonCommands</c> command line for the answer file.
    ///     <para>
    ///         The path is built inside PowerShell from <c>$env:SystemRoot</c>. That does not rely on Windows
    ///         expanding <c>%SystemRoot%</c> in the command line, and does not assume that Windows is on C:.
    ///         The window stays visible on purpose: it carries the "apps are being installed" message.
    ///         <c>-ExecutionPolicy Bypass</c> is needed because a fresh Windows does not run script files at all.
    ///     </para>
    /// </summary>
    public const string MachineCommandLine =
        "powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command \"& ($env:SystemRoot + '" +
        ScriptFolderUnderSystemRoot + MachineScriptName + "')\"";

    /// <summary>Key of the per-user RunOnce value, relative to the root of the Default user's hive (HKCU of a new account).</summary>
    public const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";

    /// <summary>
    ///     Name of the RunOnce value. No leading "!": Windows then removes the value before it starts the
    ///     command, so a script that fails can never run again at every sign-in.
    /// </summary>
    public const string RunOnceValueName = "PlainstallUser";

    /// <summary>
    ///     Data of the RunOnce value, a plain string (REG_SZ). Hidden window: the per-user part has
    ///     nothing to tell the user. Must stay within <see cref="RunOnceMaxLength" />.
    /// </summary>
    public const string RunOnceValueData =
        "powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"& ($env:SystemRoot + '" +
        ScriptFolderUnderSystemRoot + UserScriptName + "')\"";

    /// <summary>Windows does not run a RunOnce command line longer than this, and gives no error when it skips one.</summary>
    public const int RunOnceMaxLength = 260;

    /// <summary>How long Machine.ps1 waits for an internet connection before it gives up on the apps.</summary>
    public const int NetworkWaitSeconds = 300;

    /// <summary>The user's theme settings. A first sign-in change below it ends User.ps1 with a theme refresh.</summary>
    public const string ThemesKey = @"Software\Microsoft\Windows\CurrentVersion\Themes";

    /// <summary>How long Machine.ps1 waits for winget to become available to the new account.</summary>
    public const int WingetWaitSeconds = 300;

    /// <summary>How long one app may take to install before it is stopped and the next one starts.</summary>
    public const int InstallLimitSeconds = 1200;

    // ---------------------------------------------------------------------------------------------
    // Script text. Everything below is fixed text written for this project. Data is added only by the
    // methods after it, and only through PowerShellText.
    // ---------------------------------------------------------------------------------------------

    private const string UserHeader = """
                                      # Plainstall: settings for a new user account.
                                      #
                                      # Created by Plainstall when the installation stick was built. Windows starts this file
                                      # once for every new account, at its first sign-in, with that user's own rights.
                                      # It changes settings of that user only. It never restarts the PC and downloads nothing.
                                      # Log: %LOCALAPPDATA%\Plainstall\Logs\User.log

                                      $ErrorActionPreference = 'Continue'
                                      $ProgressPreference = 'SilentlyContinue'

                                      $plainstallLogFolder = Join-Path $env:LOCALAPPDATA 'Plainstall\Logs'
                                      $plainstallLog = Join-Path $plainstallLogFolder 'User.log'
                                      $plainstallDone = Join-Path $env:LOCALAPPDATA 'Plainstall\User.done'
                                      $plainstallProblems = 0


                                      """;

    private const string MachineHeader = """
                                         # Plainstall: last steps on a freshly installed PC.
                                         #
                                         # Created by Plainstall when the installation stick was built. Windows starts this file
                                         # once, with administrator rights, when the first account signs in for the first time.
                                         # It never restarts the PC. It downloads nothing, except the apps the owner of the stick
                                         # chose, and those only through winget.
                                         # Log: %SystemRoot%\Setup\Scripts\Plainstall\Logs\Machine.log

                                         $ErrorActionPreference = 'Continue'
                                         $ProgressPreference = 'SilentlyContinue'

                                         $plainstallLogFolder = Join-Path $env:SystemRoot 'Setup\Scripts\Plainstall\Logs'
                                         $plainstallLog = Join-Path $plainstallLogFolder 'Machine.log'
                                         $plainstallDone = Join-Path $plainstallLogFolder 'Machine.done'
                                         $plainstallProblems = 0


                                         """;

    private const string CommonFunctions = """
                                           function Write-PlainstallLog {
                                               param([string]$Message)
                                               $plainstallLine = '{0}  {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
                                               try {
                                                   Add-Content -LiteralPath $script:plainstallLog -Value $plainstallLine -Encoding UTF8 -ErrorAction Stop
                                               } catch {
                                                   # The log itself cannot be written. There is no other place to report that,
                                                   # and the work must go on.
                                               }
                                           }

                                           # Runs one action. Whatever happens inside is written to the log, and the script goes on.
                                           function Invoke-PlainstallStep {
                                               param([string]$Title, [scriptblock]$Action)
                                               try {
                                                   $plainstallItems = @(& $Action *>&1)
                                                   $plainstallErrors = @($plainstallItems | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] })
                                                   $plainstallText = ($plainstallItems | Out-String).Trim()
                                                   if ($plainstallErrors.Count -gt 0) {
                                                       $script:plainstallProblems++
                                                       Write-PlainstallLog ('ERROR   ' + $Title)
                                                   } else {
                                                       Write-PlainstallLog ('OK      ' + $Title)
                                                   }
                                                   if ($plainstallText) {
                                                       Write-PlainstallLog ('        ' + $plainstallText)
                                                   }
                                               } catch {
                                                   $script:plainstallProblems++
                                                   Write-PlainstallLog ('FAILED  ' + $Title + ': ' + $_.Exception.Message)
                                               }
                                           }

                                           # Runs one command from the Plainstall catalog. The text becomes code here and nowhere else,
                                           # inside the step, so that a command with a mistake in it fails alone.
                                           function Invoke-PlainstallCommand {
                                               param([string]$Title, [string]$Code)
                                               Invoke-PlainstallStep $Title { & ([scriptblock]::Create($Code)) }
                                           }


                                           """;

    private const string RegistryFunctions = """
                                             # Registry helpers. They use the .NET registry classes, so a key or value name is always
                                             # taken literally. All keys are relative to HKEY_CURRENT_USER.
                                             function Set-PlainstallRegistryValue {
                                                 param([string]$Key, [string]$Name, [string]$Kind, $Data)
                                                 if ([string]::IsNullOrWhiteSpace($Key)) { throw 'The registry key is empty.' }
                                                 $plainstallCulture = [System.Globalization.CultureInfo]::InvariantCulture
                                                 if ($Kind -eq 'DWord') {
                                                     # The registry class wants a signed number with the same bits.
                                                     $plainstallValue = [System.BitConverter]::ToInt32([System.BitConverter]::GetBytes([uint32]::Parse([string]$Data, $plainstallCulture)), 0)
                                                 } elseif ($Kind -eq 'QWord') {
                                                     $plainstallValue = [System.BitConverter]::ToInt64([System.BitConverter]::GetBytes([uint64]::Parse([string]$Data, $plainstallCulture)), 0)
                                                 } elseif ($Kind -eq 'MultiString') {
                                                     $plainstallValue = [string[]]@($Data)
                                                 } elseif ($Kind -eq 'Binary') {
                                                     $plainstallHex = [string]$Data
                                                     $plainstallValue = [byte[]]::new($plainstallHex.Length / 2)
                                                     for ($plainstallIndex = 0; $plainstallIndex -lt $plainstallValue.Length; $plainstallIndex++) {
                                                         $plainstallValue[$plainstallIndex] = [System.Convert]::ToByte($plainstallHex.Substring($plainstallIndex * 2, 2), 16)
                                                     }
                                                 } elseif ($Kind -eq 'String' -or $Kind -eq 'ExpandString') {
                                                     $plainstallValue = [string]$Data
                                                 } else {
                                                     throw ('Unknown registry value type: ' + $Kind)
                                                 }
                                                 $plainstallKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($Key)
                                                 try {
                                                     $plainstallKey.SetValue($Name, $plainstallValue, [Microsoft.Win32.RegistryValueKind]$Kind)
                                                 } finally {
                                                     $plainstallKey.Close()
                                                 }
                                             }

                                             function Remove-PlainstallRegistryValue {
                                                 param([string]$Key, [string]$Name)
                                                 if ([string]::IsNullOrWhiteSpace($Key)) { throw 'The registry key is empty.' }
                                                 $plainstallKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($Key, $true)
                                                 if ($null -eq $plainstallKey) { return }
                                                 try {
                                                     $plainstallKey.DeleteValue($Name, $false)
                                                 } finally {
                                                     $plainstallKey.Close()
                                                 }
                                             }

                                             function Remove-PlainstallRegistryKey {
                                                 param([string]$Key)
                                                 if ([string]::IsNullOrWhiteSpace($Key)) { throw 'The registry key is empty.' }
                                                 [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($Key, $false)
                                             }


                                             """;

    private const string StartBlock = """
                                      try {
                                          New-Item -ItemType Directory -Path $plainstallLogFolder -Force -ErrorAction Stop | Out-Null
                                      } catch {
                                          # Without the folder there is no log, but the work is still worth doing.
                                      }
                                      if (Test-Path -LiteralPath $plainstallDone) {
                                          # This script has already run to its end once.
                                          exit 0
                                      }
                                      Write-PlainstallLog 'Started.'


                                      """;

    private const string MachineGreeting = """
                                           try { $Host.UI.RawUI.WindowTitle = 'Finishing the setup of this PC' } catch { }
                                           Write-Host ''
                                           Write-Host '  Windows is almost ready.'
                                           Write-Host '  A few last steps are running. This window closes by itself.'
                                           Write-Host ''


                                           """;

    private const string WingetBlock = """
                                       $plainstallNetworkSeconds = @@NETWORK_SECONDS@@
                                       $plainstallWingetSeconds = @@WINGET_SECONDS@@
                                       $plainstallInstallSeconds = @@INSTALL_SECONDS@@
                                       $plainstallInstalled = 0

                                       function Test-PlainstallInternet {
                                           try {
                                               [void][System.Net.Dns]::GetHostAddresses('cdn.winget.microsoft.com')
                                               return $true
                                           } catch {
                                               return $false
                                           }
                                       }

                                       function Find-PlainstallWinget {
                                           $plainstallCommand = Get-Command -Name 'winget.exe' -CommandType Application -ErrorAction Ignore | Select-Object -First 1
                                           if ($plainstallCommand) { return $plainstallCommand.Path }
                                           $plainstallAlias = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\winget.exe'
                                           if (Test-Path -LiteralPath $plainstallAlias) { return $plainstallAlias }
                                           return $null
                                       }

                                       # Waits until the condition is true, but never longer than the given time.
                                       function Wait-PlainstallCondition {
                                           param([scriptblock]$Condition, [int]$Seconds)
                                           $plainstallDeadline = (Get-Date).AddSeconds($Seconds)
                                           while ($true) {
                                               if (& $Condition) { return $true }
                                               if ((Get-Date) -ge $plainstallDeadline) { return $false }
                                               Start-Sleep -Seconds 5
                                           }
                                       }

                                       Write-Host '  Plainstall installs your apps now.'
                                       Write-Host '  Please do not switch the PC off, and keep it connected to the internet.'
                                       Write-Host '  This can take several minutes. This window closes by itself when it is done.'
                                       Write-Host ''
                                       Write-PlainstallLog ('Apps to install: ' + ($plainstallApps -join ', '))

                                       $plainstallReady = $true
                                       if (-not (Test-PlainstallInternet)) {
                                           Write-Host '  Waiting for an internet connection, for up to @@NETWORK_MINUTES@@ minutes.'
                                           Write-Host '  Connect to Wi-Fi or plug in a network cable now.'
                                           Write-Host ''
                                           if (-not (Wait-PlainstallCondition { Test-PlainstallInternet } $plainstallNetworkSeconds)) {
                                               $plainstallReady = $false
                                               $plainstallProblems++
                                               Write-PlainstallLog 'SKIPPED the apps: no internet connection within the time limit.'
                                               Write-Host '  There is no internet connection, so Plainstall did not install the apps.'
                                           }
                                       }

                                       $plainstallWinget = $null
                                       if ($plainstallReady) {
                                           $plainstallWinget = Find-PlainstallWinget
                                           if (-not $plainstallWinget) {
                                               # On a new account Windows may not have finished registering App Installer (winget) yet.
                                               # This asks Windows to do it now. The package is already on the PC; nothing is downloaded.
                                               Write-Host '  Getting the app installer ready.'
                                               try {
                                                   Add-AppxPackage -RegisterByFamilyName -MainPackage 'Microsoft.DesktopAppInstaller_8wekyb3d8bbwe' -ErrorAction Stop
                                               } catch {
                                                   Write-PlainstallLog ('App Installer could not be registered right away: ' + $_.Exception.Message)
                                               }
                                               [void](Wait-PlainstallCondition { [bool](Find-PlainstallWinget) } $plainstallWingetSeconds)
                                               $plainstallWinget = Find-PlainstallWinget
                                           }
                                           if (-not $plainstallWinget) {
                                               $plainstallReady = $false
                                               $plainstallProblems++
                                               Write-PlainstallLog 'SKIPPED the apps: winget did not become available within the time limit.'
                                               Write-Host '  The app installer of Windows is not ready, so Plainstall did not install the apps.'
                                           }
                                       }

                                       if ($plainstallReady) {
                                           Write-PlainstallLog ('Using ' + $plainstallWinget)
                                           $plainstallNumber = 0
                                           foreach ($plainstallApp in $plainstallApps) {
                                               $plainstallNumber++
                                               # A Microsoft Store id comes from the msstore source, every other id from the winget source.
                                               if ($plainstallApp -cmatch $plainstallStorePattern) {
                                                   $plainstallSource = 'msstore'
                                               } elseif ($plainstallApp -cmatch $plainstallAppPattern) {
                                                   $plainstallSource = 'winget'
                                               } else {
                                                   $plainstallProblems++
                                                   Write-PlainstallLog ('SKIPPED app number {0}: its name is not a winget package id or a Store id.' -f $plainstallNumber)
                                                   continue
                                               }
                                               Write-Host ('  Installing app {0} of {1}: {2}' -f $plainstallNumber, $plainstallApps.Count, $plainstallApp)
                                               Invoke-PlainstallStep ('Install ' + $plainstallApp) {
                                                   $plainstallArguments = @('install', '--id', $plainstallApp, '--exact', '--silent', '--accept-package-agreements', '--accept-source-agreements', '--source', $plainstallSource)
                                                   $plainstallProcess = Start-Process -FilePath $plainstallWinget -ArgumentList $plainstallArguments -NoNewWindow -PassThru
                                                   # Reading the handle now makes the exit code available later (a quirk of Start-Process).
                                                   $null = $plainstallProcess.Handle
                                                   if (-not $plainstallProcess.WaitForExit($plainstallInstallSeconds * 1000)) {
                                                       try { $plainstallProcess.Kill() } catch { }
                                                       throw 'The installation did not finish within the time limit and was stopped.'
                                                   }
                                                   if ($plainstallProcess.ExitCode -ne 0) {
                                                       throw ('winget ended with code 0x{0:X8}.' -f $plainstallProcess.ExitCode)
                                                   }
                                                   $script:plainstallInstalled++
                                               }
                                           }
                                       }

                                       Write-Host ''
                                       if ($plainstallInstalled -eq $plainstallApps.Count) {
                                           Write-Host '  All apps are installed.'
                                           Start-Sleep -Seconds 3
                                       } else {
                                           Write-Host ('  Plainstall installed {0} of {1} apps.' -f $plainstallInstalled, $plainstallApps.Count)
                                           Write-Host '  You can install the missing ones yourself later, for example from the Microsoft Store.'
                                           Write-Host ('  Details are in this file: ' + $plainstallLog)
                                           Start-Sleep -Seconds 15
                                       }


                                       """;

    // Windows reads the theme when the taskbar starts, and at first sign-in the taskbar is already running
    // when User.ps1 sets the theme. Without this message the taskbar stays light until the next sign-in.
    private const string ThemeRefreshBlock = """
                                             # The taskbar is already running and does not read the new colors by itself. Settings sends
                                             # this message when the theme changes, so the taskbar and open windows switch at once.
                                             Invoke-PlainstallStep 'Show the new colors at once' {
                                                 Add-Type -Namespace PlainstallNative -Name User32 -MemberDefinition '[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);'
                                                 $plainstallResult = [UIntPtr]::Zero
                                                 # To all top-level windows (0xFFFF): WM_SETTINGCHANGE (0x1A), "ImmersiveColorSet". Windows that
                                                 # do not answer are skipped (SMTO_ABORTIFHUNG), and each one gets at most 5 seconds.
                                                 [void][PlainstallNative.User32]::SendMessageTimeout([IntPtr]0xFFFF, 0x1A, [UIntPtr]::Zero, 'ImmersiveColorSet', 2, 5000, [ref]$plainstallResult)
                                             }

                                             """;

    private const string Footer = """
                                  Write-PlainstallLog ('Finished. Steps with problems: ' + $plainstallProblems)

                                  # The very last step: an empty marker file. It is created whether or not every step worked.
                                  # It keeps this script from running twice, and it shows that the work is over.
                                  try {
                                      [void][System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($plainstallDone))
                                      [System.IO.File]::WriteAllBytes($plainstallDone, [byte[]]::new(0))
                                  } catch {
                                      Write-PlainstallLog ('The marker file could not be written: ' + $_.Exception.Message)
                                  }
                                  exit 0

                                  """;

    /// <summary>Both scripts for a plan.</summary>
    /// <exception cref="PlainstallException">InvalidInput when an action or a winget id cannot be written safely.</exception>
    public static FirstLogonScripts Generate(BuildPlan plan)
    {
        return new FirstLogonScripts(GenerateMachineScript(plan), GenerateUserScript(plan));
    }

    /// <summary>
    ///     The value the build writes into the Default user's hive so that Windows starts User.ps1 for
    ///     every new account. Offered as a ready action so the offline registry code can apply it like any
    ///     catalog action. Only needed when <see cref="GenerateUserScript" /> returns a script.
    /// </summary>
    public static RegistrySetAction CreateRunOnceAction()
    {
        return new RegistrySetAction
        {
            Hive = RegistryHiveId.DefaultUser,
            Key = RunOnceKey,
            Name = RunOnceValueName,
            Type = RegistryValueType.String,
            Data = JsonSerializer.SerializeToElement(RunOnceValueData),
            Note = "Starts the per-user first sign-in script of Plainstall."
        };
    }

    /// <summary>
    ///     User.ps1, or null when the plan has no <c>firstLogon.registry</c> action and no user-scope command.
    ///     Actions keep the order of the plan.
    /// </summary>
    public static GeneratedScript? GenerateUserScript(BuildPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var steps = new List<string>();
        foreach (var planned in plan.Actions)
            switch (planned.Action)
            {
                case FirstLogonRegistryAction registry:
                    steps.Add(RegistryStep(planned, registry));
                    break;
                case FirstLogonCommandAction { Scope: FirstLogonScope.User } command:
                    steps.Add(CommandStep(planned, command));
                    break;
            }

        if (steps.Count == 0) return null;

        var text = new StringBuilder();
        text.Append(UserHeader);
        text.Append(CommonFunctions);
        text.Append(RegistryFunctions);
        text.Append(StartBlock);
        foreach (var step in steps) text.Append(step).Append('\n');
        text.Append('\n');
        if (plan.Actions.Any(a => a.Action is FirstLogonRegistryAction registry && IsThemeKey(registry.Key)))
            text.Append(ThemeRefreshBlock);
        text.Append(Footer);
        return new GeneratedScript(UserScriptImagePath, Finish(text));
    }

    /// <summary>
    ///     Machine.ps1, or null when the plan has no machine-scope command and no winget install.
    ///     Commands come first, in the order of the plan, then the apps one by one.
    /// </summary>
    public static GeneratedScript? GenerateMachineScript(BuildPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var steps = plan.Actions
            .Where(a => a.Action is FirstLogonCommandAction { Scope: FirstLogonScope.Machine })
            .Select(a => CommandStep(a, (FirstLogonCommandAction)a.Action))
            .ToList();

        var apps = new List<string>();
        foreach (var id in plan.WingetInstalls)
        {
            if (!InstallIds.IsValid(id))
                // The message does not repeat the value: it may be long or contain line breaks.
                throw new PlainstallException(ErrorKind.InvalidInput,
                    "apps.install contains a value that is not a winget package id or a Microsoft Store id. A winget id looks like 'Mozilla.Firefox': letters, digits, '+', '_' and '-', with at least one dot. A Store id looks like '9NKSQGP7F2NH': capital letters and digits only.");
            if (!apps.Contains(id, StringComparer.OrdinalIgnoreCase)) apps.Add(id);
        }

        if (steps.Count == 0 && apps.Count == 0) return null;

        var text = new StringBuilder();
        text.Append(MachineHeader);
        text.Append(CommonFunctions);
        text.Append(StartBlock);
        text.Append(MachineGreeting);
        foreach (var step in steps) text.Append(step).Append('\n');
        if (steps.Count > 0) text.Append('\n');
        if (apps.Count > 0)
        {
            text.Append("$plainstallApps = ").Append(PowerShellText.ArrayLiteral(apps)).Append('\n');
            text.Append("$plainstallAppPattern = ").Append(PowerShellText.Literal(InstallIds.WingetPatternText))
                .Append('\n');
            text.Append("$plainstallStorePattern = ").Append(PowerShellText.Literal(InstallIds.StorePatternText))
                .Append('\n');
            text.Append(WingetBlock
                .Replace("@@NETWORK_SECONDS@@", Number(NetworkWaitSeconds), StringComparison.Ordinal)
                .Replace("@@NETWORK_MINUTES@@", Number(NetworkWaitSeconds / 60), StringComparison.Ordinal)
                .Replace("@@WINGET_SECONDS@@", Number(WingetWaitSeconds), StringComparison.Ordinal)
                .Replace("@@INSTALL_SECONDS@@", Number(InstallLimitSeconds), StringComparison.Ordinal));
        }

        text.Append(Footer);
        return new GeneratedScript(MachineScriptImagePath, Finish(text));
    }

    private static string RegistryStep(PlannedAction planned, FirstLogonRegistryAction action)
    {
        var where = $"{planned.EntryId} ({action.KindName})";
        if (string.IsNullOrWhiteSpace(action.Key) || action.Key.StartsWith('\\') || action.Key.EndsWith('\\'))
            throw new PlainstallException(ErrorKind.InvalidInput,
                $"{where}: the key must be a path relative to HKCU, not empty and without outer backslashes.");

        var key = PowerShellText.Literal(action.Key);
        switch (action.Op)
        {
            case FirstLogonRegistryOp.Set:
            {
                if (action.Name is null || action.Type is not { } type || action.Data is not { } data)
                    throw new PlainstallException(ErrorKind.InvalidInput,
                        $"{where}: a set operation needs name, type and data.");
                var value = RegistryData.TryConvert(type, data, out var error)
                            ?? throw new PlainstallException(ErrorKind.InvalidInput, $"{where}: {error}.");
                var (kind, dataText) = value switch
                {
                    // Numbers travel as decimal text and are parsed in the script, so nothing depends on
                    // how PowerShell types a numeric literal.
                    uint dword => ("DWord", PowerShellText.Literal(dword.ToString(CultureInfo.InvariantCulture))),
                    ulong qword => ("QWord", PowerShellText.Literal(qword.ToString(CultureInfo.InvariantCulture))),
                    string text when type == RegistryValueType.ExpandString => ("ExpandString",
                        PowerShellText.Literal(text)),
                    string text => ("String", PowerShellText.Literal(text)),
                    string[] lines => ("MultiString", PowerShellText.ArrayLiteral(lines)),
                    byte[] bytes => ("Binary", PowerShellText.Literal(Convert.ToHexString(bytes))),
                    _ => throw new PlainstallException(ErrorKind.Internal, $"{where}: unexpected registry data.")
                };
                var title = PowerShellText.Literal($"{planned.EntryTitle}: set {action.Target}");
                return
                    $"Invoke-PlainstallStep {title} {{ Set-PlainstallRegistryValue {key} {PowerShellText.Literal(action.Name)} '{kind}' {dataText} }}";
            }

            case FirstLogonRegistryOp.Delete:
            {
                var title = PowerShellText.Literal($"{planned.EntryTitle}: delete {action.Target}");
                return action.Name is null
                    ? $"Invoke-PlainstallStep {title} {{ Remove-PlainstallRegistryKey {key} }}"
                    : $"Invoke-PlainstallStep {title} {{ Remove-PlainstallRegistryValue {key} {PowerShellText.Literal(action.Name)} }}";
            }

            default:
                throw new PlainstallException(ErrorKind.Internal, $"{where}: unknown operation.");
        }
    }

    /// <summary>True for a key below the user's theme settings, such as Themes\Personalize for dark mode.</summary>
    private static bool IsThemeKey(string? key)
    {
        return key is not null &&
               (key.Equals(ThemesKey, StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith(ThemesKey + @"\", StringComparison.OrdinalIgnoreCase));
    }

    private static string CommandStep(PlannedAction planned, FirstLogonCommandAction action)
    {
        if (string.IsNullOrWhiteSpace(action.Command))
            throw new PlainstallException(ErrorKind.InvalidInput,
                $"{planned.EntryId} ({action.KindName}): the command is empty.");
        var title = PowerShellText.Literal($"{planned.EntryTitle}: {action.Summary}");
        return $"Invoke-PlainstallCommand {title} {PowerShellText.Literal(action.Command)}";
    }

    private static string Number(int value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>One line-end style for the whole file, whatever the source file of this class was checked out with.</summary>
    private static string Finish(StringBuilder text)
    {
        return text.ToString().Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", "\r\n", StringComparison.Ordinal);
    }
}