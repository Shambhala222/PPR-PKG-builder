PPR-PKG Builder v0.8.3 for macOS by Shambhala222
==================================================

This is the macOS port of Drakmor's and SvenGDK's PPR-PKG builder (Windows)
by Shambhala222. Three tabs: Plain Build, Drakmor's SDK Fix v12, Inspect.

Plain Build is the Built-in Kraken packer (Windows Publishing Tools /
Oodle are not used). GP5, AC and PFS v3 use Drakmor's 0.6.5 library.

macOS 27: OpenSSL 3 (libcrypto.3.dylib) is bundled so SHA3 does not
load Apple's blocked system libcrypto. Same Plain Build packer and speed
as 0.6.5.2 on older macOS.


What is GP5?
------------
GP5 is NOT exFAT, NOT PFSC, NOT the inner image, NOT the dump.

A .gp5 file is Sony Publishing Tools’ project file (XML). It lists
which files go into the package, Content ID, entitlement key, PlayGo,
and optional compression hints. Same idea as a .gp4 on PS4.

If you only have a normal dump folder (sce_sys, eboot, data…), ignore
GP5. Click Browse and pick the folder.

Use GP5… only when you actually have a .gp5 sitting next to the files.


Everyday pack (a game dump on the Mac)
--------------------------------------
Typical times: a mid-size dump in minutes, a ~200 GB dump around an hour.

1. Source = dump folder (the one that contains sce_sys).
2. Output = where the .pkg should go. Not inside the dump.
3. Package type = Application (APP).
4. Image mode = PLAINTEXT_NOAUTH.
5. Kraken level 7, threads 0, PlayGo 64.
6. Skip Mac sidecar files = ON  (see below).
7. Deterministic = ON.
8. PFS format = v2.
9. Build PKG.

Leave PFS v3, auto-shuffle, GP5 and AC off unless you need them.
Those take longer. There is no faster option for them.


Skip Mac sidecar files (._*)
----------------------------
On a Mac, the Finder often creates extra junk next to real files:

  ._eboot.bin
  ._icon0.png
  .DS_Store

Those are Apple sidecar files. They are not game files. Windows does
not create them. If you pack with them included, the Mac package has
more files than the same dump packed on Windows.

ON  (normal on a Mac)
    Those sidecar files are left out. The package contains the real
    dump files only. Use this every day.

OFF
    Sidecars are packed too. Only do this if you are trying to match
    a Windows pack of the SAME dump, and that Windows pack counted
    the ._ files as well (someone copied a Mac dump to Windows
    without deleting them). Then both sides must include them, or
    both sides must delete them first.

If you just work on the Mac and want a clean package: leave it ON.


Coalesce outer blocks
---------------------
Windows 0.6.5: ON by default.

What it does: after files are compressed, leftover empty gaps in the
image can be filled with other small pieces, so less wasted space.

Effect: usually a slightly smaller inner image. Same game files.
Does not change plaintext vs encrypted. Does not skip files.

On this macOS app it only runs for GP5, AC or PFS v3.
A normal folder plus PFS v2 pack ignores it.


Adjust relocation / alignment
-----------------------------
Windows 0.6.5: ON by default.

What it does: some files must start on a fixed boundary (alignment).
That creates empty padding. This option may slide those files so the
padding shrinks.

Effect: again, usually a slightly smaller image. Same files.
Not a “cheat”. Not related to sidecars.

Same as coalesce: only on GP5, AC or PFS v3. Everyday v2 dumps
skip it.


PFS v2 vs PFS v3
----------------
PFS here is the compressed file-system image inside the package
(the inner image), not your USB stick format (exFAT) and not PFSC
as a separate tool.

PFS v2
  The normal format. Kraken compresses the files. No extra
  “shuffle” step. This is what you want for a normal dump.
  The usual choice for a normal dump.

PFS v3
  A newer metadata format around the same Kraken data. It can
  shuffle bytes before compression and store per-file hints.
  The .pkg will not be the same bytes as a v2 pack of the same
  dump. Much slower.

Use v2 unless a GP5 or a test explicitly needs v3.

Shuffle prediction / Auto-select shuffle
  Only apply to v3. They try different byte shuffles to squeeze
  a bit more compression. Very slow. Leave them off for everyday
  packs.

Skip PFSv3 input-data check
  Expert. Default OFF. Ignored on v2. Only if v3 refuses a file
  and you know why.


Source / Output / Temporary
---------------------------
Source      Game folder, exFAT image (.exfat), ffpfsc image, or a .gp5.
            Browse still accepts .exfat and .ffpfsc / .ffpfc / .ffpfs.
Output      Folder for the finished .pkg. Must not sit inside Source,
            or the .pkg would be packed into itself.
Free:       Output shows that disk (internal or external), free
            space, and the size of the .pkg. If Temporary is on the
            same disk, a second line shows the total (.pkg + temp).
            Temporary only shows its own share. Two disks: Output
            says temp is elsewhere. Game folder and mounted exFAT
            need about 2x the dump. ffpfsc needs about 3x the
            unpacked files (extract + .pkg + temp), not 3x the
            small image file. A short-by line appears if a disk
            is tight. Build PKG warns if a disk is short.
Temporary   Scratch files. Choosing Output copies this onto the
            output folder so a large inner image does not fill the
            Mac disk. Change it to another disk and that disk's
            free space is shown instead.


Content ID, Title, Version, Passcode
------------------------------------
Filled from sce_sys/param.json when possible.
Change them only if they are wrong.
Empty passcode = 32 zeros (normal debug / plaintext).

applicationDrmType is always packed as standard (free, upgradable, or
any other value). The original param.json in the dump is restored
afterwards. Already-"standard" dumps stay standard.


Inspect & Unpack
----------------
Open a .pkg and unpack the files. The same tab also unpacks an exFAT
(.exfat) or FFPFSC image into a folder. There is no Open Folder button.


SDK version + Override
----------------------
OFF  Keep the stamp from param.json / the executables.
ON   Rewrite it to the major you pick. Only if you mean to.


Package type
------------
Application (APP)   Normal game dump. Usual choice.
Homebrew            Your own homebrew tree, not a retail dump.
AC                  Extra content. Then Entitlement key (32 hex
                    characters) if the GP5 does not already have one.
                    Slower. No way around that.


Image mode
----------
PLAINTEXT_NOAUTH    No outer encryption. Everyday Homebrew / dump pack.
Native AES-XTS      Encrypted outer image. Only if you want that.


Kraken level / threads
----------------------
Level  -4 = faster / larger file,  7 = default,  9 = smaller / slower.
Threads  0 = auto. Leave it.


PlayGo chunks
-------------
64 is the usual default (same as Windows).
If sce_sys already has
  playgo-chunk.dat
  playgo-hash-table.dat
  playgo-ficm.dat
the builder keeps that layout. Otherwise it generates 1 scenario / N
chunks.


Other checkboxes
----------------
Deterministic build     ON. Same settings, same package.
Calculate final SHA-256 Optional hash at the end. Slow on 100 GB.
                        OFF unless you want that line in the log.


What Mac cannot copy from Windows
---------------------------------
Windows can use “Original Oodle Reduced” (Publishing Tools DLL).
That DLL is Windows-only. Plain Build always uses Built-in Kraken.

So: same dump + Built-in on both sides can match.
Windows default Oodle Reduced will not match the Mac .pkg byte for byte.

If the disk fills up, the build pauses. Free space, then Retry.
Cancel stops the pack. Temporary files are kept until you cancel.


Drakmor's SDK Fix v12
---------------------
Separate tab. Uses Drakmor's SDK Fix 12 tools through bundled Wine, not
the Built-in Kraken packer.

Needs a 96-byte sce_sys/keystone and param.json. Source can be a game
folder, an exFAT image (.exfat), or FFPFSC. exFAT is mounted first
(same as Plain). FFPFSC cannot be mounted and is extracted first.
Sony then packs the folder. The dump files are not modified. Generated
files go to Temporary / Output.

PlayGo chunks: 1 through 255, default 100 (same as the Windows Fix 12
GUI). Compression -4 through 9, default 7. Optional reference PKG builds
a patch plus a .remastered.pkg companion. Full APP packs pick
attributePub from unpacked size; patch builds keep the dump value.

Finder AppleDouble files (._*) and .DS_Store are left out of the GP5.
Windows dumps do not have those files.

Verify PKG / Extract PKG on this tab use the SDK publisher first.
Extract PKG that the publisher cannot open: use Inspect (open a .pkg
and unpack the files). That tab also unpacks exFAT and FFPFSC.

First SDK run may initialise Wine. That tab needs Rosetta on Apple
Silicon.


Do not
------
- Put Output or Temporary inside the dump.
- Pick GP5 unless you have a .gp5.
- Turn on PFS v3 / shuffle “just to try” on a full game.
- Cancel a long pack unless you want to throw that run away.
- Use SDK Fix without a 96-byte keystone. Use Plain Build instead.


Apps
----
PPR-PKG Builder.app         this version (0.8.3)
fpkg-gui-0.6.5.2-MacOS.app  previous 0.6.5.2, leave it alone
fpkg-gui-0.6.5.1-MacOS.app  previous 0.6.5.1, leave it alone
fpkg-gui-0.6.5-MacOS.app    older 0.6.5, leave it alone
