# Changelog

This file lists the changes in each version of Plainstall. The version numbers follow [Semantic Versioning](https://semver.org).

## 0.1.2

- New option on the Advanced page: "Check the image for damage (ScanHealth)". After all changes, DISM examines the system files of the image. If it finds damage, the build stops and Plainstall writes nothing. The option is off by default. It adds 5 to 15 minutes to the build.
- OneDrive: Plainstall no longer deletes the OneDrive installer file. It is a Windows system file, so "sfc /scannow" reported it as damaged. Windows still does not install OneDrive for new accounts.
- Windows Update installs drivers again in all presets. The "Gaming" preset no longer turns off driver updates. The switch is still available on the Tweaks page.
- The full right-click menu is on in the "Recommended", "Privacy first" and "Gaming" presets.
- New tweak: "No language list for websites". It is on in the "Recommended", "Privacy first" and "Gaming" presets.
- New tweak: "No new Edge desktop shortcut". It is on in the "Recommended", "Privacy first" and "Gaming" presets.
- New tweak: "No cloud results in Start search". It is on in the "Privacy first" preset.
- New tweak: "No recent files and folders". It is on in the "Privacy first" preset.
- Dark mode: at the first sign-in, Plainstall tells Windows that the colors changed, so the taskbar changes to dark at once and not only at the next sign-in.

## 0.1.1

- The Customize page shows its four sections (Apps, Tweaks, Setup and Advanced) as cards. Each card shows a short summary, for example how many apps Plainstall removes. **Next** opens the sections in sequence and shows the name of the next section.
- New tweak: "No search history on this PC". It is on in the "Privacy first" preset. Windows applies it when each new account signs in for the first time.
- New tweak: "No Snap layouts pop-up". It is off in all presets.
- Removed the tweak "Widgets off". Current versions of Windows 11 protect this setting, so on most build PCs the change failed and builds with the "Recommended", "Privacy first" and "Gaming" presets ended with a failure message. The Widgets app is still removed in these presets, and this also removes the Widgets button. A profile that contains this tweak still works: Plainstall ignores the tweak and shows a note.

## 0.1.0

The first version.

- Download Windows 11 (x64) from Microsoft. Plainstall compares the file with the checksum that Microsoft publishes. You can also use an ISO file that you have.
- Start from a preset: Recommended, Privacy first, Gaming or Minimal changes.
- Remove preinstalled apps and change Windows settings. Each item has a short explanation and a risk level.
- Set up Windows Setup: local account, computer name, region, keyboard, time zone and the privacy pages.
- Install apps with winget when you sign in for the first time.
- Add drivers and Windows updates to the installer.
- Write the result to a USB stick or to an ISO file.
- Save your selections as a profile. The profile does not contain the password.
- Use the command-line tool Plainstall.Cli for the same work.
- Clean up after a build that stopped, and delete the files that Plainstall keeps on the PC.

Known limits:

- The executables are not signed. Windows SmartScreen can show a warning when you start them.
- Plainstall works only on PCs with an x64 processor (Intel or AMD).
