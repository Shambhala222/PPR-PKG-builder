# PPR-PKG-builder (MacOS Silicon)

An unofficial macOS port by Shambhala222 of
[Drakmor](https://github.com/drakmor)'s Windows PPR-PKG builder and
[SvenGDK's LibProsperoPkg](https://github.com/SvenGDK/LibProsperoPKG).
This is an independent community edition, not an official Drakmor or
SvenGDK release.

The app builds a debug PS5 `.pkg` on macOS (Apple Silicon) from a game
folder, an exFAT image, an ffpfsc image, or a GP5 project. There are
three tabs:

- **Plain Build**: Built-in Kraken packer (Windows Publishing Tools /
  Oodle are not used).
- **Drakmor's SDK Fix v12**: bundled SDK Fix 12 publisher (Wine) for
  dumps that include a 96-byte `sce_sys/keystone`.
- **Inspect**: open a `.pkg` and unpack the files.

Packing options are explained in `README-PACK.txt`.

## Download and run

Download `PPR-PKG-builder-0.8.2-macos-arm64.zip` from
[Releases](https://github.com/Shambhala222/PPR-PKG-builder/releases).
After extraction:

```
PPR-PKG-builder-0.8.2-macos-arm64/
  PPR-PKG Builder.app
  README.md
  README-PACK.txt
  LICENSE
  NOTICE
  CREDITS.txt
  THIRD_PARTY_NOTICES.txt
```

Open **PPR-PKG Builder.app**. This build is for macOS, Apple Silicon.
The .NET runtime is bundled. The SDK Fix tab also bundles Wine and
Python, so the zip is large. The app is ad-hoc signed and is not
Apple-notarized.

**First launch (macOS Gatekeeper).** After a GitHub download, macOS may
block the app to protect your Mac. That is Gatekeeper, not a broken or
infected download. Unlock it once:

1. Open **System Settings → Privacy & Security**.
2. Scroll down to the **Security** section.
3. macOS shows that the app was blocked to protect your Mac. Click
   **Open Anyway** next to that message.
4. A second dialog appears (**Move to Bin** / **Open Anyway** / **Done**).
   Click **Open Anyway**.
5. Confirm with Touch ID or your password. The app then opens.

After that you can always open the app with a normal double-click. macOS
will not show these warnings again.

Plain Build and Inspect run as native Apple Silicon. The SDK Fix tab
starts Intel Wine through Rosetta. If macOS asks to install Rosetta,
that is only for that tab.

## Credits and license

Unofficial macOS port by **Shambhala222**, based on **Drakmor's**
Windows PPR-PKG builder, **Drakmor's** SDK Fix 12 tools, and
**SvenGDK's** LibProsperoPkg.

Released under **GPLv3**, with upstream copyright notices and third-party
licenses retained. See LICENSE, NOTICE, CREDITS.txt and
THIRD_PARTY_NOTICES.txt. No warranty.

## Build from source

Requires .NET 10 SDK on macOS Apple Silicon.

```sh
dotnet publish src/GUI/LibProsperoPkg.Gui.csproj \
  -c Release -r osx-arm64 --self-contained true \
  -o publish
```

The Host overlay (`src/Host`) adds the SDK Fix tab. Runtime scripts live
in `src/SDKRuntime`. The release app also contains a private Wine and
CPython tree that is not in this repository.

The Windows packer libraries in `lib/` are required next to the published
binary as `LibProsperoPkg.Win05.dll` and `LibProsperoPkg.Win06.dll`.
macOS 27 needs the bundled `libcrypto.3.dylib` next to the executable so
SHA3 does not load Apple's blocked system libcrypto.
