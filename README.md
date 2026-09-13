# PPR-PKG builder

An **unofficial macOS port by Shambhala222** of
[Drakmor](https://github.com/drakmor)’s Windows PPR-PKG builder and
[SvenGDK’s LibProsperoPkg](https://github.com/SvenGDK/LibProsperoPKG).
This is an independent community edition, not an official Drakmor or
SvenGDK release.

The app builds a debug PS5 `.pkg` from a dump folder (or a GP5 project)
on Apple Silicon. Everyday folder packs use the same fast path as Mac 0.5.
GP5, AC and PFS v3 use Drakmor’s 0.6.5 library. Built-in Kraken only —
Windows Publishing Tools / Oodle are not used.

Packing options are explained in `README-PACK.txt`.

## Download and run

Download `PPR-PKG-builder-0.6.5-macos-arm64.zip` from
[Releases](https://github.com/Shambhala222/PPR-PKG-builder/releases).
After extraction:

```
PPR-PKG-builder-0.6.5-macos-arm64/
  PPR-PKG builder.app
  README.md
  README-PACK.txt
  LICENSE
  NOTICE
  CREDITS.txt
  THIRD_PARTY_NOTICES.txt
```

Open **PPR-PKG builder.app**. This build is for **Apple Silicon**.
The .NET runtime is bundled. The app is ad-hoc signed and is not
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

**Erster Start (Gatekeeper).** Nach dem GitHub-Download kann macOS die App
blockieren, um deinen Mac zu schützen. Das ist Gatekeeper, kein
Virenfund. Einmal so freigeben:

1. **Systemeinstellungen → Datenschutz & Sicherheit** öffnen.
2. Nach unten scrollen bis zum Abschnitt **Sicherheit**.
3. Dort steht, dass die App blockiert wurde, um deinen Mac zu schützen.
   Direkt daneben **Dennoch öffnen** klicken.
4. Es kommt noch eine Meldung mit drei Optionen: **In den Papierkorb
   legen**, **Dennoch öffnen**, **Fertig**. **Dennoch öffnen** klicken.
5. Mit Touch ID oder Passwort bestätigen. Die App öffnet sich.

Danach kannst du die App immer ganz normal per Doppelklick öffnen.

## Credits and license

Unofficial macOS port by **Shambhala222**, based on **Drakmor’s**
Windows PPR-PKG builder and **SvenGDK’s** LibProsperoPkg.

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

The published folder is the app executable tree. To wrap it as
`PPR-PKG builder.app`, copy those files into `Contents/MacOS`, use
`src/GUI/osx/Info-0.6.5.plist` as `Contents/Info.plist`, add
`AppIcon.icns`, and ad-hoc sign with `src/GUI/osx/entitlements.plist`.

The Drakmor Windows libraries (`lib/fpkg-0.5` and `lib/fpkg-0.6.5`)
are required next to the published binary as `LibProsperoPkg.Win05.dll`
and `LibProsperoPkg.Win06.dll`.
