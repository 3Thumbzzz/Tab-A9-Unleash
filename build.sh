#!/usr/bin/env bash
# Builds Tab-A9-LineageOS-Installer.exe with Mono (Linux/macOS) or csc (Windows/.NET Framework).
set -e
mcs -target:winexe -platform:x64 -optimize+ -out:Tab-A9-LineageOS-Installer.exe \
  -r:System.Windows.Forms.dll -r:System.Drawing.dll \
  -resource:patch-recovery.sh,patch.sh Program.cs
