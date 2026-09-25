using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace LibProsperoPkg.Gui.Services;

internal sealed class BuildProgressTracker
{
    public const int StageCount = 7;

    private static readonly Regex ElapsedPrefix = new(@"^\[\+[0-9:.]+\]\s*", RegexOptions.Compiled);
    private static readonly Regex LibStage = new(@"\[stage\s+(\d+)\s*/\s*(\d+)\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Percent = new(@"(\d{1,3})\s*%", RegexOptions.Compiled);
    private static readonly Regex CountedBytes = new(@"(\d{1,3}(?:,\d{3})+|\d+)\s+bytes", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LargeFile = new(
        @"processing\s+large\s+file:?\s*(.+?)(?=\s+data\s|\s+\(|\s+\d{1,3}\s*%|\s+->|, ratio|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HexImage = new(@"image=0x([0-9A-Fa-f]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HashSet<string> _countedDumpFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _leftoverWorkFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private int _stage = 1;
    private double _stagePercent;
    private string _detailKey = "stage_prepare";
    private string? _outputDir;
    private string? _tempDir;
    private string? _contentId;
    private string? _sourceDir;
    private bool _innerComplete;
    private string? _currentDumpFile;
    private long _currentDumpFileSize;
    private long _dumpCompletedBytes;
    private long _expectedBytes;
    private long _sourceBytes;
    private long _packedBytes;
    private long _dumpProcessed;
    private ulong _innerIoBaseline;
    private ulong _fileIoBaseline;
    private long _lastLoggedPercent = -1;
    private DateTime _lastSyntheticUtc = DateTime.MinValue;
    private long _lastWorkSize;
    private DateTime _workStableUtc = DateTime.MinValue;
    private ulong _ioReadBaseline;
    private ulong _ioReadLast;
    private DateTime _ioLastUtc = DateTime.MinValue;
    private double _ioBytesPerSec;
    private string? _etaText;
    private long _ioDisplayCurrent;
    private string? _silentWork;
    private int _silentForStage;
    private bool _silentIntroEmitted;
    private bool _done;

    public readonly record struct Snapshot(
        int Stage,
        double StagePercent,
        double OverallPercent,
        string DetailKey,
        string? SyntheticLog,
        bool Determinate);

    public int CurrentStage
    {
        get
        {
            lock (_gate)
                return _stage;
        }
    }

    public void Start(string outputDir, string tempDir, string contentId, string? sourceDir = null)
    {
        lock (_gate)
        {
            _stage = 1;
            _stagePercent = 0;
            _detailKey = "stage_prepare";
            _outputDir = outputDir;
            _tempDir = tempDir;
            _contentId = contentId;
            _sourceDir = sourceDir;
            _expectedBytes = 0;
            _sourceBytes = 0;
            _packedBytes = 0;
            _dumpProcessed = 0;
            _innerIoBaseline = 0;
            _fileIoBaseline = 0;
            _innerComplete = false;
            _currentDumpFile = null;
            _currentDumpFileSize = 0;
            _dumpCompletedBytes = 0;
            _countedDumpFiles.Clear();
            _leftoverWorkFiles.Clear();
            SnapshotLeftoverWorkFiles(_outputDir);
            if (!string.Equals(_outputDir, _tempDir, StringComparison.OrdinalIgnoreCase))
                SnapshotLeftoverWorkFiles(_tempDir);
            _lastLoggedPercent = -1;
            _lastSyntheticUtc = DateTime.MinValue;
            _lastWorkSize = 0;
            _workStableUtc = DateTime.MinValue;
            _ioReadBaseline = 0;
            _ioReadLast = 0;
            _ioLastUtc = DateTime.MinValue;
            _ioBytesPerSec = 0;
            _etaText = null;
            _ioDisplayCurrent = 0;
            _silentWork = null;
            _silentForStage = 0;
            _silentIntroEmitted = false;
            _done = false;
        }
    }

    public bool IsFinished
    {
        get
        {
            lock (_gate)
                return _done;
        }
    }

    public void GetSizes(out long sourceBytes, out long packedBytes)
    {
        lock (_gate)
        {
            sourceBytes = _sourceBytes;
            packedBytes = _packedBytes > 0 ? _packedBytes : _lastWorkSize;
        }
    }

    public void SetSourceSize(long bytes)
    {
        if (bytes < 1024 * 1024)
            return;
        lock (_gate)
        {
            if (bytes > _sourceBytes)
                _sourceBytes = bytes;
            if (_stage <= 2 && bytes > _expectedBytes)
                _expectedBytes = bytes;
        }
    }

    public void SetPrepareScan(int files, long bytes)
    {
        lock (_gate)
        {
            if (_stage > 1)
                return;
            _detailKey = "stage_prepare";
            if (bytes > 0)
                _stagePercent = Math.Min(90, 5 + Math.Log10(Math.Max(bytes, 10)) * 8);
        }
    }

    public void ObserveLog(string message)
    {
        string text = Clean(message);
        if (text.Length == 0)
            return;

        lock (_gate)
        {
            NoteDumpFile(text);

            if (Contains(text, "Inner image complete") || Contains(text, "Inner image assembled"))
                _innerComplete = true;

            if (Contains(text, "Appended SI segment") || Contains(text, "Done (FIH)") || Contains(text, "Done (debug FIH)") || Contains(text, "Done (Retail FIH)"))
            {
                SetStage(7, 100, "stage_si");
                return;
            }

            if (Contains(text, "Outer SHA3 superblock scan") || Contains(text, "Outer SHA3 complete"))
            {
                SetStage(4, Contains(text, "complete") ? 100 : ParsePercent(text) ?? _stagePercent, "stage_outer_digest");
                _silentWork = "Outer SHA3";
                return;
            }

            if (Contains(text, "Calculating CNT SHA3") || Contains(text, "CNT SHA3 complete"))
            {
                SetStage(5, Contains(text, "complete") ? 100 : ParsePercent(text) ?? 0, "stage_cnt_digest");
                _silentWork = "CNT SHA3";
                return;
            }

            if (Contains(text, "PlayGo CRC"))
            {
                SetStage(7, ParsePercent(text) ?? _stagePercent, "stage_si");
                _silentWork = "SI / PlayGo CRC";
                return;
            }

            if (Contains(text, "Compact intermediate CNT"))
            {
                SetStage(5, ParsePercent(text) ?? _stagePercent, "stage_cnt");
                return;
            }

            if (Contains(text, "Finalizing the CNT") || Contains(text, "Writing finalized") || Contains(text, "Finalization inputs ready"))
            {
                SetStage(6, Contains(text, "Writing finalized") ? 5 : 0, "stage_fih");
                RememberHexSize(text);
                RememberFirstBytes(text);
                return;
            }

            if (_innerComplete && (Contains(text, "playgo-chunk.crc") || Contains(text, "BuildChunkCrc") || Contains(text, "SI will be") || Contains(text, "Building SI")))
            {
                SetStage(7, 0, "stage_si");
                _silentWork = "SI / PlayGo CRC";
                RestartSilentProgress();
                return;
            }

            if (_innerComplete && (Contains(text, "Calculating PFS image digests") || Contains(text, "Capturing image digests")))
            {
                string key = _stage <= 4 ? "stage_outer_digest" : "stage_cnt_digest";
                if (_stage < 4)
                    SetStage(4, 0, key);
                else
                    _detailKey = key;
                _silentWork = "SHA3-256";
                RestartSilentProgress();
                CapturePackedSize();
                return;
            }

            if (Contains(text, "Writing CNT bodies") || Contains(text, "Writing publisher outer PFS") || Contains(text, "CNT image complete") || Contains(text, "Validated intermediate"))
            {
                double pct = Contains(text, "CNT image complete") || Contains(text, "Validated intermediate") ? 100 : ParsePercent(text) ?? 0;
                SetStage(5, pct, "stage_cnt");
                RememberFirstBytes(text);
                return;
            }

            if (Contains(text, "Building plaintext") || Contains(text, "AES-XTS encrypting outer") || Contains(text, "Outer PFS complete") || Contains(text, "APR NAPS FIDX") || Contains(text, "outer-block digests") || Contains(text, "Rebuilding the plaintext outer"))
            {
                double pct = Contains(text, "Outer PFS complete") ? 100 : ParsePercent(text) ?? 0;
                SetStage(4, pct, "stage_outer");
                RememberFirstBytes(text);
                return;
            }

            if (Contains(text, "Generating NAPS") || Contains(text, "NAPS layout complete") || Contains(text, "NAPS plaintext integrity"))
            {
                SetStage(3, Contains(text, "NAPS layout complete") ? 100 : 0, "stage_naps");
                RememberFirstBytes(text);
                return;
            }

            if (Contains(text, "Flushing finalized FIH"))
            {
                SetStage(6, Contains(text, "complete") ? 100 : ParsePercent(text) ?? 10, "stage_fih");
                RememberFirstBytes(text);
                return;
            }

            if (Contains(text, "Inner layout planning") || Contains(text, "Inner size planning")
                || Contains(text, "Inner layout validation") || Contains(text, "Inner layout write")
                || Contains(text, "Inner data compression and write") || Contains(text, "Final inner layout"))
            {
                double pct = Contains(text, "complete") || Contains(text, "100%")
                    ? 100
                    : ParsePercent(text) ?? DumpProcessedPercent() ?? _stagePercent;
                SetStage(2, pct, "stage_inner");
                RememberFirstBytes(text);
                return;
            }

            if (Contains(text, "Inner image complete") || Contains(text, "Inner image assembled") || Contains(text, "Building and compressing the inner") || Contains(text, "Preparing PS5 inner") || Contains(text, "Planning nwonly") || Contains(text, "Compressing and writing AFID") || Contains(text, "Kraken level") || StartsReadOrData(text))
            {
                double pct = Contains(text, "Inner image complete") || Contains(text, "Inner image assembled")
                    ? 100
                    : DumpProcessedPercent() ?? _stagePercent;
                SetStage(2, pct, "stage_inner");
                RememberFirstBytes(text);
                return;
            }

            Match lib = LibStage.Match(text);
            if (lib.Success && int.TryParse(lib.Groups[1].Value, out int libStage))
            {
                int ours = libStage switch
                {
                    1 => 2,
                    2 => 3,
                    3 => 4,
                    4 => 5,
                    _ => 6,
                };
                string key = ours switch
                {
                    2 => "stage_inner",
                    3 => "stage_naps",
                    4 => "stage_outer",
                    5 => "stage_cnt",
                    _ => "stage_fih",
                };
                SetStage(ours, FileBackedPercent() ?? ParsePercent(text) ?? 0, key);
                RememberFirstBytes(text);
                return;
            }

            if (Contains(text, "Source scan") || Contains(text, "Build configuration") || Contains(text, "Building the PS5 package"))
            {
                SetStage(1, Contains(text, "Building the PS5 package") ? 80 : 30, "stage_prepare");
                RememberFirstBytes(text);
            }
        }
    }

    public Snapshot Poll()
    {
        lock (_gate)
        {
            if (_done)
                return new Snapshot(StageCount, 100, 100, _detailKey, null, true);

            TryFileAndFdProgress();
            double overall = ((_stage - 1) + (_stagePercent / 100.0)) / StageCount * 100.0;
            if (overall > 99.4 && _stage < StageCount)
                overall = 99.4;
            if (_stage == StageCount && _stagePercent >= 100)
                overall = 100;

            string? synthetic = null;
            int rounded = (int)Math.Round(_stagePercent, MidpointRounding.AwayFromZero);
            DateTime now = DateTime.UtcNow;
            bool first = _lastLoggedPercent < 0;
            bool due = first || rounded == 100 || (now - _lastSyntheticUtc).TotalSeconds >= 2
                || Math.Abs(rounded - _lastLoggedPercent) >= 1;
            if (due)
            {
                _lastSyntheticUtc = now;
                long packed = _packedBytes > 0 ? _packedBytes : _lastWorkSize;
                if (!_silentIntroEmitted && _silentForStage == _stage && _stage >= 4 && packed >= 1024 * 1024)
                {
                    _silentIntroEmitted = true;
                    _lastLoggedPercent = -1;
                    synthetic = $"The finished {FormatBytes(packed)} image is hashed several times (SHA3, PlayGo CRC, SI). Same file each round — not a hang.";
                }
                else
                {
                    _lastLoggedPercent = rounded;
                    string pctText = _stagePercent > 0 && _stagePercent < 1
                        ? _stagePercent.ToString("0.00", CultureInfo.CurrentCulture)
                        : _stagePercent < 10
                            ? _stagePercent.ToString("0.0", CultureInfo.CurrentCulture)
                            : rounded.ToString(CultureInfo.CurrentCulture);
                    synthetic = $"Progress {_stage}/{StageCount} — {DetailEnglish(_detailKey)} {pctText}%";
                    bool silent = _silentForStage == _stage && _ioDisplayCurrent > 0;
                    long current = silent ? _ioDisplayCurrent : LargestWorkFile();
                    if (current <= 0 && silent)
                        current = EstimateCurrentBytes(rounded);
                    synthetic += FormatSizeClause(current);
                    if (!string.IsNullOrEmpty(_etaText))
                        synthetic += " · " + _etaText;
                }
            }

            return new Snapshot(_stage, _stagePercent, Math.Clamp(overall, 0, 100), _detailKey, synthetic, true);
        }
    }

    public void Finish()
    {
        lock (_gate)
        {
            _done = true;
            _stage = StageCount;
            _stagePercent = 100;
            _detailKey = "stage_si";
            _etaText = null;
            _ioDisplayCurrent = 0;
            _silentForStage = 0;
        }
    }

    private void TryFileAndFdProgress()
    {
        if (_done)
            return;

        long size = LargestWorkFile();
        NoteWorkSize(size);

        if (_stage is 4 or 5 && size >= 1024 * 1024 && IsWorkStable(TimeSpan.FromSeconds(2)))
        {
            CapturePackedSize();
            BeginSilentIfNeeded();
            ApplySilentReadProgress();
            return;
        }

        if (_stage >= 6 && size >= 1024 * 1024 && IsWorkStable(TimeSpan.FromSeconds(2)))
        {
            CapturePackedSize();
            if (_stage < 7)
            {
                _silentWork = "SI / PlayGo CRC";
                SetStage(7, 0, "stage_si");
            }

            BeginSilentIfNeeded();
            ApplySilentReadProgress();
            return;
        }

        if (_stage == 7)
        {
            BeginSilentIfNeeded();
            ApplySilentReadProgress();
            return;
        }

        if (_stage == 2)
        {
            ApplyDumpReadProgress();
            return;
        }

        if (_stage < 2 || size <= 0)
            return;

        long denom = ProgressDenominator();
        if (denom < 1024 * 1024)
            return;

        double grown = 100.0 * size / denom;
        if (_stage is >= 3 and <= 6)
        {
            if (grown >= _stagePercent - 0.5)
                _stagePercent = Math.Min(99.4, grown);
        }
    }

    private void NoteDumpFile(string text)
    {
        Match match = LargeFile.Match(text);
        string rest;
        if (match.Success)
            rest = match.Groups[1].Value.Trim().Trim('"', '\'');
        else
        {
            const string marker = "processing large file:";
            int at = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
                return;
            rest = text[(at + marker.Length)..].Trim();
            int cut = rest.IndexOf(" data ", StringComparison.OrdinalIgnoreCase);
            if (cut < 0)
                cut = rest.IndexOf(" (", StringComparison.Ordinal);
            if (cut > 0)
                rest = rest[..cut].Trim();
            rest = rest.Trim('"', '\'', ' ', '\t');
        }

        if (rest.Length == 0)
            return;

        string path = rest;
        if (!Path.IsPathRooted(path) && !string.IsNullOrEmpty(_sourceDir))
            path = Path.Combine(_sourceDir, rest.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            if (File.Exists(path))
                path = Path.GetFullPath(path);
        }
        catch (Exception)
        {
        }

        string id = path;
        long size = 0;
        try
        {
            if (File.Exists(path))
                size = new FileInfo(path).Length;
        }
        catch (Exception)
        {
            size = 0;
        }

        if (size < 1024 * 1024)
        {
            Match bytes = CountedBytes.Match(text);
            if (bytes.Success
                && long.TryParse(bytes.Groups[1].Value.Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out long logged)
                && logged >= 1024 * 1024)
                size = logged;
        }

        if (!string.Equals(id, _currentDumpFile, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(_currentDumpFile)
                && _currentDumpFileSize > 0
                && _countedDumpFiles.Add(_currentDumpFile))
                _dumpCompletedBytes += _currentDumpFileSize;
            _currentDumpFile = id;
            _currentDumpFileSize = size;
            _fileIoBaseline = 0;
        }
        else if (size > _currentDumpFileSize)
            _currentDumpFileSize = size;

        // Only the percent on this file line — not a stray "2/7 87%" from elsewhere.
        double within = 0;
        int fileAt = text.IndexOf(rest, StringComparison.OrdinalIgnoreCase);
        string after = fileAt >= 0 ? text[(fileAt + rest.Length)..] : text;
        Match pct = Percent.Match(after);
        if (pct.Success && int.TryParse(pct.Groups[1].Value, out int p))
            within = Math.Clamp(p, 0, 100);

        long current = 0;
        if (_currentDumpFileSize > 0)
            current = (long)(_currentDumpFileSize * (within / 100.0));
        if (current == 0 && within == 0 && _currentDumpFileSize > 0)
            current = 0;
        long total = _dumpCompletedBytes + current;
        if (total > _sourceBytes && _sourceBytes > 0)
            total = _sourceBytes;
        if (total > _dumpProcessed)
            _dumpProcessed = total;
    }

    private void NoteWorkSize(long size)
    {
        if (size <= 0)
            return;
        if (size != _lastWorkSize)
        {
            _lastWorkSize = size;
            _workStableUtc = DateTime.UtcNow;
        }
    }

    private bool IsWorkStable(TimeSpan forAtLeast)
        => _lastWorkSize > 0 && (DateTime.UtcNow - _workStableUtc) >= forAtLeast;

    private void ApplySilentReadProgress()
    {
        (ulong Read, ulong Written)? snap = MacProcessIo.Snapshot();
        if (snap is null)
            return;

        ulong read = snap.Value.Read;
        DateTime now = DateTime.UtcNow;
        if (_ioReadBaseline == 0)
        {
            _ioReadBaseline = read;
            _ioReadLast = read;
            _ioLastUtc = now;
            _etaText = "reading the finished image";
            return;
        }

        double dt = (now - _ioLastUtc).TotalSeconds;
        if (dt >= 0.35 && read >= _ioReadLast)
        {
            double inst = (read - _ioReadLast) / Math.Max(dt, 0.001);
            _ioBytesPerSec = _ioBytesPerSec <= 1 ? inst : (_ioBytesPerSec * 0.6 + inst * 0.4);
            _ioReadLast = read;
            _ioLastUtc = now;
        }

        long packed = _packedBytes > 0 ? _packedBytes : _expectedBytes;
        if (packed < 1024 * 1024)
            packed = _lastWorkSize;
        if (packed < 1024 * 1024)
            return;

        long delta = read >= _ioReadBaseline ? (long)(read - _ioReadBaseline) : 0;
        int round = (int)(delta / packed) + 1;
        long within = delta % packed;
        _ioDisplayCurrent = within;
        _expectedBytes = packed;
        _stagePercent = Math.Min(99.4, 100.0 * within / packed);
        ApplySilentRoundLabels(round);

        string kind = string.IsNullOrEmpty(_silentWork)
            ? _stage switch
            {
                4 => "outer hash",
                5 => "CNT hash",
                6 => "FIH hash",
                _ => "SI / PlayGo CRC",
            }
            : _silentWork;
        string roundText = round <= 1
            ? kind + " over the finished " + FormatBytes(packed) + " image"
            : kind + " · same " + FormatBytes(packed) + " image · round " + round;

        if (_ioBytesPerSec >= 1024 * 1024)
        {
            double remain = (packed - within) / _ioBytesPerSec;
            _etaText = FormatEta(remain) + " left · " + roundText;
        }
        else
            _etaText = roundText;
    }

    private void ApplySilentRoundLabels(int round)
    {
        int stage;
        string key;
        string work;
        if (round <= 1)
        {
            stage = 4;
            key = "stage_outer_digest";
            work = "Outer SHA3";
        }
        else if (round == 2)
        {
            stage = 5;
            key = "stage_cnt_digest";
            work = "CNT SHA3";
        }
        else if (round == 3)
        {
            stage = 6;
            key = "stage_fih";
            work = "FIH";
        }
        else
        {
            stage = 7;
            key = "stage_si";
            work = "SI / PlayGo CRC";
        }

        if (stage < _stage)
            return;

        if (stage > _stage)
        {
            _stage = stage;
            _silentForStage = stage;
            _lastLoggedPercent = -1;
        }

        _detailKey = key;
        _silentWork = work;
    }

    private void BeginSilentIfNeeded()
    {
        if (_silentForStage == _stage)
            return;

        _silentForStage = _stage;
        RestartSilentProgress();
        if (string.IsNullOrEmpty(_silentWork))
        {
            _silentWork = _stage switch
            {
                4 => "outer hash",
                5 => "CNT hash",
                6 => "FIH hash",
                _ => "SI / PlayGo CRC",
            };
        }

        if (_stage == 4 && (_detailKey == "stage_outer" || string.IsNullOrEmpty(_detailKey)))
            _detailKey = "stage_outer_digest";
        if (_stage == 5 && (_detailKey == "stage_cnt" || string.IsNullOrEmpty(_detailKey)))
            _detailKey = "stage_cnt_digest";
    }

    private void RestartSilentProgress()
    {
        _stagePercent = 0;
        _ioReadBaseline = 0;
        _ioReadLast = 0;
        _ioDisplayCurrent = 0;
        _etaText = null;
        _silentForStage = _stage;
        _lastLoggedPercent = -1;
    }

    private void ApplyDumpReadProgress()
    {
        if (_sourceBytes < 1024 * 1024)
            return;

        (ulong Read, ulong Written)? snap = MacProcessIo.Snapshot();
        ulong read = snap?.Read ?? 0;
        if (read > 0 && _innerIoBaseline == 0)
            _innerIoBaseline = read;

        long fromFile = _dumpCompletedBytes;
        if (_currentDumpFileSize > 0)
        {
            long inFile = 0;
            if (read > 0)
            {
                if (_fileIoBaseline == 0)
                    _fileIoBaseline = read;
                if (read >= _fileIoBaseline)
                    inFile = (long)(read - _fileIoBaseline);
            }

            if (inFile > _currentDumpFileSize)
                inFile = _currentDumpFileSize;
            fromFile += inFile;
        }

        long fromInnerIo = 0;
        if (read > 0 && _innerIoBaseline > 0 && read >= _innerIoBaseline)
            fromInnerIo = (long)(read - _innerIoBaseline);
        if (fromInnerIo > _sourceBytes)
            fromInnerIo = _sourceBytes;

        long processed = fromFile;
        if (processed <= 0 && fromInnerIo > 0)
            processed = fromInnerIo;
        if (processed > _sourceBytes)
            processed = _sourceBytes;
        if (processed > _dumpProcessed)
            _dumpProcessed = processed;

        if (_dumpProcessed > 0 && _stage == 2)
            _stagePercent = Math.Min(99.4, 100.0 * _dumpProcessed / _sourceBytes);
    }

    private double? DumpProcessedPercent()
    {
        if (_sourceBytes < 1024 * 1024 || _dumpProcessed <= 0)
            return null;
        return Math.Min(99.4, 100.0 * _dumpProcessed / _sourceBytes);
    }

    private static string FormatEta(double seconds)
    {
        if (seconds < 20)
            return "< 30 sec";
        if (seconds < 90)
            return ((int)Math.Round(seconds / 5.0) * 5) + " sec";
        int min = Math.Max(1, (int)Math.Round(seconds / 60.0));
        return "~" + min + " min";
    }

    private long LargestWorkFile()
    {
        long best = 0;
        best = Math.Max(best, LargestIn(_outputDir));
        if (!string.Equals(_outputDir, _tempDir, StringComparison.OrdinalIgnoreCase))
            best = Math.Max(best, LargestIn(_tempDir));
        return best;
    }

    private void SnapshotLeftoverWorkFiles(string? dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return;
        try
        {
            foreach (string file in Directory.EnumerateFiles(dir))
            {
                if (!IsWorkFile(Path.GetFileName(file)))
                    continue;
                try
                {
                    _leftoverWorkFiles[file] = new FileInfo(file).Length;
                }
                catch (Exception)
                {
                    // Skip a file that vanished between the listing and the size read.
                }
            }
        }
        catch (Exception)
        {
            // Output folder can be busy or unreadable at start; packed then starts at 0.
        }
    }

    private bool IsThisRunWorkFile(string path, long length)
    {
        if (!_leftoverWorkFiles.TryGetValue(path, out long baseline))
            return true;
        return length != baseline;
    }

    private long LargestIn(string? dir, string? suffix = null)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return 0;
        long best = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(dir))
            {
                string name = Path.GetFileName(file);
                if (!IsWorkFile(name))
                    continue;
                if (!string.IsNullOrEmpty(suffix)
                    && !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    continue;
                long length = new FileInfo(file).Length;
                if (!IsThisRunWorkFile(file, length))
                    continue;
                if (length > best)
                    best = length;
            }
        }
        catch (Exception)
        {
            return best;
        }

        return best;
    }

    private static bool IsWorkFile(string name)
    {
        if (name.StartsWith("._", StringComparison.Ordinal))
            return false;
        return name.Contains("libprospero", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".cnt.tmp", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".pfs_image.dat", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".naps_pkg_layout.dat", StringComparison.OrdinalIgnoreCase);
    }

    private void SetStage(int stage, double percent, string detailKey)
    {
        if (stage > 2 && !_innerComplete)
            return;
        if (stage < _stage)
            return;
        if (stage > _stage)
        {
            _stage = stage;
            _stagePercent = Math.Clamp(percent, 0, 100);
            _detailKey = detailKey;
            _lastLoggedPercent = -1;
            _ioReadBaseline = 0;
            _ioReadLast = 0;
            _ioDisplayCurrent = 0;
            _etaText = null;
            _silentForStage = 0;
            _silentWork = null;
            if (_stage == 2)
            {
                _innerIoBaseline = 0;
                _fileIoBaseline = 0;
            }
            if (_stage >= 3)
                CapturePackedSize();
            return;
        }

        _detailKey = detailKey;
        if (percent >= _stagePercent - 0.25)
            _stagePercent = Math.Clamp(percent, 0, 100);
    }

    private void RememberFirstBytes(string text)
    {
        Match match = CountedBytes.Match(text);
        if (!match.Success)
            return;
        if (!long.TryParse(match.Groups[1].Value.Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out long bytes))
            return;
        // AppleDouble / sce_sys sidecars log a few hundred bytes. That must not become the image size
        // or the bar jumps to 99% of 808 bytes (Kong on a Mac dump).
        if (bytes < 1024 * 1024)
            return;
        if (_stage <= 2)
        {
            if (bytes > _expectedBytes)
                _expectedBytes = bytes;
            return;
        }

        if (_packedBytes > 0 && bytes >= _packedBytes / 2 && bytes <= _packedBytes + (256L * 1024 * 1024))
            _expectedBytes = Math.Max(_expectedBytes, bytes);
    }

    private void RememberHexSize(string text)
    {
        Match match = HexImage.Match(text);
        if (!match.Success)
            return;
        if (long.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long pfs) && pfs > 0)
        {
            if (_stage <= 2)
                _expectedBytes = Math.Max(_expectedBytes, pfs + 65536);
            else if (_packedBytes > 0)
                _expectedBytes = Math.Max(_expectedBytes, Math.Min(pfs + 65536, _packedBytes + (256L * 1024 * 1024)));
        }
    }

    private void CapturePackedSize()
    {
        long packed = LargestMatching(".pfs_image.dat");
        if (packed < 1024 * 1024)
            packed = LargestMatching(".cnt.tmp");
        if (packed < 1024 * 1024)
            return;
        _packedBytes = packed;
        _expectedBytes = packed;
    }

    private string FormatSizeClause(long current)
    {
        long wrote = current > 0 ? current : LargestWorkFile();
        if (_packedBytes > wrote)
            wrote = _packedBytes;

        var parts = new System.Collections.Generic.List<string>();

        if (_silentForStage == _stage && _ioDisplayCurrent > 0 && wrote >= 1024 * 1024)
        {
            string verb = _stage >= 7 ? "checksum" : "hashing";
            parts.Add(verb + " " + FormatBytes(_ioDisplayCurrent) + " / " + FormatBytes(wrote) + " packed");
        }
        else if (wrote > 0)
            parts.Add(FormatBytes(wrote) + " packed");

        if (_sourceBytes >= 1024 * 1024)
        {
            long processed = _dumpProcessed;
            parts.Add("dump processed " + FormatBytes(processed) + " / " + FormatBytes(_sourceBytes));
        }

        if (_stage <= 2 && _dumpProcessed > wrote && wrote >= 1024 * 1024)
        {
            double saved = 100.0 * (1.0 - (double)wrote / _dumpProcessed);
            if (saved > 0.5 && saved < 95)
                parts.Add(saved.ToString("0", CultureInfo.CurrentCulture) + "% saved");
        }

        if (parts.Count == 0)
            return "";
        return " (" + string.Join(" · ", parts) + ")";
    }

    private long ProgressDenominator()
    {
        if (_stage <= 2)
            return _sourceBytes > 0 ? _sourceBytes : _expectedBytes;
        if (_packedBytes > 0)
            return Math.Max(_packedBytes, LargestWorkFile());
        return _expectedBytes;
    }

    private long LargestMatching(string suffix)
    {
        long best = 0;
        best = Math.Max(best, LargestIn(_outputDir, suffix));
        if (!string.Equals(_outputDir, _tempDir, StringComparison.OrdinalIgnoreCase))
            best = Math.Max(best, LargestIn(_tempDir, suffix));
        return best;
    }

    private long EstimateCurrentBytes(int percent)
    {
        if (_expectedBytes <= 0)
            return 0;
        return (long)(_expectedBytes * (percent / 100.0));
    }

    private double? FileBackedPercent()
    {
        if (_expectedBytes < 1024 * 1024)
            return null;
        long size = LargestWorkFile();
        if (size <= 0)
            return null;
        return Math.Min(99.4, 100.0 * size / _expectedBytes);
    }

    private static double? ParsePercent(string text)
    {
        Match match = Percent.Match(text);
        if (!match.Success)
            return null;
        if (!int.TryParse(match.Groups[1].Value, out int value))
            return null;
        return Math.Clamp(value, 0, 100);
    }

    private static bool StartsReadOrData(string text)
        => text.StartsWith("read ", StringComparison.OrdinalIgnoreCase)
            || text.Contains(" read ", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("data ", StringComparison.OrdinalIgnoreCase)
            || text.Contains(" data ", StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string text, string value)
        => text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;

    internal static string Clean(string message)
    {
        string text = message.Trim();
        while (true)
        {
            string next = ElapsedPrefix.Replace(text, "");
            if (next.Length == text.Length)
                break;
            text = next.Trim();
        }

        if (text.StartsWith("[inner]", StringComparison.OrdinalIgnoreCase))
            text = text[7..].Trim();
        else if (text.StartsWith("[outer]", StringComparison.OrdinalIgnoreCase))
            text = text[7..].Trim();
        return text;
    }

    private static string DetailEnglish(string key) => key switch
    {
        "stage_inner" => "Inner image",
        "stage_naps" => "NAPS tables",
        "stage_outer" => "Outer PFS",
        "stage_outer_digest" => "Outer SHA3",
        "stage_cnt" => "CNT package",
        "stage_cnt_digest" => "CNT SHA3",
        "stage_fih" => "FIH image",
        "stage_si" => "SI checksum",
        _ => "Preparing",
    };

    internal static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return bytes.ToString("N0", CultureInfo.CurrentCulture) + " B";
        double value = bytes;
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString(unit >= 3 ? "N2" : "N1", CultureInfo.CurrentCulture) + " " + units[unit];
    }
}
