# Tab A9 (SM-X110) LineageOS Installer

A step-by-step Windows wizard for installing a LineageOS 23.2 GSI on the Samsung Galaxy Tab A9 Wi-Fi (SM-X110).

It walks through ten steps: tablet checks, PC tools (Samsung driver, platform-tools, Odin, WSL), bootloader unlock, building a fastbootd recovery from your own stock firmware, flashing it with Odin, connecting in fastbootd, choosing a GSI, flashing it, and first boot.

> Unlocking wipes the tablet and permanently trips Knox. Never update to One UI 8, which removes OEM unlocking. Use at your own risk.

## Files

- `Program.cs` — the WinForms wizard (single file, .NET Framework 4.x, no NuGet packages).
- `patch-recovery.sh` — runs inside WSL. Extracts `recovery.img.lz4` (AP) and `vbmeta.img.lz4` (BL), patches recovery for fastbootd with [fastboot-patcher](https://github.com/ratcoded/fastboot-patcher), disables AVB verification in vbmeta, and packs `output/SM-X110-fastbootd-recovery.tar.md5` for Odin's AP slot. Embedded into the .exe as a resource.

## Build

```
./build.sh
```

Needs Mono (`mcs`). On Windows you can use the .NET Framework `csc.exe` with the same arguments.

## Credits

- [fastboot-patcher](https://github.com/ratcoded/fastboot-patcher) / [Patch-Recovery](https://github.com/Johx22/Patch-Recovery)
- [MisterZtr LineageOS GSI](https://github.com/MisterZtr/LineageOS_gsi)
- [XDA SM-X110 GSI guide](https://xdaforums.com/t/guide-sm-x110-how-to-install-gsi-root-on-tab-a9.4715422/)
