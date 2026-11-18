<#
.SYNOPSIS
Adapts the library module of the EOS plugin in an exported Android project to the Gradle of Unity 6000.3.17 and later, and turns
on the core library desugaring that the EOS SDK asks of the application module.

.DESCRIPTION
The EOS Plugin for Unity (6.2.0 and 6.3.0) writes its library module, eos_dependencies.androidlib, for an old Android Gradle
plugin: a buildscript block with jcenter(), the compileSdkVersion and targetSdkVersion properties, and package="..." in the
manifest. Gradle 9.1 and Android Gradle plugin 9.0 stop at "Could not find method jcenter() for arguments []".

This script does to a folder that was exported already what the build step of the Unity project does while Unity exports
(Assets/BoardSandbox/Editor/EosAndroidLibraryFix.cs, EosAndroidProjectFix.cs and EosAndroidDesugaringFix.cs, which this is a copy
of): it takes the buildscript block and the old properties out of build.gradle, puts a namespace and the compileSdk in, and takes
the package attribute off the manifest.

The AAR of the EOS SDK is built with core library desugaring, and the build of the application stops at "Dependency ':eos-sdk:'
requires core library desugaring to be enabled for :launcher" unless the application desugars too. Where launcher/build.gradle
(beside the module that holds the library) does not have it yet, the script adds what is missing at the end of that file: an
android block with compileOptions { coreLibraryDesugaringEnabled true } and a dependencies block with coreLibraryDesugaring and
the desugar_jdk_libs library.

Only these two files of the library and launcher/build.gradle are written. What is in the new form already is not touched, so the
script can be run again. It needs nothing but Windows PowerShell 5.1 or later; Unity does not have to run.

Unity writes the library again at every export. If the Unity project has the build step, it does this by itself, and this script
is only for a folder that was exported without it.

.PARAMETER Path
The exported project (the folder that holds unityLibrary), its unityLibrary module, or eos_dependencies.androidlib itself.
Default: the current folder. Leave the backslash off the end of the path, or cmd.exe turns the closing quote into a character.

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\FixEosAndroidExport.ps1 -Path F:\BoardSandbox_Android

.NOTES
Exit code: 0 when the library is in the new form (adapted now, or before), 1 when no library was found, 2 when build.gradle of the
library is not in a form that can be adapted. After a change, sync the project in Android Studio again.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Path = (Get-Location).Path
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$LibraryFolder = 'eos_dependencies.androidlib'

# The package the manifest of the library has had in every version of the plugin.
$DefaultNamespace = 'com.pew.eos_dependencies'

# What the Android Gradle plugin desugars with, from Google's Maven repository (DesugarLibrary in EosAndroidDesugaringFix.cs).
$DesugarLibrary = 'com.android.tools:desugar_jdk_libs:2.1.5'

$Culture = [System.Text.RegularExpressions.RegexOptions]'CultureInvariant'
$Lines = [System.Text.RegularExpressions.RegexOptions]'CultureInvariant, Multiline'

function New-Rx([string]$pattern, $options) {
    return New-Object System.Text.RegularExpressions.Regex($pattern, $options)
}

$Jcenter = New-Rx '\bjcenter[ \t]*\(' $Lines
$BuildscriptBlock = New-Rx '^[ \t]*buildscript[ \t]*\{' $Lines
$OldVersionProperty = New-Rx '^[ \t]*(compileSdkVersion|targetSdkVersion)\b' $Lines
$NamespaceProperty = New-Rx '^[ \t]*namespace\b' $Lines
$AndroidBlock = New-Rx '^([ \t]*)android[ \t]*\{[ \t]*(\r\n|\n)?' $Lines
$CompileSdk = New-Rx '^[ \t]*compileSdk(?:Version)?[ \t]*=?[ \t]*(\d+)' $Lines
$NamespaceValue = New-Rx '^[ \t]*namespace[ \t]*=?[ \t]*[''"]([^''"\r\n]+)[''"]' $Lines
$RewrittenLine = New-Rx '^[ \t]*(compileSdkVersion|compileSdk|buildToolsVersion|targetSdkVersion|namespace)\b[^\r\n]*(\r\n|\n)?' $Lines
$EmptyDefaultConfig = New-Rx '^[ \t]*defaultConfig[ \t]*\{\s*\}[ \t]*(\r\n|\n)?' $Lines
$ManifestStartTag = New-Rx '<manifest\b[^>]*>' $Culture
$PackageAttribute = New-Rx '\s+package\s*=\s*(?:"([^"]*)"|''([^'']*)'')' $Culture
$DesugarAndroidBlock = New-Rx '^[ \t]*android[ \t]*\{' $Lines
$DesugarFlagOn = New-Rx '^[ \t]*(?:isCoreLibraryDesugaringEnabled|coreLibraryDesugaringEnabled)[ \t]*=?[ \t]*true\b' $Lines
# The dependency, not the flag, whose name starts the same.
$DesugarDependency = New-Rx '^[ \t]*coreLibraryDesugaring(?![A-Za-z0-9_])' $Lines

# ---- text ---------------------------------------------------------------------------------------------------------------

# The words after // are no code. Quotes are not looked at: nothing that is looked for sits in a string.
function Remove-LineComments([string]$text) {
    return [System.Text.RegularExpressions.Regex]::Replace($text, '//[^\r\n]*', '', $Culture)
}

function Get-Capture($regex, [string]$text, [int]$group) {
    $m = $regex.Match($text)
    if ($m.Success) { return $m.Groups[$group].Value }
    return $null
}

# Takes the first block that starts a line with the keyword out of the text, with the braces inside it.
# Text whose braces do not match is returned as it is.
function Remove-Block([string]$text, [string]$keyword) {
    $start = New-Rx ('^[ \t]*' + [System.Text.RegularExpressions.Regex]::Escape($keyword) + '[ \t]*\{') $Lines
    $found = $start.Match($text)
    if (-not $found.Success) { return $text }
    $depth = 0
    for ($i = $found.Index + $found.Length - 1; $i -lt $text.Length; $i++) {
        $c = $text[$i]
        if ($c -ceq '{') {
            $depth++
        }
        elseif ($c -ceq '}') {
            $depth--
            if ($depth -eq 0) {
                $end = $i + 1
                while ($end -lt $text.Length -and ($text[$end] -ceq ' ' -or $text[$end] -ceq "`t")) { $end++ }
                if ($end -lt $text.Length -and $text[$end] -ceq "`r") { $end++ }
                if ($end -lt $text.Length -and $text[$end] -ceq "`n") { $end++ }
                return $text.Remove($found.Index, $end - $found.Index)
            }
        }
    }
    return $text
}

function Test-BuildGradleNeedsFix([string]$text) {
    if ([string]::IsNullOrEmpty($text)) { return $false }
    $code = Remove-LineComments $text
    return ($Jcenter.IsMatch($code) -or $BuildscriptBlock.IsMatch($code) -or $OldVersionProperty.IsMatch($code) -or (-not $NamespaceProperty.IsMatch($code)))
}

# The compileSdk of a build.gradle (of the Unity library module, say), or $null.
function Find-CompileSdk([string]$text) {
    if ([string]::IsNullOrEmpty($text)) { return $null }
    return Get-Capture $CompileSdk (Remove-LineComments $text) 1
}

# $manifestNamespace is the package of the manifest. $fallbackCompileSdk is for a file that does not say which SDK it is compiled
# against. Text that has no android block is returned as it is.
function Repair-BuildGradle([string]$text, [string]$manifestNamespace, [string]$fallbackCompileSdk) {
    if ([string]::IsNullOrEmpty($text)) { return $text }
    $code = Remove-LineComments $text
    if (-not $AndroidBlock.IsMatch($text)) { return $text }

    $newline = "`n"
    if ($text.Contains("`r`n")) { $newline = "`r`n" }
    $compileSdk = Get-Capture $CompileSdk $code 1
    if ($null -eq $compileSdk) { $compileSdk = $fallbackCompileSdk }
    $ns = Get-Capture $NamespaceValue $code 1
    if ($null -eq $ns) {
        if ([string]::IsNullOrEmpty($manifestNamespace)) { $ns = $DefaultNamespace } else { $ns = $manifestNamespace }
    }

    $result = Remove-Block $text 'buildscript'
    $result = $RewrittenLine.Replace($result, '')
    $result = $EmptyDefaultConfig.Replace($result, '')

    # The build tools are left out on purpose. Every module of the project is built by the same Android Gradle plugin, which
    # brings its own version for a module that does not ask for one.
    $android = $AndroidBlock.Match($result)
    if (-not $android.Success) { return $text }
    $indent = $android.Groups[1].Value + '    '
    $added = ''
    if ($android.Groups[2].Length -eq 0) { $added += $newline }
    $added += $indent + "namespace '" + $ns + "'" + $newline
    if (-not [string]::IsNullOrEmpty($compileSdk)) { $added += $indent + 'compileSdk ' + $compileSdk + $newline }
    return $result.Insert($android.Index + $android.Length, $added)
}

# The package attribute of the manifest tag: the tag and the attribute as matches, or $null.
function Find-ManifestPackage([string]$text) {
    if ([string]::IsNullOrEmpty($text)) { return $null }
    $tag = $ManifestStartTag.Match($text)
    if (-not $tag.Success) { return $null }
    $attribute = $PackageAttribute.Match($tag.Value)
    if (-not $attribute.Success) { return $null }
    return @{ Tag = $tag; Attribute = $attribute }
}

# Takes the package attribute off the manifest tag. The package is what the library is called in build.gradle from now on.
function Repair-Manifest([string]$text) {
    $found = Find-ManifestPackage $text
    if ($null -eq $found) { return @{ Text = $text; Package = $null; Changed = $false } }
    $tag = $found.Tag
    $attribute = $found.Attribute
    if ($attribute.Groups[1].Success) { $package = $attribute.Groups[1].Value } else { $package = $attribute.Groups[2].Value }
    $fixedTag = $tag.Value.Remove($attribute.Index, $attribute.Length)
    $fixed = $text.Substring(0, $tag.Index) + $fixedTag + $text.Substring($tag.Index + $tag.Length)
    return @{ Text = $fixed; Package = $package; Changed = $true }
}

# True for the build file of a module (it has an android block) that does not desugar yet: the flag is not on, or there is no
# library to desugar with.
function Test-DesugaringNeedsFix([string]$text) {
    if ([string]::IsNullOrEmpty($text)) { return $false }
    $code = Remove-LineComments $text
    return ($DesugarAndroidBlock.IsMatch($code) -and ((-not $DesugarFlagOn.IsMatch($code)) -or (-not $DesugarDependency.IsMatch($code))))
}

# The text with what is missing added at its end, in the line endings of the file. Text that does not need it is returned as it is.
function Repair-Desugaring([string]$text) {
    if (-not (Test-DesugaringNeedsFix $text)) { return $text }
    $code = Remove-LineComments $text
    $newline = "`n"
    if ($text.Contains("`r`n")) { $newline = "`r`n" }
    $result = $text
    if ($text[$text.Length - 1] -cne "`n") { $result += $newline }
    $result += $newline
    $result += '// The AAR of the EOS SDK asks the application for core library desugaring (Board Sandbox, EosAndroidDesugaringFix).' + $newline
    if (-not $DesugarFlagOn.IsMatch($code)) {
        $result += 'android {' + $newline
        $result += '    compileOptions {' + $newline
        $result += '        coreLibraryDesugaringEnabled true' + $newline
        $result += '    }' + $newline
        $result += '}' + $newline
    }
    if (-not $DesugarDependency.IsMatch($code)) {
        $result += 'dependencies {' + $newline
        $result += "    coreLibraryDesugaring '" + $DesugarLibrary + "'" + $newline
        $result += '}' + $newline
    }
    return $result
}

# ---- files --------------------------------------------------------------------------------------------------------------

function Get-TrimmedPath([string]$path) {
    $separators = [char[]]@([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    return $path.TrimEnd($separators)
}

# Looks in the places where Unity puts a library plug-in, nearest first, and then anywhere below the path.
function Find-Library([string]$path) {
    if ([string]::IsNullOrEmpty($path) -or -not [System.IO.Directory]::Exists($path)) { return $null }

    $trimmed = Get-TrimmedPath $path
    if ($trimmed.Length -eq 0) { $trimmed = $path } # the root of a file system
    if ([System.IO.Path]::GetFileName($trimmed) -ceq $LibraryFolder) { return $trimmed }

    # The path is the unityLibrary module: the library is a folder in it.
    $inside = [System.IO.Path]::Combine($path, $LibraryFolder)
    if ([System.IO.Directory]::Exists($inside)) { return $inside }

    # The path is the exported project.
    $inModule = [System.IO.Path]::Combine([System.IO.Path]::Combine($path, 'unityLibrary'), $LibraryFolder)
    if ([System.IO.Directory]::Exists($inModule)) { return $inModule }

    # The library lies beside the unityLibrary module.
    $parent = [System.IO.Path]::GetDirectoryName($trimmed)
    if (-not [string]::IsNullOrEmpty($parent)) {
        $beside = [System.IO.Path]::Combine($parent, $LibraryFolder)
        if ([System.IO.Directory]::Exists($beside)) { return $beside }
    }

    $below = @([System.IO.Directory]::GetDirectories($path, $LibraryFolder, [System.IO.SearchOption]::AllDirectories))
    if ($below.Count -gt 0) { return $below[0] }
    return $null
}

# The application module lies beside the module that holds the library, in the root of the Gradle project. Where the library is in
# the root itself, it lies beside the library. Where there is none (the copy of the library in Assets/Plugins/Android), $null.
function Find-LauncherBuildGradle([string]$library) {
    $parent = [System.IO.Path]::GetDirectoryName((Get-TrimmedPath $library))
    if ([string]::IsNullOrEmpty($parent)) { return $null }
    $grandparent = [System.IO.Path]::GetDirectoryName($parent)
    foreach ($root in @($grandparent, $parent)) {
        if ([string]::IsNullOrEmpty($root)) { continue }
        $candidate = [System.IO.Path]::Combine([System.IO.Path]::Combine($root, 'launcher'), 'build.gradle')
        if ([System.IO.File]::Exists($candidate)) { return $candidate }
    }
    return $null
}

# The SDK that the Unity module is compiled against, for a library whose own build.gradle does not say.
function Find-UnityCompileSdk([string]$library) {
    $parent = [System.IO.Path]::GetDirectoryName((Get-TrimmedPath $library))
    if ([string]::IsNullOrEmpty($parent)) { return $null }
    $candidates = @(
        [System.IO.Path]::Combine($parent, 'build.gradle'),
        [System.IO.Path]::Combine([System.IO.Path]::Combine($parent, 'unityLibrary'), 'build.gradle')
    )
    foreach ($candidate in $candidates) {
        if (-not [System.IO.File]::Exists($candidate)) { continue }
        $compileSdk = Find-CompileSdk ([System.IO.File]::ReadAllText($candidate))
        if ($null -ne $compileSdk) { return $compileSdk }
    }
    return $null
}

# A file that was copied from a read-only one is read-only itself. The text is written as UTF-8 without a byte order mark.
function Write-Text([string]$path, [string]$text) {
    $attributes = [System.IO.File]::GetAttributes($path)
    $readOnly = [int][System.IO.FileAttributes]::ReadOnly
    if (([int]$attributes -band $readOnly) -ne 0) {
        [System.IO.File]::SetAttributes($path, [System.IO.FileAttributes]([int]$attributes -band (-bnot $readOnly)))
    }
    [System.IO.File]::WriteAllText($path, $text)
}

# ---- run ----------------------------------------------------------------------------------------------------------------

$Path = $Path.Trim().Trim('"')
$library = Find-Library $Path
if ($null -eq $library) {
    Write-Host ('There is no ' + $LibraryFolder + ' in ' + $Path + '.')
    exit 1
}

$manifestPath = [System.IO.Path]::Combine($library, 'AndroidManifest.xml')
$gradlePath = [System.IO.Path]::Combine($library, 'build.gradle')
$manifestChanged = $false
$gradleChanged = $false
$gradleStillOld = $false

# The manifest first: its package becomes the namespace in build.gradle.
$package = $null
if ([System.IO.File]::Exists($manifestPath)) {
    $manifest = [System.IO.File]::ReadAllText($manifestPath)
    $repaired = Repair-Manifest $manifest
    if ($repaired.Changed) {
        $package = $repaired.Package
        Write-Text $manifestPath $repaired.Text
        $manifestChanged = $true
    }
}

if ([System.IO.File]::Exists($gradlePath)) {
    $gradle = [System.IO.File]::ReadAllText($gradlePath)
    if (Test-BuildGradleNeedsFix $gradle) {
        $adapted = Repair-BuildGradle $gradle $package (Find-UnityCompileSdk $library)
        if ($adapted -cne $gradle) {
            Write-Text $gradlePath $adapted
            $gradleChanged = $true
        }
        $gradleStillOld = Test-BuildGradleNeedsFix $adapted
    }
}

# The application module needs core library desugaring for the AAR of the EOS SDK. The module that holds the library is not
# changed: the documentation of Android asks for the setting there only for tests and lint of the module itself.
$desugaringChanged = $false
$launcherPath = Find-LauncherBuildGradle $library
if ($null -ne $launcherPath) {
    $launcher = [System.IO.File]::ReadAllText($launcherPath)
    $desugared = Repair-Desugaring $launcher
    if ($desugared -cne $launcher) {
        Write-Text $launcherPath $desugared
        $desugaringChanged = $true
    }
}

if ($gradleStillOld) {
    $message = 'The build.gradle of the library of the EOS plugin (' + $library + ') is not in a form that can be adapted to the Gradle version of Unity, so it was left as it is.'
}
elseif (-not ($gradleChanged -or $manifestChanged)) {
    $message = 'The library of the EOS plugin is already in the form that the Gradle version of Unity takes (' + $library + ').'
}
else {
    $what = @()
    if ($gradleChanged) { $what += 'build.gradle' }
    if ($manifestChanged) { $what += 'AndroidManifest.xml' }
    $message = 'Adapted the library of the EOS plugin to the Gradle version of Unity (' + $library + '): ' + ($what -join ', ') + '.'
}
if ($desugaringChanged) {
    $message += ' Turned on core library desugaring, which the AAR of the EOS SDK asks for, in launcher/build.gradle.'
}
Write-Host $message
if ($gradleChanged -or $manifestChanged -or $desugaringChanged) {
    Write-Host 'Sync the project in Android Studio again.'
}
if ($gradleStillOld) { exit 2 }
exit 0
