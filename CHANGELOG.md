# Changelog

This file lists the changes in each version of Plainstall. The version numbers follow [Semantic Versioning](https://semver.org).

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
