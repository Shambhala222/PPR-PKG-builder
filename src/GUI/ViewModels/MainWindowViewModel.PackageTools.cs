using Avalonia.Media.Imaging;
using Avalonia.Threading;
using LibProsperoPkg.Gui.Localization;
using LibProsperoPkg.Gui.Mvvm;
using LibProsperoPkg.Gui.Services;
using LibProsperoPkg.Metadata;
using LibProsperoPkg.PKG;
using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LibProsperoPkg.Gui.ViewModels;

public sealed partial class MainWindowViewModel
{
    private string _packagePath = "";
    private string _unpackFolder = "";
    private string _workingFolder = "";
    private string _inspectTitle = "";
    private string _inspectContentId = "";
    private string _inspectVersion = "";
    private string _inspectSdk = "";
    private string _inspectType = "";
    private string _inspectRetail = "";
    private string _inspectEncrypted = "";
    private string _inspectSize = "";
    private string _inspectKey = "";
    private string _inspectTitleId = "";
    private string _unpackTargetHint = "";
    private int _selectedTabIndex;
    private Bitmap? _packageIcon;
    private Bitmap? _dumpIcon;
    private string _inspectedPackagePath = "";
    private int _inspectGeneration;

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (SetProperty(ref _selectedTabIndex, value))
                RefreshTabCredit();
        }
    }

    public string PackagePath
    {
        get => _packagePath;
        set
        {
            if (!SetProperty(ref _packagePath, value))
                return;
            _ = AutoInspectPackageAsync();
        }
    }

    public string UnpackFolder
    {
        get => _unpackFolder;
        set
        {
            if (SetProperty(ref _unpackFolder, value))
                RefreshUnpackTargetHint();
        }
    }

    public string WorkingFolder
    {
        get => _workingFolder;
        private set
        {
            if (SetProperty(ref _workingFolder, value))
                OpenWorkingCommand.RaiseCanExecuteChanged();
        }
    }

    public string InspectTitle
    {
        get => _inspectTitle;
        private set => SetProperty(ref _inspectTitle, value);
    }

    public string InspectContentId
    {
        get => _inspectContentId;
        private set => SetProperty(ref _inspectContentId, value);
    }

    public string InspectVersion
    {
        get => _inspectVersion;
        private set => SetProperty(ref _inspectVersion, value);
    }

    public string InspectSdk
    {
        get => _inspectSdk;
        private set => SetProperty(ref _inspectSdk, value);
    }

    public string InspectType
    {
        get => _inspectType;
        private set => SetProperty(ref _inspectType, value);
    }

    public string InspectRetail
    {
        get => _inspectRetail;
        private set => SetProperty(ref _inspectRetail, value);
    }

    public string InspectEncrypted
    {
        get => _inspectEncrypted;
        private set => SetProperty(ref _inspectEncrypted, value);
    }

    public string InspectSize
    {
        get => _inspectSize;
        private set => SetProperty(ref _inspectSize, value);
    }

    public string InspectKey
    {
        get => _inspectKey;
        private set => SetProperty(ref _inspectKey, value);
    }

    public string UnpackTargetHint
    {
        get => _unpackTargetHint;
        private set => SetProperty(ref _unpackTargetHint, value);
    }

    public bool HasUnpackTargetHint => !string.IsNullOrEmpty(_unpackTargetHint);

    public Bitmap? PackageIcon
    {
        get => _packageIcon;
        private set
        {
            Bitmap? previous = _packageIcon;
            if (!SetProperty(ref _packageIcon, value))
                return;
            previous?.Dispose();
            OnPropertyChanged(nameof(HasPackageIcon));
        }
    }

    public bool HasPackageIcon => _packageIcon is not null;

    public Bitmap? DumpIcon
    {
        get => _dumpIcon;
        private set
        {
            Bitmap? previous = _dumpIcon;
            if (!SetProperty(ref _dumpIcon, value))
                return;
            previous?.Dispose();
        }
    }

    public string BuildTabLabel { get; private set; } = "";
    public string UnpackTabLabel { get; private set; } = "";
    public string InspectTabLabel { get; private set; } = "";
    public string PackageLabel { get; private set; } = "";
    public string UnpackFolderLabel { get; private set; } = "";
    public string UnpackLabel { get; private set; } = "";
    public string OpenPackageLabel { get; private set; } = "";
    public string InspectHint { get; private set; } = "";
    public string OpenWorkingLabel { get; private set; } = "";
    public string RetailInspectLabel { get; private set; } = "";
    public string EncryptedInspectLabel { get; private set; } = "";
    public string SizeInspectLabel { get; private set; } = "";
    public string KeyInspectLabel { get; private set; } = "";
    public string UnpackCredit { get; private set; } = "";
    public string InspectCredit { get; private set; } = "";

    public AsyncRelayCommand BrowsePackageCommand { get; private set; } = null!;
    public AsyncRelayCommand BrowseUnpackFolderCommand { get; private set; } = null!;
    public AsyncRelayCommand UnpackCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenPackageCommand { get; private set; } = null!;
    public RelayCommand OpenWorkingCommand { get; private set; } = null!;

    private void InitPackageTools()
    {
        BrowsePackageCommand = new AsyncRelayCommand(() => BrowsePackageFile(UiText.Get(_language, "pick_package"), v => PackagePath = v));
        BrowseUnpackFolderCommand = new AsyncRelayCommand(() => BrowseFolder(UiText.Get(_language, "pick_unpack"), v => UnpackFolder = v));
        UnpackCommand = new AsyncRelayCommand(UnpackAsync, () => !IsBusy);
        OpenPackageCommand = new AsyncRelayCommand(OpenPackageAsync, () => !IsBusy);
        OpenWorkingCommand = new RelayCommand(OpenWorking, () => Directory.Exists(WorkingFolder));
    }

    partial void OnBusyChanged()
    {
        UnpackCommand.RaiseCanExecuteChanged();
        OpenPackageCommand.RaiseCanExecuteChanged();
    }

    private void ApplyPackageToolLanguage()
    {
        BuildTabLabel = T("tab_build");
        UnpackTabLabel = T("tab_unpack");
        InspectTabLabel = T("tab_inspect");
        PackageLabel = T("package");
        UnpackFolderLabel = T("unpack_folder");
        UnpackLabel = T("unpack");
        OpenPackageLabel = T("open_package");
        InspectHint = T("inspect_hint");
        OpenWorkingLabel = T("open_working");
        RetailInspectLabel = T("inspect_retail");
        EncryptedInspectLabel = T("inspect_encrypted");
        SizeInspectLabel = T("inspect_size");
        KeyInspectLabel = T("inspect_key");
        UnpackCredit = T("credit_unpack");
        InspectCredit = T("credit_inspect");
        RefreshTabCredit();
        OnPropertyChanged(nameof(BuildTabLabel));
        OnPropertyChanged(nameof(UnpackTabLabel));
        OnPropertyChanged(nameof(InspectTabLabel));
        OnPropertyChanged(nameof(PackageLabel));
        OnPropertyChanged(nameof(UnpackFolderLabel));
        OnPropertyChanged(nameof(UnpackLabel));
        OnPropertyChanged(nameof(OpenPackageLabel));
        OnPropertyChanged(nameof(InspectHint));
        OnPropertyChanged(nameof(OpenWorkingLabel));
        OnPropertyChanged(nameof(RetailInspectLabel));
        OnPropertyChanged(nameof(EncryptedInspectLabel));
        OnPropertyChanged(nameof(SizeInspectLabel));
        OnPropertyChanged(nameof(KeyInspectLabel));
        OnPropertyChanged(nameof(UnpackCredit));
        OnPropertyChanged(nameof(InspectCredit));
        RefreshUnpackTargetHint();
    }

    private void RefreshTabCredit()
    {
        Copyright = T("copyright");
        OnPropertyChanged(nameof(Copyright));
    }

    private async Task BrowsePackageFile(string title, Action<string> assign)
    {
        string? path = await _storage.OpenFileAsync(
            title,
            UiText.Get(_language, "package_filter"),
            ["pkg", "exfat", "ffpfsc", "ffpfs", "ffpfc"]);
        if (!string.IsNullOrWhiteSpace(path))
            assign(path);
    }

    private static bool IsFinalizedPackage(string path)
    {
        try
        {
            ProsperoPkgType? type = ProsperoPkgReader.DetectType(path);
            return type is ProsperoPkgType.FullRetail or ProsperoPkgType.FullDebug;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsUnpackImage(string path)
        => ImageSourceSession.IsImagePath(path)
            || (File.Exists(path) && ImageSourceSession.DetectKind(path) is "exfat" or "ffpfsc");

    private async Task AutoInspectPackageAsync()
    {
        string package = PackagePath.Trim();
        if (IsBusy || !File.Exists(package) || string.Equals(package, _inspectedPackagePath, StringComparison.Ordinal))
            return;
        await InspectPackageAsync(quiet: true);
    }

    private async Task UnpackAsync()
    {
        if (IsBusy)
            return;
        string package = PackagePath.Trim();
        string parent = UnpackFolder.Trim();
        string passcode = NormalizePasscode(Passcode);
        if (!File.Exists(package))
        {
            Append(T("error_prefix") + T("package_missing"));
            Status = T("status_error");
            return;
        }
        if (string.IsNullOrWhiteSpace(parent))
        {
            Append(T("error_prefix") + T("unpack_folder_required"));
            Status = T("status_error");
            return;
        }

        EnsureInspectNames(package, passcode);
        string folderName = GameDumpFolderName.Build(
            InspectTitle, InspectVersion, _inspectTitleId, InspectContentId);
        string output = GameDumpFolderName.ResolveOutputDirectory(parent, folderName);

        _buildCancellation = new CancellationTokenSource();
        CancellationToken token = _buildCancellation.Token;
        IsBusy = true;
        Status = T("status_unpacking");
        IsProgressIndeterminate = false;
        ProgressValue = 0;
        ProgressLabel = "0%";
        Append(T("unpack_started"));
        Append(IsFinalizedPackage(package) ? T("unpack_layout") : T("unpack_layout_image"));
        if (IsFinalizedPackage(package))
            Append(T("unpack_resume"));
        Append(T("package_prefix") + package);
        Append(T("output_prefix") + output);
        if (!string.Equals(Path.GetFullPath(parent), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            Append(string.Format(CultureInfo.CurrentCulture, T("unpack_into"), folderName));
        try
        {
            Directory.CreateDirectory(output);
            int extracted = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                Action<long, long> report = (written, total) =>
                {
                    token.ThrowIfCancellationRequested();
                    double pct = total > 0 ? 100.0 * written / total : 0;
                    if (pct > 100)
                        pct = 100;
                    string label = pct < 10
                        ? pct.ToString("0.0", CultureInfo.CurrentCulture) + "%"
                        : pct.ToString("0", CultureInfo.CurrentCulture) + "%";
                    Dispatcher.UIThread.Post(() =>
                    {
                        IsProgressIndeterminate = false;
                        ProgressValue = pct;
                        ProgressLabel = label;
                    });
                };

                if (IsUnpackImage(package) && !IsFinalizedPackage(package))
                {
                    return ImageSourceSession.ExtractToFolder(
                        package, output, line =>
                        {
                            token.ThrowIfCancellationRequested();
                            Dispatcher.UIThread.Post(() => Append(line));
                        }, report, token);
                }

                var options = new ProsperoExtractionOptions { ReportProgress = report };
                ProsperoPackageManifest manifest = ProsperoPackageExtractor.Extract(
                    package, output, ProsperoExtractionKey.FromPasscode(passcode), options, line =>
                    {
                        token.ThrowIfCancellationRequested();
                        Dispatcher.UIThread.Post(() => Append(line));
                    });
                return manifest.ExtractedFileCount;
            }, token);

            WorkingFolder = output;
            ProgressValue = 100;
            ProgressLabel = "100%";
            IsProgressIndeterminate = false;
            Append(T("unpack_done") + extracted.ToString("N0", CultureInfo.CurrentCulture));
            Status = T("status_done");
            await LoadInspectFromDiskAsync();
        }
        catch (OperationCanceledException)
        {
            Status = T("status_canceled");
            Append(T("canceled_by_user"));
        }
        catch (Exception ex)
        {
            Status = T("status_error");
            Append(T("error_prefix") + ex.Message);
        }
        finally
        {
            _buildCancellation?.Dispose();
            _buildCancellation = null;
            IsBusy = false;
        }
    }

    private Task OpenPackageAsync() => InspectPackageAsync(quiet: false);

    private async Task InspectPackageAsync(bool quiet)
    {
        if (IsBusy)
            return;
        string package = PackagePath.Trim();
        string passcode = NormalizePasscode(Passcode);
        if (!File.Exists(package))
        {
            if (!quiet)
            {
                Append(T("error_prefix") + T("package_missing"));
                Status = T("status_error");
            }
            return;
        }

        int generation = ++_inspectGeneration;
        _buildCancellation = new CancellationTokenSource();
        CancellationToken token = _buildCancellation.Token;
        IsBusy = true;
        Status = T("status_inspecting");
        IsProgressIndeterminate = true;
        if (!quiet)
            Append(T("inspect_started") + package);
        try
        {
            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                if (IsUnpackImage(package) && !IsFinalizedPackage(package))
                {
                    ImageInspect? image = ImageSourceSession.Inspect(package);
                    long size = new FileInfo(package).Length;
                    Dispatcher.UIThread.Post(() =>
                    {
                        InspectType = image?.Kind ?? ImageSourceSession.DetectKind(package);
                        InspectRetail = T("no");
                        InspectEncrypted = T("no");
                        InspectSize = FormatPackageSize(size);
                        InspectKey = T("no");
                        InspectContentId = "";
                    });
                    if (!string.IsNullOrWhiteSpace(image?.ParamJson))
                    {
                        try { ApplyParamPreview(ProsperoParam.Parse(image.ParamJson)); }
                        catch { }
                    }
                    Dispatcher.UIThread.Post(() => SetPackageIcon(image?.IconPng));
                    return;
                }

                ProsperoPackageExtractionInfo info = ProsperoPackageExtractor.Inspect(package);
                ProsperoPkg pkg = ProsperoPkgReader.Read(package);
                long pkgSize = new FileInfo(package).Length;
                Dispatcher.UIThread.Post(() =>
                {
                    InspectContentId = info.ContentId ?? pkg.Header?.ContentId ?? "";
                    InspectType = pkg.Type.ToString();
                    InspectRetail = info.IsRetail ? T("yes") : T("no");
                    InspectEncrypted = info.OuterEncrypted ? T("yes") : T("no");
                    InspectSize = FormatPackageSize(pkgSize);
                    InspectKey = info.RequiresSuppliedKey ? T("yes") : T("no");
                });

                ApplyParamPreview(PackageEntryReader.TryReadParam(package, passcode));
                byte[]? icon = PackageEntryReader.TryRead(package, "icon0.png", passcode);
                Dispatcher.UIThread.Post(() => SetPackageIcon(icon));
            }, token);
            if (generation == _inspectGeneration)
                _inspectedPackagePath = package;
            Status = T("status_done");
        }
        catch (OperationCanceledException)
        {
            Status = T("status_canceled");
            if (!quiet)
                Append(T("canceled_by_user"));
        }
        catch (Exception ex)
        {
            Status = T("status_error");
            Append(T("error_prefix") + ex.Message);
        }
        finally
        {
            _buildCancellation?.Dispose();
            _buildCancellation = null;
            IsProgressIndeterminate = false;
            IsBusy = false;
        }
    }

    private async Task LoadInspectFromDiskAsync()
    {
        if (!Directory.Exists(WorkingFolder))
            return;
        string paramPath = Path.Combine(WorkingFolder, "sce_sys", "param.json");
        if (File.Exists(paramPath))
        {
            try { ApplyParamPreview(ProsperoParam.Load(paramPath)); }
            catch (Exception ex) { Append(T("param_fail") + ex.Message); }
        }
        string iconPath = Path.Combine(WorkingFolder, "sce_sys", "icon0.png");
        if (File.Exists(iconPath))
            SetPackageIcon(await File.ReadAllBytesAsync(iconPath));
    }

    private void ApplyParamPreview(ProsperoParam? param)
    {
        if (param is null)
            return;
        string language = param.DefaultLanguage ?? "en-US";
        string? title = param.GetTitleName(language) ?? param.GetTitleName("en-US");
        Dispatcher.UIThread.Post(() => ApplyParamFields(param, title));
    }

    private void ApplyParamFields(ProsperoParam param, string? title)
    {
        if (!string.IsNullOrWhiteSpace(param.ContentId))
            InspectContentId = param.ContentId;
        InspectTitle = title ?? "";
        InspectVersion = param.ContentVersion ?? param.MasterVersion ?? "";
        _inspectTitleId = param.TitleId ?? "";
        InspectSdk = FormatSdkVersion(param.SdkVersion);
        RefreshUnpackTargetHint();
    }

    private void EnsureInspectNames(string package, string passcode)
    {
        if (!string.IsNullOrWhiteSpace(InspectTitle)
            && !string.IsNullOrWhiteSpace(GameDumpFolderName.ResolveTitleId(_inspectTitleId, InspectContentId)))
            return;
        if (IsUnpackImage(package) && !IsFinalizedPackage(package))
        {
            ImageInspect? image = ImageSourceSession.Inspect(package);
            if (string.IsNullOrWhiteSpace(image?.ParamJson))
                return;
            try
            {
                ApplyParamPreview(ProsperoParam.Parse(image.ParamJson));
            }
            catch
            {
            }
            return;
        }
        ProsperoParam? param = PackageEntryReader.TryReadParam(package, passcode);
        if (param is null)
            return;
        string language = param.DefaultLanguage ?? "en-US";
        ApplyParamFields(param, param.GetTitleName(language) ?? param.GetTitleName("en-US"));
    }

    private void RefreshUnpackTargetHint()
    {
        string name = GameDumpFolderName.Build(
            InspectTitle, InspectVersion, _inspectTitleId, InspectContentId);
        UnpackTargetHint = string.IsNullOrWhiteSpace(InspectTitle) && string.IsNullOrWhiteSpace(InspectContentId)
            ? ""
            : string.Format(CultureInfo.CurrentCulture, T("unpack_into"), name);
        OnPropertyChanged(nameof(HasUnpackTargetHint));
    }

    private void SetPackageIcon(byte[]? png)
    {
        PackageIcon = TryCreateBitmap(png);
    }

    private void SetDumpIcon(byte[]? png)
    {
        DumpIcon = TryCreateBitmap(png);
    }

    private static Bitmap? TryCreateBitmap(byte[]? png)
    {
        if (png is null || png.Length == 0)
            return null;
        try
        {
            using var stream = new MemoryStream(png, writable: false);
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    private void OpenWorking()
    {
        if (!Directory.Exists(WorkingFolder))
            return;
        ProcessOpen(WorkingFolder);
    }

    private static string FormatSdkVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";
        string value = raw.Trim();
        if (!value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return value;
        string hex = value[2..];
        if (hex.Length < 2 || (hex.Length & 1) != 0)
            return value;

        var parts = new string[4];
        int count = 0;
        int take = Math.Min(8, hex.Length);
        for (int i = 0; i < take; i += 2)
        {
            if (!byte.TryParse(hex.AsSpan(i, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte b))
                return value;
            int tens = b >> 4;
            int ones = b & 0xF;
            if (tens > 9 || ones > 9)
                return value;
            int n = tens * 10 + ones;
            parts[count] = count == 0
                ? n.ToString(CultureInfo.InvariantCulture)
                : n.ToString("00", CultureInfo.InvariantCulture);
            count++;
        }
        return string.Join(".", parts, 0, count);
    }

    private string FormatPackageSize(long bytes)
    {
        const double gib = 1024d * 1024d * 1024d;
        const double mib = 1024d * 1024d;
        if (bytes >= gib)
            return string.Format(CultureInfo.CurrentCulture, "{0:N2} GiB", bytes / gib);
        if (bytes >= mib)
            return string.Format(CultureInfo.CurrentCulture, "{0:N1} MiB", bytes / mib);
        return bytes.ToString("N0", CultureInfo.CurrentCulture) + T("bytes");
    }

    private static string NormalizePasscode(string passcode)
    {
        passcode = passcode.Trim();
        return passcode.Length == 0 ? new string('0', 32) : passcode;
    }

    private static void ProcessOpen(string path)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        });
    }
}
