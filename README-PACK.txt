PPR-PKG builder v0.6.5 (macOS) — README
=======================================

This is the Mac port of Drakmor's & SvenGDK's PPR-PKG builder (Windows)
by Shambhala222. Same job as Windows 0.6.5: turn a dump folder
(or a GP5 project) into a debug .pkg.

It is not a new packer. Everyday dumps use the same fast path as
Mac 0.5 (Built-in Kraken). GP5, AC and PFS v3 use Drakmor’s 0.6.5
library. Windows-only Oodle / Publishing Tools are not used here.


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
This is the fast path — same class of time as Mac 0.5
(e.g. a mid-size dump in minutes, a ~200 GB dump around an hour,
not many hours).

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
Those take the slow 0.6 path. There is no faster option for them.


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

On this Mac app it only runs for GP5, AC or PFS v3 (the 0.6 path).
A normal folder + PFS v2 pack ignores it — that is why everyday
packs stay fast.


Adjust relocation / alignment
-----------------------------
Windows 0.6.5: ON by default.

What it does: some files must start on a fixed boundary (alignment).
That creates empty padding. This option may slide those files so the
padding shrinks.

Effect: again, usually a slightly smaller image. Same files.
Not a “cheat”. Not related to sidecars.

Same as coalesce: only on GP5 / AC / PFS v3. Everyday v2 dumps
skip it.


PFS v2 vs PFS v3
----------------
PFS here is the compressed file-system image inside the package
(the inner image), not your USB stick format (exFAT) and not PFSC
as a separate tool.

PFS v2
  The normal format. Kraken compresses the files. No extra
  “shuffle” step. This is what you want for a normal dump.
  Same family and same speed class as the older Mac 0.5 packs.

PFS v3
  A newer metadata format around the same Kraken data. It can
  shuffle bytes before compression and store per-file hints.
  The .pkg will not be the same bytes as a v2 pack of the same
  dump. Much slower (layout / dedup). That is the 0.6 library;
  it cannot be made as fast as v2.

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
Source      Dump folder, or a .gp5 if you have one.
Output      Folder for the finished .pkg. Must not sit inside Source,
            or the .pkg would be packed into itself.
Free:       Free space on that disk (same bytes as Finder; the app
            shows them as GiB/TiB, Finder as GB/TB).
Temporary   Scratch files. If this is the Mac system temp, the app
            writes big files next to Output so the internal disk
            does not fill up.


Content ID, Title, Version, Passcode
------------------------------------
Filled from sce_sys/param.json when possible.
Change them only if they are wrong.
Empty passcode = 32 zeros (normal debug / plaintext).


SDK version + Override
----------------------
OFF  Keep the stamp from param.json / the executables.
ON   Rewrite it to the major you pick. Only if you mean to.


Package type
------------
Application (APP)   Normal game dump. Usual choice. Fast path.
Homebrew            Your own homebrew tree, not a retail dump.
                    Fast path, same as APP.
AC                  Extra content. Then Entitlement key (32 hex
                    characters) if the GP5 does not already have one.
                    Uses the 0.6 path — slower. No way around that.


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
Deterministic build     ON. Same settings → same package.
Calculate final SHA-256 Optional hash at the end. Slow on 100 GB.
                        OFF unless you want that line in the log.


What Mac cannot copy from Windows
---------------------------------
Windows can use “Original Oodle Reduced” (Publishing Tools DLL).
That DLL is Windows-only. This Mac app always uses Built-in Kraken.

So: same dump + Built-in on both sides can match.
Windows default Oodle Reduced will not match the Mac .pkg byte for byte.

Windows 0.6.5 can pause when the disk is full and ask to Retry.
That dialog is not in this Mac app.


Do not
------
- Put Output or Temporary inside the dump.
- Pick GP5 unless you have a .gp5.
- Turn on PFS v3 / shuffle “just to try” on a full game.
- Cancel a long pack unless you want to throw that run away.


Apps
----
fpkg-gui-0.6.5-MacOS.app    this version
fpkg-gui-0.6.2-MacOS.app    older 0.6 — leave it alone
fpkg-gui-0.5-MacOS.app      older app — leave it alone
