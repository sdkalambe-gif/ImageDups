# ImageDups

ImageDups is a Windows desktop application for finding duplicate and visually
similar images across two folders. It compares image content and perceptual
hashes, and lets you review matches before sending selected files to the
Recycle Bin.

## Requirements

- Windows
- Visual Studio with support for `.slnx` solutions and the **.NET desktop
  development** workload
- The .NET Framework 4.7.2 Developer Pack

## Build

Restore the NuGet packages for the solution, then build the `Release`
configuration. From a Visual Studio Developer PowerShell with NuGet available:

```powershell
nuget restore ImageDups.slnx
msbuild ImageDups.slnx /t:Build /p:Configuration=Release
```

The executable is written to `ImageDups\bin\Release\ImageDups.exe`.

The application stores its logs under `%TEMP%\ImageDups` and its
master-image index cache in the selected master folder.

## Download the latest build

Every push to `main` builds the Release configuration and updates the rolling
prerelease at [GitHub Releases](https://github.com/sdkalambe-gif/ImageDups/releases/tag/latest).
Download `ImageDups.zip` and extract it before running `ImageDups.exe`. The
archive includes the application and its required `log4net.dll` dependency.
You can also run the workflow manually from the Actions tab.
