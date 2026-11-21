# ImageDups

ImageDups is a Windows desktop application for comparing image and video files
across two folders. It finds duplicate and visually similar images using
content and perceptual hashes, and finds videos only when their file contents
are identical. You can review matches before sending selected files to the
Recycle Bin.

The comparison controls let you choose images, videos, or both. Images are
selected by default; videos are opt-in.

Supported image extensions are `.bmp`, `.gif`, `.jpeg`, `.jpg`, `.png`, `.tif`,
and `.tiff`.

Supported video extensions are `.3gp`, `.avi`, `.flv`, `.m2ts`, `.m4v`, `.mkv`,
`.mov`, `.mp4`, `.mpeg`, `.mpg`, `.mts`, `.ogv`, `.ts`, `.vob`, `.webm`, and
`.wmv`. Video comparisons use cached SHA-256 hashes; videos are not compared by
visual similarity. Before a selected video is recycled, its match is
revalidated byte-for-byte.

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

The application stores its logs under `%TEMP%\ImageDups` and media index caches
in the selected master and current folders. Changed files are reprocessed, and
cache entries are removed when current files are sent to the Recycle Bin.

## Download the latest build

Every push to `main` builds the Release configuration and updates the rolling
prerelease at [GitHub Releases](https://github.com/sdkalambe-gif/ImageDups/releases/tag/latest).
The application, file, and MSI versions are set to `1.0.<GitHub run number>` for
each build; the application logs its version when it starts.
Download `ImageDups.msi` for a per-user installation under
`%LOCALAPPDATA%\Programs\ImageDups` with a Start Menu shortcut. The MSI does not
require administrator approval. Alternatively, download `ImageDups.zip` and
extract it before running `ImageDups.exe`; the archive includes the required
`log4net.dll` dependency. You can also run the workflow manually from the
Actions tab.
