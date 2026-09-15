using Avalonia.Media;
using Avalonia.Threading;
using LibProsperoPkg;
using LibProsperoPkg.Gui.Localization;
using LibProsperoPkg.Gui.Mvvm;
using LibProsperoPkg.Gui.Services;
using LibProsperoPkg.Gui.ViewModels.Fields;
using LibProsperoPkg.Metadata;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace LibProsperoPkg.Gui.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private static readonly Regex ContentIdPattern =
        new("^[A-Z]{2}[0-9]{4}-[A-Z]{4}[0-9]{5}_00-[A-Z0-9]{16}$", RegexOptions.Compiled);

    private static readonly Regex VersionPattern =
        new(@"^[0-9]{2}\.([0-9]{2}|[0-9]{3}\.[0-9]{3})$", RegexOptions.Compiled);

    private readonly IStorageService _storage;
    private readonly StringBuilder _log = new();
    private readonly List<string> _logLines = new();
    private readonly List<string?> _logKeys = new();
    private readonly BuildProgressTracker _progress = new();
    private bool _buildProgressLive;
    private CancellationTokenSource? _buildCancellation;

    private string _language = UiText.English;
    private string _sourceFolder = "";
    private string _outputFolder = "";
    private string _temporaryFolder = "";
    private string _contentId = "";
    private string _titleName = "";
    private string _version = "01.000.000";
    private string _passcode = new string('0', 32);
    private decimal _krakenLevel = 7;
    private decimal _krakenThreads = 0;
    private decimal _playGoChunks = 64;
    private int _sdkMajor = 1;
    private bool _overrideSdkVersion;
    private bool _deterministic = true;
    private bool _calculateFinalSha256;
    private bool _skipMacSidecars = true;
    private bool _analyzeShuffle;
    private bool _skipPfsInputCheck;
    private bool _outerCoalescing = true;
    private bool _relocationAlignment = true;
    private string _entitlementKey = "";
    private static readonly IBrush SpaceOkBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0x8A, 0x3C));
    private static readonly IBrush SpaceBadBrush = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x2B));
    private static readonly IBrush SpaceNeutralBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

    private string _freeSpace = "";
    private string _outputSpaceNote = "";
    private string _tempFreeSpace = "";
    private IBrush? _outputSpaceBrush;
    private IBrush? _outputNoteBrush;
    private IBrush? _tempSpaceBrush;
    private double _outputSpaceOpacity = 0.65;
    private double _outputNoteOpacity = 0.65;
    private double _tempSpaceOpacity = 0.65;
    private bool _isBusy;
    private string _logText = "";
    private string _status = "";
    private string _metadataStatus = "";
    private string _playGoStatus = "";
    private string _lastOutputPath = "";
    private double _progressValue;
    private string _progressLabel = "";
    private bool _progressIndeterminate;
    private ChoiceOption? _packageType;
    private ChoiceOption? _imageMode;
    private ChoiceOption? _pfsFormat;
    private ChoiceOption? _predictionLevel;
    private ChoiceOption? _sourceKind;
    private LanguageOption _selectedLanguage = UiText.Languages[0];
    private long _sourceBytes;
    private bool _sourceNeedsExtract;
    private CancellationTokenSource? _sourceMeasureCts;

    public MainWindowViewModel(IStorageService storage)
    {
        _storage = storage;
        PackageTypes = [];
        ImageModes = [];
        PfsFormats = [];
        PredictionLevels = [];
        Languages = new ObservableCollection<LanguageOption>(UiText.Languages);
        SdkMajors = new ObservableCollection<int>(Enumerable.Range(1, 11));
        SourceKinds = [];
        BrowseSourceCommand = new AsyncRelayCommand(BrowseSourceAsync);
        BrowseGp5Command = new AsyncRelayCommand(BrowseGp5Async);
        BrowseOutputCommand = new AsyncRelayCommand(() => BrowseFolder(UiText.Get(_language, "pick_output"), v => OutputFolder = v));
        BrowseTemporaryCommand = new AsyncRelayCommand(() => BrowseFolder(UiText.Get(_language, "pick_temp"), v => TemporaryFolder = v));
        BuildCommand = new AsyncRelayCommand(BuildAsync, () => !_isBusy);
        CancelCommand = new RelayCommand(CancelBuild, () => _isBusy);
        OpenOutputCommand = new RelayCommand(OpenOutput, () => !string.IsNullOrEmpty(_lastOutputPath) && File.Exists(_lastOutputPath));
        CopyLogCommand = new AsyncRelayCommand(CopyLogAsync);
        InitPackageTools();
        TemporaryFolder = Path.GetTempPath();
        ApplyLanguage();
        RefreshPlayGoStatus();
    }

    public ObservableCollection<LanguageOption> Languages { get; }
    public ObservableCollection<int> SdkMajors { get; }
    public ObservableCollection<ChoiceOption> PackageTypes { get; }
    public ObservableCollection<ChoiceOption> ImageModes { get; }
    public ObservableCollection<ChoiceOption> PfsFormats { get; }
    public ObservableCollection<ChoiceOption> PredictionLevels { get; }
    public ObservableCollection<ChoiceOption> SourceKinds { get; }

    public LanguageOption SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (value is null || !SetProperty(ref _selectedLanguage, value))
                return;
            _language = value.Code;
            ApplyLanguage();
        }
    }

    public string WindowTitle { get; private set; } = "";
    public string AppVersion { get; } = "v0.6.5.2";
    public string Heading { get; private set; } = "";
    public string Subtitle { get; private set; } = "";
    public string Copyright { get; private set; } = "";
    public string LanguageLabel { get; private set; } = "";
    public string SettingsLabel { get; private set; } = "";
    public string SourceLabel { get; private set; } = "";
    public string OutputLabel { get; private set; } = "";
    public string TemporaryLabel { get; private set; } = "";
    public string ContentIdLabel { get; private set; } = "";
    public string TitleLabel { get; private set; } = "";
    public string VersionLabel { get; private set; } = "";
    public string SdkVersionLabel { get; private set; } = "";
    public string OverrideSdkLabel { get; private set; } = "";
    public string FinalSha256Label { get; private set; } = "";
    public string PasscodeLabel { get; private set; } = "";
    public string PackageTypeLabel { get; private set; } = "";
    public string ImageModeLabel { get; private set; } = "";
    public string KrakenLevelLabel { get; private set; } = "";
    public string KrakenThreadsLabel { get; private set; } = "";
    public string PlayGoChunksLabel { get; private set; } = "";
    public string OptionsLabel { get; private set; } = "";
    public string DeterministicLabel { get; private set; } = "";
    public string SkipMacSidecarsLabel { get; private set; } = "";
    public string BrowseGp5Label { get; private set; } = "";
    public string SourceKindLabel { get; private set; } = "";
    public string EntitlementKeyLabel { get; private set; } = "";
    public string PfsFormatLabel { get; private set; } = "";
    public string PredictionLevelLabel { get; private set; } = "";
    public string AnalyzeShuffleLabel { get; private set; } = "";
    public string SkipPfsInputCheckLabel { get; private set; } = "";
    public string OuterCoalescingLabel { get; private set; } = "";
    public string RelocationAlignmentLabel { get; private set; } = "";
    public string BrowseLabel { get; private set; } = "";
    public string BuildLogLabel { get; private set; } = "";
    public string BuildLabel { get; private set; } = "";
    public string CancelLabel { get; private set; } = "";
    public string OpenOutputLabel { get; private set; } = "";
    public string CopyLogLabel { get; private set; } = "";
    public string MetadataStatus
    {
        get => _metadataStatus;
        private set
        {
            if (SetProperty(ref _metadataStatus, value))
                OnPropertyChanged(nameof(HasMetadataStatus));
        }
    }

    public bool HasMetadataStatus => !string.IsNullOrEmpty(_metadataStatus);

    public string PlayGoStatus
    {
        get => _playGoStatus;
        private set => SetProperty(ref _playGoStatus, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, value);
    }

    public string ProgressLabel
    {
        get => _progressLabel;
        private set => SetProperty(ref _progressLabel, value);
    }

    public bool IsProgressIndeterminate
    {
        get => _progressIndeterminate;
        private set => SetProperty(ref _progressIndeterminate, value);
    }

    public string SourceFolder
    {
        get => _sourceFolder;
        set
        {
            if (SetProperty(ref _sourceFolder, value))
            {
                LoadSourceMetadata();
                ScheduleSourceMeasure();
            }
        }
    }

    public string OutputFolder
    {
        get => _outputFolder;
        set
        {
            if (!SetProperty(ref _outputFolder, value))
                return;
            if (IsSystemTemp(_temporaryFolder) && !string.IsNullOrWhiteSpace(value))
                TemporaryFolder = value;
            RefreshFreeSpace();
        }
    }

    public string TemporaryFolder
    {
        get => _temporaryFolder;
        set
        {
            if (SetProperty(ref _temporaryFolder, value))
                RefreshFreeSpace();
        }
    }

    public string ContentId
    {
        get => _contentId;
        set => SetProperty(ref _contentId, value);
    }

    public string TitleName
    {
        get => _titleName;
        set => SetProperty(ref _titleName, value);
    }

    public string Version
    {
        get => _version;
        set => SetProperty(ref _version, value);
    }

    public string Passcode
    {
        get => _passcode;
        set => SetProperty(ref _passcode, value);
    }

    public ChoiceOption? PackageType
    {
        get => _packageType;
        set
        {
            if (SetProperty(ref _packageType, value))
                OnPropertyChanged(nameof(IsAcPackage));
        }
    }

    public ChoiceOption? ImageMode
    {
        get => _imageMode;
        set => SetProperty(ref _imageMode, value);
    }

    public ChoiceOption? PfsFormat
    {
        get => _pfsFormat;
        set
        {
            if (SetProperty(ref _pfsFormat, value))
                OnPropertyChanged(nameof(IsPfsV3));
        }
    }

    public ChoiceOption? PredictionLevel
    {
        get => _predictionLevel;
        set => SetProperty(ref _predictionLevel, value);
    }

    public ChoiceOption? SourceKind
    {
        get => _sourceKind;
        set => SetProperty(ref _sourceKind, value);
    }

    public bool IsPfsV3 => string.Equals(_pfsFormat?.Value as string, "Version3", StringComparison.Ordinal);

    public bool IsAcPackage =>
        _packageType?.Value is ProsperoPackageMode mode
        && mode == ProsperoPackageMode.AdditionalContentData;

    public string EntitlementKey
    {
        get => _entitlementKey;
        set => SetProperty(ref _entitlementKey, value);
    }

    public bool AnalyzeShuffle
    {
        get => _analyzeShuffle;
        set => SetProperty(ref _analyzeShuffle, value);
    }

    public bool SkipPfsInputCheck
    {
        get => _skipPfsInputCheck;
        set => SetProperty(ref _skipPfsInputCheck, value);
    }

    public bool OuterCoalescing
    {
        get => _outerCoalescing;
        set => SetProperty(ref _outerCoalescing, value);
    }

    public bool RelocationAlignment
    {
        get => _relocationAlignment;
        set => SetProperty(ref _relocationAlignment, value);
    }

    public string FreeSpace
    {
        get => _freeSpace;
        private set => SetProperty(ref _freeSpace, value);
    }

    public string OutputSpaceNote
    {
        get => _outputSpaceNote;
        private set
        {
            if (SetProperty(ref _outputSpaceNote, value))
                OnPropertyChanged(nameof(HasOutputSpaceNote));
        }
    }

    public bool HasOutputSpaceNote => !string.IsNullOrEmpty(_outputSpaceNote);

    public string TempFreeSpace
    {
        get => _tempFreeSpace;
        private set => SetProperty(ref _tempFreeSpace, value);
    }

    public IBrush? OutputSpaceBrush
    {
        get => _outputSpaceBrush;
        private set => SetProperty(ref _outputSpaceBrush, value);
    }

    public IBrush? OutputNoteBrush
    {
        get => _outputNoteBrush;
        private set => SetProperty(ref _outputNoteBrush, value);
    }

    public IBrush? TempSpaceBrush
    {
        get => _tempSpaceBrush;
        private set => SetProperty(ref _tempSpaceBrush, value);
    }

    public double OutputSpaceOpacity
    {
        get => _outputSpaceOpacity;
        private set => SetProperty(ref _outputSpaceOpacity, value);
    }

    public double OutputNoteOpacity
    {
        get => _outputNoteOpacity;
        private set => SetProperty(ref _outputNoteOpacity, value);
    }

    public double TempSpaceOpacity
    {
        get => _tempSpaceOpacity;
        private set => SetProperty(ref _tempSpaceOpacity, value);
    }

    public decimal KrakenLevel
    {
        get => _krakenLevel;
        set => SetProperty(ref _krakenLevel, value);
    }

    public decimal KrakenThreads
    {
        get => _krakenThreads;
        set => SetProperty(ref _krakenThreads, value);
    }

    public decimal PlayGoChunks
    {
        get => _playGoChunks;
        set
        {
            if (SetProperty(ref _playGoChunks, value))
                RefreshPlayGoStatus();
        }
    }

    public bool Deterministic
    {
        get => _deterministic;
        set => SetProperty(ref _deterministic, value);
    }

    public bool CalculateFinalSha256
    {
        get => _calculateFinalSha256;
        set => SetProperty(ref _calculateFinalSha256, value);
    }

    public int SdkMajor
    {
        get => _sdkMajor;
        set => SetProperty(ref _sdkMajor, value);
    }

    public bool OverrideSdkVersion
    {
        get => _overrideSdkVersion;
        set => SetProperty(ref _overrideSdkVersion, value);
    }

    public bool SkipMacSidecars
    {
        get => _skipMacSidecars;
        set => SetProperty(ref _skipMacSidecars, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
                return;
            if (!value)
            {
                ProgressValue = 0;
                ProgressLabel = "";
                IsProgressIndeterminate = false;
            }
            BuildCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
            OnBusyChanged();
        }
    }

    partial void OnBusyChanged();

    public string LogText
    {
        get => _logText;
        private set => SetProperty(ref _logText, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public AsyncRelayCommand BrowseSourceCommand { get; }
    public AsyncRelayCommand BrowseGp5Command { get; }
    public AsyncRelayCommand BrowseOutputCommand { get; }
    public AsyncRelayCommand BrowseTemporaryCommand { get; }
    public AsyncRelayCommand BuildCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand OpenOutputCommand { get; }
    public AsyncRelayCommand CopyLogCommand { get; }

    private string T(string key) => UiText.Get(_language, key);

    private void ApplyLanguage()
    {
        object? packageValue = PackageType?.Value;
        object? imageValue = ImageMode?.Value;
        object? pfsValue = PfsFormat?.Value;
        object? predictValue = PredictionLevel?.Value;
        object? sourceKindValue = SourceKind?.Value;

        WindowTitle = T("window");
        Heading = T("heading");
        Subtitle = T("subtitle");
        Copyright = T("copyright");
        RefreshTabCredit();
        LanguageLabel = T("language");
        SettingsLabel = T("settings");
        SourceLabel = T("source");
        OutputLabel = T("output");
        TemporaryLabel = T("temporary");
        ContentIdLabel = T("content_id");
        TitleLabel = T("title");
        VersionLabel = T("version");
        SdkVersionLabel = T("sdk_version");
        OverrideSdkLabel = T("override_sdk");
        FinalSha256Label = T("final_sha256");
        PasscodeLabel = T("passcode");
        PackageTypeLabel = T("package_type");
        ImageModeLabel = T("image_mode");
        KrakenLevelLabel = T("compression_level");
        KrakenThreadsLabel = T("compression_threads");
        PlayGoChunksLabel = T("playgo_chunks");
        OptionsLabel = T("options");
        DeterministicLabel = T("deterministic");
        SkipMacSidecarsLabel = T("skip_mac_sidecars");
        BrowseGp5Label = T("browse_gp5");
        SourceKindLabel = T("source_kind");
        EntitlementKeyLabel = T("entitlement_key");
        PfsFormatLabel = T("pfs_format");
        PredictionLevelLabel = T("prediction_level");
        AnalyzeShuffleLabel = T("analyze_shuffle");
        SkipPfsInputCheckLabel = T("skip_pfs_input_check");
        OuterCoalescingLabel = T("outer_coalescing");
        RelocationAlignmentLabel = T("relocation_alignment");
        BrowseLabel = T("browse");
        BuildLogLabel = T("build_log");
        BuildLabel = T("build");
        CancelLabel = T("cancel");
        OpenOutputLabel = T("open_output");
        CopyLogLabel = T("copy_log");
        if (string.IsNullOrEmpty(_status) || _status == UiText.Get(UiText.English, "status_ready")
            || _status == UiText.Get(UiText.Russian, "status_ready"))
            Status = T("status_ready");

        PackageTypes.Clear();
        PackageTypes.Add(new ChoiceOption(T("type_app"), ProsperoPackageMode.Application));
        PackageTypes.Add(new ChoiceOption(T("type_homebrew"), ProsperoPackageMode.Homebrew));
        PackageTypes.Add(new ChoiceOption(T("type_ac"), ProsperoPackageMode.AdditionalContentData));
        PackageType = FindByValue(PackageTypes, packageValue) ?? PackageTypes[0];

        ImageModes.Clear();
        ImageModes.Add(new ChoiceOption(T("mode_plain"), "plain"));
        ImageModes.Add(new ChoiceOption(T("mode_native"), "native"));
        ImageMode = FindByValue(ImageModes, imageValue) ?? FindByValue(ImageModes, "plain") ?? ImageModes[0];

        PfsFormats.Clear();
        PfsFormats.Add(new ChoiceOption(T("pfs_v2"), "Version2"));
        PfsFormats.Add(new ChoiceOption(T("pfs_v3"), "Version3"));
        PfsFormat = FindByValue(PfsFormats, pfsValue) ?? PfsFormats[0];

        PredictionLevels.Clear();
        PredictionLevels.Add(new ChoiceOption(T("predict_auto"), "auto"));
        for (int level = -4; level <= 9; level++)
            PredictionLevels.Add(new ChoiceOption(level.ToString(CultureInfo.InvariantCulture), level.ToString(CultureInfo.InvariantCulture)));
        PredictionLevel = FindByValue(PredictionLevels, predictValue) ?? PredictionLevels[0];

        SourceKinds.Clear();
        SourceKinds.Add(new ChoiceOption(T("source_folder"), "folder"));
        SourceKinds.Add(new ChoiceOption(T("source_exfat"), "exfat"));
        SourceKinds.Add(new ChoiceOption(T("source_ffpfsc"), "ffpfsc"));
        SourceKinds.Add(new ChoiceOption(T("source_gp5"), "gp5"));
        SourceKind = FindByValue(SourceKinds, sourceKindValue) ?? SourceKinds[0];

        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(Copyright));
        OnPropertyChanged(nameof(LanguageLabel));
        OnPropertyChanged(nameof(SettingsLabel));
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(OutputLabel));
        OnPropertyChanged(nameof(TemporaryLabel));
        OnPropertyChanged(nameof(ContentIdLabel));
        OnPropertyChanged(nameof(TitleLabel));
        OnPropertyChanged(nameof(VersionLabel));
        OnPropertyChanged(nameof(SdkVersionLabel));
        OnPropertyChanged(nameof(OverrideSdkLabel));
        OnPropertyChanged(nameof(FinalSha256Label));
        OnPropertyChanged(nameof(PasscodeLabel));
        OnPropertyChanged(nameof(PackageTypeLabel));
        OnPropertyChanged(nameof(ImageModeLabel));
        OnPropertyChanged(nameof(KrakenLevelLabel));
        OnPropertyChanged(nameof(KrakenThreadsLabel));
        OnPropertyChanged(nameof(PlayGoChunksLabel));
        OnPropertyChanged(nameof(OptionsLabel));
        OnPropertyChanged(nameof(DeterministicLabel));
        OnPropertyChanged(nameof(SkipMacSidecarsLabel));
        OnPropertyChanged(nameof(BrowseGp5Label));
        OnPropertyChanged(nameof(SourceKindLabel));
        OnPropertyChanged(nameof(EntitlementKeyLabel));
        OnPropertyChanged(nameof(PfsFormatLabel));
        OnPropertyChanged(nameof(PredictionLevelLabel));
        OnPropertyChanged(nameof(AnalyzeShuffleLabel));
        OnPropertyChanged(nameof(SkipPfsInputCheckLabel));
        OnPropertyChanged(nameof(OuterCoalescingLabel));
        OnPropertyChanged(nameof(RelocationAlignmentLabel));
        OnPropertyChanged(nameof(IsPfsV3));
        OnPropertyChanged(nameof(IsAcPackage));
        OnPropertyChanged(nameof(BrowseLabel));
        RefreshFreeSpace();
        OnPropertyChanged(nameof(BuildLogLabel));
        OnPropertyChanged(nameof(BuildLabel));
        OnPropertyChanged(nameof(CancelLabel));
        OnPropertyChanged(nameof(OpenOutputLabel));
        OnPropertyChanged(nameof(CopyLogLabel));
        ApplyPackageToolLanguage();
        RefreshPlayGoStatus();
    }

    private static ChoiceOption? FindByValue(ObservableCollection<ChoiceOption> items, object? value)
    {
        if (value is null)
            return null;
        foreach (ChoiceOption item in items)
        {
            if (Equals(item.Value, value))
                return item;
        }
        return null;
    }

    private async Task BrowseFolder(string title, Action<string> assign)
    {
        string? path = await _storage.OpenFolderAsync(title);
        if (!string.IsNullOrEmpty(path))
            assign(path);
    }

    private async Task BrowseSourceAsync()
    {
        string kind = SourceKind?.Value as string ?? "folder";
        if (kind == "gp5")
        {
            await BrowseGp5Async();
            return;
        }
        if (kind == "exfat")
        {
            string? path = await _storage.OpenFileAsync(T("pick_exfat"), T("exfat_filter"), ["exfat", "xfat"]);
            if (!string.IsNullOrEmpty(path))
                SourceFolder = path;
            return;
        }
        if (kind == "ffpfsc")
        {
            string? path = await _storage.OpenFileAsync(T("pick_ffpfsc"), T("ffpfsc_filter"), ["ffpfsc", "ffpfc", "ffpfs"]);
            if (!string.IsNullOrEmpty(path))
                SourceFolder = path;
            return;
        }
        await BrowseFolder(T("pick_source"), v => SourceFolder = v);
    }

    private async Task BrowseGp5Async()
    {
        string? path = await _storage.OpenFileAsync(T("pick_gp5"), T("gp5"), ["gp5"]);
        if (!string.IsNullOrEmpty(path))
            SourceFolder = path;
    }

    private void LoadSourceMetadata()
    {
        string source = _sourceFolder.Trim();
        if (string.IsNullOrEmpty(source))
        {
            SetDumpIcon(null);
            return;
        }
        if (File.Exists(source) && source.EndsWith(".gp5", StringComparison.OrdinalIgnoreCase))
        {
            LoadGp5Metadata(source);
            SetDumpIcon(null);
            RefreshPlayGoStatus();
            return;
        }
        if (ImageSourceSession.IsImagePath(source) || (File.Exists(source) && ImageSourceSession.DetectKind(source) != "folder"))
        {
            LoadImageMetadata(source);
            return;
        }
        if (!Directory.Exists(source))
        {
            SetDumpIcon(null);
            return;
        }

        string? sceSys = FindNamedDirectory(source, "sce_sys");
        if (sceSys is null)
        {
            foreach (string child in Directory.EnumerateDirectories(source))
            {
                sceSys = FindNamedDirectory(child, "sce_sys");
                if (sceSys is not null)
                {
                    SourceFolder = child;
                    return;
                }
            }
            return;
        }

        string paramPath = Path.Combine(sceSys, "param.json");
        if (!File.Exists(paramPath))
        {
            foreach (string file in Directory.EnumerateFiles(sceSys))
            {
                if (string.Equals(Path.GetFileName(file), "param.json", StringComparison.OrdinalIgnoreCase))
                {
                    paramPath = file;
                    break;
                }
            }
        }

        if (!File.Exists(paramPath))
        {
            MetadataStatus = T("param_missing");
            return;
        }

        try
        {
            var param = ProsperoParam.Load(paramPath);
            if (!string.IsNullOrWhiteSpace(param.ContentId))
                ContentId = param.ContentId;
            string? version = param.ContentVersion ?? param.MasterVersion;
            if (!string.IsNullOrWhiteSpace(version) && TryCanonicalContentVersion(version, out string canonical))
                Version = canonical;
            else if (!string.IsNullOrWhiteSpace(version))
                Version = version;
            string? title = param.GetTitleName(param.DefaultLanguage ?? "en-US") ?? param.GetTitleName("en-US");
            if (!string.IsNullOrWhiteSpace(title))
                TitleName = title;
            if (!OverrideSdkVersion && TryReadSdkMajor(param.SdkVersion, out int major))
                SdkMajor = major;
            string id = param.ContentId ?? ContentId;
            MetadataStatus = T("metadata_loaded") + id + "  " + (title ?? TitleName);
            RefreshPlayGoStatus();
            string icon = Path.Combine(source, "sce_sys", "icon0.png");
            if (File.Exists(icon))
            {
                try { SetDumpIcon(File.ReadAllBytes(icon)); }
                catch { SetDumpIcon(null); }
            }
            else
                SetDumpIcon(null);
        }
        catch (Exception ex)
        {
            MetadataStatus = T("param_fail") + ex.Message;
            SetDumpIcon(null);
        }
    }

    private void LoadImageMetadata(string imagePath)
    {
        try
        {
            ImageInspect? inspect = ImageSourceSession.Inspect(imagePath);
            if (inspect is null)
            {
                MetadataStatus = T("param_fail") + T("source_image_unreadable");
                SetDumpIcon(null);
                return;
            }
            if (!string.IsNullOrWhiteSpace(inspect.ParamJson))
            {
                var param = ProsperoParam.Parse(inspect.ParamJson);
                if (!string.IsNullOrWhiteSpace(param.ContentId))
                    ContentId = param.ContentId;
                string? version = param.ContentVersion ?? param.MasterVersion;
                if (!string.IsNullOrWhiteSpace(version) && TryCanonicalContentVersion(version, out string canonical))
                    Version = canonical;
                else if (!string.IsNullOrWhiteSpace(version))
                    Version = version;
                string? title = param.GetTitleName(param.DefaultLanguage ?? "en-US") ?? param.GetTitleName("en-US");
                if (!string.IsNullOrWhiteSpace(title))
                    TitleName = title;
                if (!OverrideSdkVersion && TryReadSdkMajor(param.SdkVersion, out int major))
                    SdkMajor = major;
                MetadataStatus = T("metadata_loaded") + (param.ContentId ?? ContentId) + "  " + TitleName;
            }
            else
                MetadataStatus = Path.GetFileName(imagePath);
            SetDumpIcon(inspect.IconPng);
            PlayGoStatus = string.Format(T("playgo_auto"), (int)PlayGoChunks);
        }
        catch (Exception ex)
        {
            MetadataStatus = T("param_fail") + ex.Message;
            SetDumpIcon(null);
        }
    }

    private void LoadGp5Metadata(string gp5Path)
    {
        try
        {
            XDocument doc = XDocument.Load(gp5Path);
            XElement? package = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "package");
            string? contentId = package?.Attribute("contentid")?.Value ?? package?.Attribute("content_id")?.Value;
            string? entitlement = package?.Attribute("entitlement_key")?.Value;
            if (!string.IsNullOrWhiteSpace(contentId) && ContentIdPattern.IsMatch(contentId))
                ContentId = contentId;
            if (!string.IsNullOrWhiteSpace(entitlement))
                EntitlementKey = entitlement.Trim();
            MetadataStatus = T("source_gp5") + Path.GetFileName(gp5Path);
        }
        catch (Exception ex)
        {
            MetadataStatus = T("param_fail") + ex.Message;
        }
    }

    private static bool TryReadSdkMajor(string? version, out int major)
    {
        major = 0;
        if (version == null || version.Length != 18 || !version.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || !byte.TryParse(version.AsSpan(2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte result))
            return false;
        int tens = result >> 4;
        int ones = result & 0xF;
        if (tens > 9 || ones > 9)
            return false;
        major = tens * 10 + ones;
        return major is >= 1 and <= 11;
    }

    private static bool TryCanonicalContentVersion(string? value, out string canonical)
    {
        canonical = "";
        if (string.IsNullOrWhiteSpace(value))
            return false;
        string[] parts = value.Trim().Split('.');
        if (parts.Length is < 2 or > 3)
            return false;
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int major) || major is < 0 or > 99)
            return false;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minor) || minor is < 0 or > 999)
            return false;
        int patch = 0;
        if (parts.Length == 3
            && (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out patch) || patch is < 0 or > 999))
            return false;
        canonical = $"{major:00}.{minor:000}.{patch:000}";
        return true;
    }

    private static string HashFile(string path, CancellationToken token, Action<long, long> progress)
    {
        using var stream = File.OpenRead(path);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1024 * 1024];
        long total = stream.Length;
        long done = 0;
        progress(0, total);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;
            hash.AppendData(buffer, 0, read);
            done += read;
            progress(done, total);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string? FindNamedDirectory(string parent, string name)
    {
        string direct = Path.Combine(parent, name);
        if (Directory.Exists(direct))
            return direct;
        if (!Directory.Exists(parent))
            return null;
        foreach (string dir in Directory.EnumerateDirectories(parent))
        {
            if (string.Equals(Path.GetFileName(dir), name, StringComparison.OrdinalIgnoreCase))
                return dir;
        }
        return null;
    }

    private void CancelBuild()
    {
        _buildCancellation?.Cancel();
        Status = T("status_canceling");
        Append(T("canceling_build"));
    }

    private async Task CopyLogAsync()
    {
        if (string.IsNullOrEmpty(_logText))
            return;
        await _storage.CopyTextAsync(_logText);
        Status = T("log_copied");
    }

    private string BuildSizeSummary(long pkgBytes)
    {
        _progress.GetSizes(out long dumpBytes, out _);
        if (dumpBytes < 1024 * 1024)
            return BuildProgressTracker.FormatBytes(pkgBytes);

        string line = string.Format(
            CultureInfo.CurrentCulture,
            T("summary_dump_pkg"),
            BuildProgressTracker.FormatBytes(dumpBytes),
            BuildProgressTracker.FormatBytes(pkgBytes));
        if (pkgBytes > 0 && pkgBytes < dumpBytes)
        {
            double saved = 100.0 * (1.0 - (double)pkgBytes / dumpBytes);
            if (saved > 0.5 && saved < 99)
                line += string.Format(CultureInfo.CurrentCulture, T("summary_saved"), saved.ToString("0", CultureInfo.CurrentCulture));
        }

        return line;
    }

    private void WriteBuildLog(string outputFolder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(outputFolder) || string.IsNullOrEmpty(_logText))
                return;
            string path = Path.Combine(outputFolder, "ppr-pkg-build.log");
            File.WriteAllText(path, _logText);
            Append(T("log_saved") + path);
        }
        catch (Exception)
        {
        }
    }

    private static string FormatBuildException(Exception ex)
    {
        var text = new System.Text.StringBuilder();
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is System.Reflection.TargetInvocationException && current.InnerException is not null)
                continue;
            if (text.Length > 0)
                text.Append(" → ");
            text.Append(current.GetType().Name).Append(": ").Append(current.Message);
        }

        return text.Length > 0 ? text.ToString() : ex.Message;
    }

    private void OpenOutput()
    {
        if (string.IsNullOrEmpty(_lastOutputPath) || !File.Exists(_lastOutputPath))
            return;
        Process.Start(new ProcessStartInfo
        {
            FileName = "open",
            Arguments = "-R \"" + _lastOutputPath.Replace("\"", "\\\"") + "\"",
            UseShellExecute = false,
        });
    }

    private async Task BuildAsync()
    {
        if (IsBusy)
            return;

        string? error = Validate(out string source, out string output, out string temp, out string contentId,
            out string passcode, out string version, out int krakenLevel, out int krakenThreads, out int playGoChunks);
        if (error is not null)
        {
            Status = T("check_settings");
            Append(error);
            return;
        }

        if (!await ConfirmSpaceOrContinueAsync())
            return;

        IsBusy = true;
        Status = T("status_building");
        _log.Clear();
        _logLines.Clear();
        _logKeys.Clear();
        LogText = "";
        IsProgressIndeterminate = false;
        ProgressValue = 0;
        _progress.Start(output, temp, contentId, source);
        _buildProgressLive = true;
        ApplyProgress(_progress.Poll(), writeLog: false);
        _buildCancellation = new CancellationTokenSource();
        var token = _buildCancellation.Token;
        string title = TitleName.Trim();
        var mode = PackageType?.Value is ProsperoPackageMode selectedMode
            ? selectedMode
            : ProsperoPackageMode.Application;
        string image = ImageMode?.Value as string ?? "plain";

        void Log(string message) => Dispatcher.UIThread.Post(() => Append(message));
        using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        CancellationToken monitorToken = monitorCts.Token;
        var monitor = Task.Run(async () =>
        {
            try
            {
                while (!monitorToken.IsCancellationRequested)
                {
                    await Task.Delay(400, monitorToken).ConfigureAwait(false);
                    BuildProgressTracker.Snapshot snap = _progress.Poll();
                    Dispatcher.UIThread.Post(() => ApplyProgress(snap, writeLog: true));
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, monitorToken);

        try
        {
            Append(T("build_started"));
            Append(T("source_prefix") + source);
            Append(T("output_prefix") + output);
            if (!string.IsNullOrEmpty(temp))
                Append(T("temp_prefix") + temp);
            Append(T("content_prefix") + contentId);
            Append(T("passcode_prefix") + DescribePasscode(passcode));
            Append(T("mode_prefix") + (image == "plain" ? "PLAINTEXT_NOAUTH" : "Native AES-XTS"));
            Append(T("kraken_prefix") + krakenLevel + T("kraken_suffix") + (krakenThreads == 0 ? "auto" : krakenThreads.ToString()) + T("workers"));
            Append(T("pfs_prefix") + (PfsFormat?.Label ?? "PFS v2"));
            Append(AnalyzeShuffle ? T("shuffle_on") : T("shuffle_off"));
            Append(T("layout_prefix") + (OuterCoalescing ? T("enabled") : T("disabled"))
                + T("layout_and") + (RelocationAlignment ? T("enabled") : T("disabled")));
            Append(T("playgo_prefix") + playGoChunks + T("playgo_suffix"));

            string outputPath = await Task.Run(() => RunBuild(
                source, output, temp, contentId, passcode, version, title, mode, image,
                krakenLevel, krakenThreads, playGoChunks, token, Log), token);

            token.ThrowIfCancellationRequested();
            string? verify = VerifyFih(outputPath);
            if (verify is not null)
                throw new InvalidDataException(verify);

            _lastOutputPath = outputPath;
            OpenOutputCommand.RaiseCanExecuteChanged();
            var info = new FileInfo(outputPath);
            try { monitorCts.Cancel(); } catch (Exception) { }
            if (CalculateFinalSha256)
            {
                string sha = await Task.Run(() => HashFile(outputPath, token, (done, total) =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        double pct = total > 0 ? done * 100.0 / total : 0;
                        ProgressValue = pct;
                        ProgressLabel = $"{pct:0}%";
                        Status = "SHA-256";
                    });
                }), token);
                Append("SHA-256: " + sha);
            }
            _progress.Finish();
            ClearLiveFooter();
            ApplyProgress(_progress.Poll(), writeLog: false);
            Append(T("success"));
            Append(T("verified"));
            Append(T("container") + Path.GetFileName(outputPath));
            Append(BuildSizeSummary(info.Length));
            Append(T("size") + info.Length.ToString("N0") + T("bytes"));
            Status = T("status_done");
            WriteBuildLog(output);
        }
        catch (OperationCanceledException)
        {
            try { monitorCts.Cancel(); } catch (Exception) { }
            ClearLiveFooter();
            Status = T("status_canceled");
            Append(T("canceled_by_user"));
            WriteBuildLog(output);
        }
        catch (Exception ex)
        {
            try { monitorCts.Cancel(); } catch (Exception) { }
            ClearLiveFooter();
            Status = T("status_error");
            Append(T("error_prefix") + FormatBuildException(ex));
            Append(T("build_error"));
            WriteBuildLog(output);
        }
        finally
        {
            try { monitorCts.Cancel(); } catch (Exception) { }
            try { await monitor.WaitAsync(TimeSpan.FromSeconds(1)); } catch (Exception) { }
            _buildCancellation?.Dispose();
            _buildCancellation = null;
            _buildProgressLive = false;
            IsBusy = false;
        }
    }

    private string? Validate(
        out string source, out string output, out string temp, out string contentId,
        out string passcode, out string version, out int krakenLevel, out int krakenThreads, out int playGoChunks)
    {
        source = SourceFolder.Trim();
        output = OutputFolder.Trim();
        temp = TemporaryFolder.Trim();
        contentId = ContentId.Trim();
        passcode = Passcode.Trim();
        version = Version.Trim();
        krakenLevel = 7;
        krakenThreads = 0;
        playGoChunks = 1;

        bool gp5 = File.Exists(source) && source.EndsWith(".gp5", StringComparison.OrdinalIgnoreCase);
        bool image = ImageSourceSession.IsImagePath(source)
            || (File.Exists(source) && ImageSourceSession.DetectKind(source) != "folder");
        if (string.IsNullOrEmpty(source) || (!Directory.Exists(source) && !gp5 && !image))
            return T("source_missing");
        if (string.IsNullOrEmpty(output))
            return T("output_required");

        try { source = Path.GetFullPath(source); }
        catch (Exception) { return T("invalid_path") + SourceFolder; }
        try { output = Path.GetFullPath(output); }
        catch (Exception) { return T("invalid_path") + OutputFolder; }

        string sourceRoot = ResolveSourceRoot(source) ?? source;
        if (IsUnder(output, sourceRoot))
            return T("output_inside");

        if (!string.IsNullOrEmpty(temp))
        {
            try { temp = Path.GetFullPath(temp); }
            catch (Exception) { return T("invalid_path") + TemporaryFolder; }
            if (IsUnder(temp, sourceRoot))
                return T("temp_inside");
        }

        if (IsAcPackage)
        {
            string key = EntitlementKey.Trim();
            if (key.Length > 0)
            {
                if (key.Length != 32)
                    return T("entitlement_form");
                if (!key.All(char.IsAsciiHexDigit))
                    return T("entitlement_hex");
            }
        }

        if (!ContentIdPattern.IsMatch(contentId))
            return T("content_id_form");
        if (passcode.Length == 0)
            passcode = new string('0', 32);
        if (passcode.Length != 32)
            return T("passcode_form");
        if (string.IsNullOrEmpty(version))
            version = "01.000.000";
        if (!TryCanonicalContentVersion(version, out version))
            return T("version_form");
        krakenLevel = (int)KrakenLevel;
        if (krakenLevel < -4 || krakenLevel > 9)
            krakenLevel = 7;
        krakenThreads = (int)KrakenThreads;
        if (krakenThreads < 0)
            krakenThreads = 0;
        playGoChunks = (int)PlayGoChunks;
        if (playGoChunks < 1)
            playGoChunks = 1;

        try
        {
            Directory.CreateDirectory(output);
            if (!string.IsNullOrEmpty(temp))
                Directory.CreateDirectory(temp);
        }
        catch (Exception ex)
        {
            return T("create_folder_failed") + ex.Message;
        }

        return null;
    }

    private void RefreshPlayGoStatus()
    {
        int chunks = (int)PlayGoChunks;
        if (chunks < 1)
            chunks = 1;
        if (ImageSourceSession.IsImagePath(_sourceFolder)
            || (File.Exists(_sourceFolder) && ImageSourceSession.DetectKind(_sourceFolder) != "folder"))
        {
            PlayGoStatus = string.Format(T("playgo_auto"), chunks);
            return;
        }

        string? root = ResolveSourceRoot(_sourceFolder);
        if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
        {
            string sce = Path.Combine(root, "sce_sys");
            string[] names = ["playgo-chunk.dat", "playgo-hash-table.dat", "playgo-ficm.dat"];
            var present = new List<string>();
            var missing = new List<string>();
            foreach (string name in names)
            {
                if (File.Exists(Path.Combine(sce, name)))
                    present.Add(name);
                else
                    missing.Add(name);
            }

            if (present.Count == 3)
            {
                PlayGoStatus = T("playgo_prepared");
                return;
            }

            if (present.Count > 0)
            {
                PlayGoStatus = T("playgo_partial") + string.Join(", ", missing);
                return;
            }
        }

        PlayGoStatus = string.Format(T("playgo_auto"), chunks);
    }

    private void ScheduleSourceMeasure()
    {
        _sourceMeasureCts?.Cancel();
        _sourceMeasureCts = null;
        _sourceBytes = 0;
        _sourceNeedsExtract = false;
        string source = _sourceFolder.Trim();
        if (string.IsNullOrEmpty(source))
        {
            RefreshFreeSpace();
            return;
        }

        if (File.Exists(source)
            && (ImageSourceSession.IsImagePath(source)
                || ImageSourceSession.DetectKind(source) is "exfat" or "ffpfsc"))
        {
            try { _sourceBytes = new FileInfo(source).Length; }
            catch (Exception) { _sourceBytes = 0; }
            string kind = ImageSourceSession.DetectKind(source);
            _sourceNeedsExtract = kind == "ffpfsc";
            RefreshFreeSpace();
            var imageCts = new CancellationTokenSource();
            _sourceMeasureCts = imageCts;
            Task.Run(() =>
            {
                long bytes;
                try { bytes = ImageSourceSession.MeasurePayloadBytes(source, imageCts.Token); }
                catch (OperationCanceledException) { return; }
                catch (Exception) { bytes = 0; }
                Dispatcher.UIThread.Post(() =>
                {
                    if (imageCts.IsCancellationRequested)
                        return;
                    if (bytes > 0)
                        _sourceBytes = bytes;
                    _sourceMeasureCts = null;
                    RefreshFreeSpace();
                });
            }, imageCts.Token);
            return;
        }

        string? root = ResolveSourceRoot(source);
        if (root is null || !Directory.Exists(root))
        {
            RefreshFreeSpace();
            return;
        }

        var cts = new CancellationTokenSource();
        _sourceMeasureCts = cts;
        RefreshFreeSpace();
        Task.Run(() =>
        {
            long bytes;
            try { bytes = MeasureSourceQuiet(root, cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (Exception) { bytes = 0; }
            Dispatcher.UIThread.Post(() =>
            {
                if (cts.IsCancellationRequested)
                    return;
                _sourceBytes = bytes;
                _sourceMeasureCts = null;
                RefreshFreeSpace();
            });
        }, cts.Token);
    }

    private static long MeasureSourceQuiet(string source, CancellationToken token)
    {
        long bytes = 0;
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            string name = Path.GetFileName(file);
            if (name.StartsWith("._", StringComparison.Ordinal)
                || name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase))
                continue;
            try { bytes += new FileInfo(file).Length; }
            catch (Exception) { }
        }
        return bytes;
    }

    private long EstimatePkgBytes()
    {
        return _sourceBytes > 0 ? _sourceBytes : 0;
    }

    private long EstimateTempBytes()
    {
        if (_sourceBytes <= 0)
            return 0;
        return _sourceNeedsExtract ? _sourceBytes * 2 : _sourceBytes;
    }

    private long EstimateNeededBytes()
    {
        return EstimatePkgBytes() + EstimateTempBytes();
    }

    private string EffectiveTempFolder()
    {
        string temp = _temporaryFolder.Trim();
        string output = _outputFolder.Trim();
        if (string.IsNullOrWhiteSpace(temp) || IsSystemTemp(temp))
            return output;
        return temp;
    }

    private string DisplayTempFolder()
    {
        string effective = EffectiveTempFolder();
        if (!string.IsNullOrWhiteSpace(effective))
            return effective;
        return _temporaryFolder.Trim();
    }

    private bool OutputAndTempShareDrive()
    {
        string output = _outputFolder.Trim();
        string temp = DisplayTempFolder();
        if (string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(temp))
            return true;
        DriveInfo? a = DriveForPath(output);
        DriveInfo? b = DriveForPath(temp);
        if (a is null || b is null)
            return true;
        if (string.Equals(
            NormalizeMount(a.Name),
            NormalizeMount(b.Name),
            StringComparison.OrdinalIgnoreCase))
            return true;
        // APFS: "/" and "/System/Volumes/Data" share one free-space pool.
        if (IsInternalDrive(a) && IsInternalDrive(b))
            return true;
        try
        {
            return !string.IsNullOrWhiteSpace(a.VolumeLabel)
                && string.Equals(a.VolumeLabel, b.VolumeLabel, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool TempUsesSystemFolder()
    {
        string temp = _temporaryFolder.Trim();
        return string.IsNullOrWhiteSpace(temp) || IsSystemTemp(temp);
    }

    private void RefreshFreeSpace()
    {
        string output = _outputFolder.Trim();
        long pkg = EstimatePkgBytes();
        long tempNeed = EstimateTempBytes();
        bool measuring = _sourceMeasureCts is not null && !string.IsNullOrWhiteSpace(_sourceFolder);
        bool sameDisk = OutputAndTempShareDrive();
        bool? outputOk = null;
        bool? tempOk = null;

        if (string.IsNullOrWhiteSpace(output))
        {
            FreeSpace = T("free_space_none");
            OutputSpaceNote = "";
        }
        else if (!TryGetDriveFree(output, out DriveInfo? outputDrive, out long outputFree))
        {
            FreeSpace = T("free_space_fail");
            OutputSpaceNote = "";
        }
        else
        {
            string kind = DriveKind(outputDrive);
            string free = BuildProgressTracker.FormatBytes(outputFree);
            if (pkg > 0)
            {
                FreeSpace = string.Format(T("space_output"), kind, free, BuildProgressTracker.FormatBytes(pkg));
                if (sameDisk)
                {
                    long total = pkg + tempNeed;
                    outputOk = outputFree >= total;
                    tempOk = outputOk;
                    OutputSpaceNote = WithShortfall(
                        string.Format(T("space_output_total"), BuildProgressTracker.FormatBytes(total)),
                        outputFree,
                        total);
                }
                else
                {
                    outputOk = outputFree >= pkg;
                    OutputSpaceNote = WithShortfall(
                        string.Format(T("space_output_temp_other"), BuildProgressTracker.FormatBytes(tempNeed)),
                        outputFree,
                        pkg);
                }
            }
            else if (measuring)
            {
                FreeSpace = string.Format(T("space_output_measuring"), kind, free);
                OutputSpaceNote = "";
            }
            else
            {
                FreeSpace = string.Format(T("space_output_free"), kind, free);
                OutputSpaceNote = "";
            }
        }

        if (TempUsesSystemFolder() && string.IsNullOrWhiteSpace(output))
        {
            string systemTemp = _temporaryFolder.Trim();
            if (string.IsNullOrWhiteSpace(systemTemp))
                systemTemp = Path.GetTempPath();
            if (!TryGetDriveFree(systemTemp, out DriveInfo? sysDrive, out long sysFree))
                TempFreeSpace = T("free_space_fail");
            else
            {
                TempFreeSpace = string.Format(
                    T("space_temp_system"),
                    DriveKind(sysDrive),
                    BuildProgressTracker.FormatBytes(sysFree));
            }
            ApplySpaceTones(outputOk, tempOk);
            return;
        }

        if (string.IsNullOrWhiteSpace(DisplayTempFolder()))
        {
            TempFreeSpace = T("space_temp_same_plain");
            ApplySpaceTones(outputOk, tempOk);
            return;
        }

        if (sameDisk && !string.IsNullOrWhiteSpace(output))
        {
            TempFreeSpace = tempNeed > 0
                ? string.Format(T("space_temp_same"), BuildProgressTracker.FormatBytes(tempNeed))
                : T("space_temp_same_plain");
            ApplySpaceTones(outputOk, tempOk);
            return;
        }

        if (!TryGetDriveFree(DisplayTempFolder(), out DriveInfo? tempDrive, out long tempFree))
        {
            TempFreeSpace = T("free_space_fail");
            ApplySpaceTones(outputOk, tempOk);
            return;
        }

        string tempKind = DriveKind(tempDrive);
        string tempFreeText = BuildProgressTracker.FormatBytes(tempFree);
        if (tempNeed > 0)
        {
            tempOk = tempFree >= tempNeed;
            TempFreeSpace = WithShortfall(
                string.Format(T("space_temp_other"), tempKind, tempFreeText, BuildProgressTracker.FormatBytes(tempNeed)),
                tempFree,
                tempNeed);
        }
        else if (measuring)
            TempFreeSpace = string.Format(T("space_output_measuring"), tempKind, tempFreeText);
        else
            TempFreeSpace = string.Format(T("space_temp_other_free"), tempKind, tempFreeText);

        ApplySpaceTones(outputOk, tempOk);
    }

    private void ApplySpaceTones(bool? outputOk, bool? tempOk)
    {
        OutputSpaceBrush = BrushFor(outputOk);
        OutputNoteBrush = BrushFor(outputOk);
        TempSpaceBrush = BrushFor(tempOk);
        OutputSpaceOpacity = outputOk is null ? 0.7 : 0.95;
        OutputNoteOpacity = outputOk is null ? 0.7 : 0.95;
        TempSpaceOpacity = tempOk is null ? 0.7 : 0.95;
    }

    private static IBrush BrushFor(bool? ok) => ok switch
    {
        true => SpaceOkBrush,
        false => SpaceBadBrush,
        _ => SpaceNeutralBrush,
    };

    private string WithShortfall(string text, long freeBytes, long neededBytes)
    {
        if (neededBytes <= 0 || freeBytes >= neededBytes)
            return text;
        return text + " " + string.Format(T("space_short"), BuildProgressTracker.FormatBytes(neededBytes - freeBytes));
    }

    private async Task<bool> ConfirmSpaceOrContinueAsync()
    {
        long pkg = EstimatePkgBytes();
        long tempNeed = EstimateTempBytes();
        if (pkg <= 0 && tempNeed <= 0)
            return true;

        string output = _outputFolder.Trim();
        if (!TryGetDriveFree(output, out DriveInfo? outputDrive, out long outputFree))
            return true;

        if (OutputAndTempShareDrive())
        {
            long needed = pkg + tempNeed;
            if (outputFree >= needed)
                return true;
            return await SpaceWarnPrompt.AskContinueAsync(
                T("disk_full_title"),
                string.Format(
                    T("space_warn_same"),
                    BuildProgressTracker.FormatBytes(needed),
                    DriveKind(outputDrive),
                    BuildProgressTracker.FormatBytes(pkg),
                    BuildProgressTracker.FormatBytes(tempNeed),
                    BuildProgressTracker.FormatBytes(outputFree)),
                T("continue_anyway"),
                T("cancel"));
        }

        var parts = new List<string>();
        if (outputFree < pkg)
        {
            parts.Add(string.Format(
                T("space_warn_output"),
                DriveKind(outputDrive),
                BuildProgressTracker.FormatBytes(pkg),
                BuildProgressTracker.FormatBytes(outputFree)));
        }

        if (TryGetDriveFree(DisplayTempFolder(), out DriveInfo? tempDrive, out long tempFree)
            && tempFree < tempNeed)
        {
            parts.Add(string.Format(
                T("space_warn_temp"),
                DriveKind(tempDrive),
                BuildProgressTracker.FormatBytes(tempNeed),
                BuildProgressTracker.FormatBytes(tempFree)));
        }

        if (parts.Count == 0)
            return true;

        return await SpaceWarnPrompt.AskContinueAsync(
            T("disk_full_title"),
            string.Join("\n\n", parts),
            T("continue_anyway"),
            T("cancel"));
    }

    private string DriveKind(DriveInfo? drive)
    {
        return IsInternalDrive(drive) ? T("disk_internal") : T("disk_external");
    }

    private static bool IsInternalDrive(DriveInfo? drive)
    {
        if (drive is null)
            return true;
        string name = NormalizeMount(drive.Name);
        if (name == "/"
            || name.StartsWith("/System/Volumes", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("/private", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!name.StartsWith("/Volumes/", StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            var root = new DriveInfo("/");
            string rootLabel = root.VolumeLabel;
            string thisLabel = drive.VolumeLabel;
            if (!string.IsNullOrWhiteSpace(rootLabel)
                && string.Equals(rootLabel, thisLabel, StringComparison.OrdinalIgnoreCase))
                return true;
            string volName = name["/Volumes/".Length..];
            if (!string.IsNullOrWhiteSpace(rootLabel)
                && string.Equals(volName, rootLabel, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch (Exception)
        {
        }

        return false;
    }

    private static bool TryGetDriveFree(string path, out DriveInfo? drive, out long freeBytes)
    {
        drive = DriveForPath(path);
        freeBytes = 0;
        if (drive is null)
            return false;
        try
        {
            if (!IsDriveReady(drive))
            {
                if (!IsInternalDrive(drive))
                    return false;
                var root = new DriveInfo("/");
                if (!IsDriveReady(root))
                    return false;
                drive = root;
            }

            freeBytes = drive.AvailableFreeSpace;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsDriveReady(DriveInfo drive)
    {
        try { return drive.IsReady; }
        catch (Exception) { return false; }
    }

    private static string NormalizeMount(string name)
    {
        name = name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return name.Length == 0 ? Path.DirectorySeparatorChar.ToString() : name;
    }

    // On macOS Path.GetPathRoot("/Volumes/SSD/foo") is "/" (the internal disk).
    // Pick the longest mounted volume prefix so an external SSD wins over "/".
    // "/" must match /Users and /var/folders: name+"/" becomes "//" and misses them.
    // Prefer a ready mount. Fall back to "/" for internal paths that only exist
    // via symlink/firmlink (/var -> /private/var).
    private static DriveInfo? DriveForPath(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception) { return null; }

        DriveInfo? bestReady = null;
        DriveInfo? bestAny = null;
        int bestReadyLength = -1;
        int bestAnyLength = -1;
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            string name = NormalizeMount(drive.Name);
            if (!IsUsefulMount(name) || !PathIsOnMount(full, name))
                continue;
            if (name.Length >= bestAnyLength)
            {
                bestAny = drive;
                bestAnyLength = name.Length;
            }
            if (!IsDriveReady(drive) || name.Length < bestReadyLength)
                continue;
            bestReady = drive;
            bestReadyLength = name.Length;
        }

        DriveInfo? found = bestReady ?? bestAny;
        if (found is not null)
            return found;
        if (full.StartsWith("/Volumes/", StringComparison.OrdinalIgnoreCase))
            return null;
        try { return new DriveInfo("/"); }
        catch (Exception) { return null; }
    }

    private static bool PathIsOnMount(string full, string mount)
    {
        if (string.Equals(full, mount, StringComparison.OrdinalIgnoreCase))
            return true;
        if (mount == "/")
            return full.StartsWith('/');
        return full.StartsWith(mount + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(mount + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUsefulMount(string name)
    {
        if (name == "/dev")
            return false;
        if (name.Contains("AppTranslocation", StringComparison.OrdinalIgnoreCase))
            return false;
        if (name.Equals("/System/Volumes/Data/home", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private static string? ResolveSourceRoot(string source)
    {
        source = source.Trim();
        if (string.IsNullOrEmpty(source))
            return null;
        if (File.Exists(source) && source.EndsWith(".gp5", StringComparison.OrdinalIgnoreCase))
            return Path.GetDirectoryName(Path.GetFullPath(source));
        if (ImageSourceSession.IsImagePath(source) || (File.Exists(source) && ImageSourceSession.DetectKind(source) != "folder"))
            return Path.GetFullPath(source);
        if (Directory.Exists(source))
            return Path.GetFullPath(source);
        return null;
    }

    private ImageSourceSession PrepareImageSource(
        string source, string kind, string workTemp, CancellationToken token, Action<string> log, bool forceExtract)
    {
        long lastPct = -1;
        return ImageSourceSession.Prepare(source, kind, workTemp, log, (written, total) =>
        {
            token.ThrowIfCancellationRequested();
            long pct = total <= 0 ? 100 : written * 100 / total;
            if (pct == lastPct)
                return;
            lastPct = pct;
            log("  " + pct.ToString(CultureInfo.InvariantCulture) + "%");
        }, token, forceExtract);
    }

    private string RunBuild(
        string source, string output, string temp, string contentId, string passcode, string version,
        string title, ProsperoPackageMode mode, string image, int krakenLevel, int krakenThreads,
        int playGoChunks, CancellationToken token, Action<string> log)
    {
        token.ThrowIfCancellationRequested();
        string workTemp = ResolveLargeBuildTemp(temp, output, log);
        ImageSourceSession? imageSession = null;
        ParamMountOverlay? paramOverlay = null;
        ParamDrmPatch? drmPatch = null;
        string packRoot = source;
        if (ImageSourceSession.IsImagePath(source) || (File.Exists(source) && ImageSourceSession.DetectKind(source) != "folder"))
        {
            string kind = SourceKind?.Value as string ?? ImageSourceSession.DetectKind(source);
            imageSession = PrepareImageSource(source, kind, workTemp, token, log, forceExtract: false);
            packRoot = imageSession.AppFolder;
            if (imageSession.IsReadOnlyMount && ParamDrmPatch.NeedsStandard(packRoot))
            {
                try
                {
                    paramOverlay = ParamMountOverlay.Create(packRoot, workTemp, SkipMacSidecars, log);
                    packRoot = paramOverlay.Root;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log("Could not overlay param.json (" + ex.Message + ") — extracting the exFAT image.");
                    paramOverlay?.Dispose();
                    paramOverlay = null;
                    imageSession.Dispose();
                    imageSession = PrepareImageSource(source, kind, workTemp, token, log, forceExtract: true);
                    packRoot = imageSession.AppFolder;
                }
            }
        }

        string sourceRoot = Directory.Exists(packRoot) ? Path.GetFullPath(packRoot) : (ResolveSourceRoot(source) ?? source);
        string? park = ParkFolderBeside(sourceRoot, contentId);
        if (Directory.Exists(park) && Directory.Exists(sourceRoot))
            MacSidecarFilter.Restore(park, sourceRoot, log);
        try
        {
            drmPatch = ParamDrmPatch.ApplyIfFree(sourceRoot, log);
            bool skipSidecarPark = imageSession?.IsReadOnlyMount == true;
            if (!skipSidecarPark && SkipMacSidecars && MacSidecarFilter.ShouldFilter(sourceRoot))
            {
                log("Mac sidecar files (._*) found — leaving them out of the package.");
                MacSidecarFilter.Park(sourceRoot, park, token, log, (n, _) => _progress.SetPrepareScan(n, 0));
            }
            else if (!skipSidecarPark && !SkipMacSidecars && MacSidecarFilter.ShouldFilter(sourceRoot))
                log("Mac sidecar files (._*) are included so the file set matches a Windows pack of this dump.");

            string measureRoot = Directory.Exists(packRoot) ? packRoot : (ResolveSourceRoot(source) ?? source);
            _progress.SetSourceSize(MeasureSource(measureRoot, token, log));
            bool gp5 = File.Exists(source) && source.EndsWith(".gp5", StringComparison.OrdinalIgnoreCase);
            string sourceFolder = gp5 ? (Path.GetDirectoryName(source) ?? measureRoot) : packRoot;
            string pfs = PfsFormat?.Value as string ?? "Version2";
            if (AnalyzeShuffle)
                pfs = "Version3";
            int? predict = null;
            if (PredictionLevel?.Value is string predictValue
                && int.TryParse(predictValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int predictLevel))
                predict = predictLevel;
            byte[]? entitlement = ParseEntitlementKey(EntitlementKey);
            bool layoutLibrary = gp5
                || mode == ProsperoPackageMode.AdditionalContentData
                || AnalyzeShuffle
                || string.Equals(pfs, "Version3", StringComparison.Ordinal);
            return Win02PackageBuilder.Build(new Win02PackageBuilder.Request
            {
                SourceFolder = sourceFolder,
                ProjectFilePath = gp5 ? source : null,
                SourceMode = gp5 ? "Gp5Project" : "Automatic",
                OutputFolder = output,
                TemporaryDirectory = workTemp,
                ContentId = contentId,
                Passcode = passcode,
                Title = title,
                Version = version,
                PackageMode = mode.ToString(),
                ImageMode = image,
                EntitlementKey = entitlement,
                KrakenLevel = krakenLevel,
                KrakenThreads = krakenThreads,
                PlayGoChunks = playGoChunks,
                Deterministic = Deterministic,
                SdkVersionOverride = OverrideSdkVersion ? Win02PackageBuilder.EncodeSdkMajor(SdkMajor, layoutLibrary) : null,
                PfsCompressionFormat = pfs,
                EnableShufflePatternAnalysis = AnalyzeShuffle,
                ShufflePredictionCompressionLevel = predict,
                SkipPfsInputDataAllowedCheck = SkipPfsInputCheck,
                EnableOuterBlockCoalescing = OuterCoalescing,
                EnableRelocationAlignmentAdjustment = RelocationAlignment,
                UseLayoutLibrary = layoutLibrary,
                ApplicationDrmType = "standard",
                OnDiskFull = path => AskDiskFullRetry(path, token, log),
                CancellationToken = token,
                Log = log,
            });
        }
        finally
        {
            drmPatch?.Dispose();
            if (Directory.Exists(sourceRoot))
                MacSidecarFilter.Restore(park, sourceRoot, log);
            paramOverlay?.Dispose();
            imageSession?.Dispose();
        }
    }

    private bool AskDiskFullRetry(string path, CancellationToken token, Action<string> log)
    {
        log(T("disk_full_log"));
        return DiskFullPrompt.AskRetry(
            T("disk_full_title"),
            T("disk_full_paused"),
            T("disk_full_file"),
            string.IsNullOrWhiteSpace(path) ? T("disk_full_no_file") : path,
            T("disk_full_hint"),
            T("disk_full_cancel_hint"),
            T("retry"),
            T("cancel"),
            token);
    }

    private static byte[]? ParseEntitlementKey(string? value)
    {
        string key = (value ?? "").Trim();
        if (key.Length != 32)
            return null;
        try
        {
            return Convert.FromHexString(key);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string ParkFolderBeside(string source, string contentId)
    {
        string trimmed = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar);
        string? parent = Path.GetDirectoryName(trimmed);
        if (string.IsNullOrEmpty(parent))
            parent = trimmed;
        string name = "." + contentId.Replace('/', '_').Replace('\\', '_') + ".sidecars";
        return Path.Combine(parent, name);
    }

    private long MeasureSource(string source, CancellationToken token, Action<string> log)
    {
        log("Measuring source folder so later stages can show live percent.");
        long bytes = 0;
        int files = 0;
        DateTime lastLog = DateTime.UtcNow;
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            string name = Path.GetFileName(file);
            if (name.StartsWith("._", StringComparison.Ordinal)
                || name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase))
                continue;
            try { bytes += new FileInfo(file).Length; }
            catch (Exception) { continue; }
            files++;
            if ((DateTime.UtcNow - lastLog).TotalMilliseconds < 350)
                continue;
            lastLog = DateTime.UtcNow;
            _progress.SetPrepareScan(files, bytes);
            log("Progress 1/7 — Preparing " + files.ToString("N0") + " files, " + FormatSourceBytes(bytes) + " scanned");
        }

        _progress.SetPrepareScan(files, bytes);
        log("Source payload: " + files.ToString("N0") + " files, " + FormatSourceBytes(bytes) + ".");
        return bytes;
    }

    private static string FormatSourceBytes(long bytes)
    {
        if (bytes < 1024)
            return bytes.ToString("N0") + " B";
        double value = bytes;
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString(unit >= 3 ? "N2" : "N1") + " " + units[unit];
    }

    private string? VerifyFih(string path)
    {
        if (!File.Exists(path))
            return T("no_pkg");
        var info = new FileInfo(path);
        if (info.Length < 0x10000)
            return T("fih_small");
        Span<byte> magic = stackalloc byte[4];
        using var stream = File.OpenRead(path);
        if (stream.Read(magic) < 4)
            return T("fih_header");
        if (magic[0] != 0x7F || magic[1] != (byte)'F' || magic[2] != (byte)'I' || magic[3] != (byte)'H')
            return T("fih_header");
        return null;
    }

    private static string DescribePasscode(string passcode)
    {
        if (passcode.Length == 32 && passcode.Trim('0').Length == 0)
            return new string('0', 32) + " (debug default)";
        return new string('●', 32) + " (32 ASCII)";
    }

    private static bool IsSystemTemp(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return true;
        string a = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        string b = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        return a.Equals(b, StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveLargeBuildTemp(string temp, string output, Action<string> log)
    {
        string chosen = string.IsNullOrWhiteSpace(temp) ? Path.GetTempPath() : temp;
        if (!IsSystemTemp(chosen))
            return Path.GetFullPath(chosen);

        string work = Path.GetFullPath(output);
        log(T("temp_moved_to_output") + work);
        return work;
    }

    private static bool IsUnder(string path, string root)
    {
        string p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        return p.Equals(r, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyProgress(BuildProgressTracker.Snapshot snap, bool writeLog)
    {
        if (!_buildProgressLive)
            return;
        if (_progress.IsFinished)
        {
            ProgressValue = 100;
            IsProgressIndeterminate = false;
            ProgressLabel = string.Format(
                CultureInfo.CurrentCulture,
                T("progress_short"),
                BuildProgressTracker.StageCount,
                BuildProgressTracker.StageCount,
                100);
            return;
        }

        ProgressValue = snap.OverallPercent;
        IsProgressIndeterminate = false;
        ProgressLabel = string.Format(
            CultureInfo.CurrentCulture,
            T("progress_short"),
            snap.Stage,
            BuildProgressTracker.StageCount,
            snap.StagePercent);
        if (IsBusy)
            Status = string.Format(
                CultureInfo.CurrentCulture,
                T("status_stage"),
                snap.Stage,
                BuildProgressTracker.StageCount,
                T(snap.DetailKey),
                snap.StagePercent);
        if (writeLog && !string.IsNullOrEmpty(snap.SyntheticLog))
            Append(snap.SyntheticLog);
    }

    private void Append(string message)
    {
        if (string.IsNullOrEmpty(message))
            return;

        if (_buildProgressLive && !message.StartsWith("Progress ", StringComparison.Ordinal))
        {
            _progress.ObserveLog(BuildProgressTracker.Clean(message));
            ApplyProgress(_progress.Poll() with { SyntheticLog = null }, writeLog: false);
        }

        string stamped = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
        string? key = ProgressKey(message);
        if (key is "ui:live" or "lib:live")
        {
            RemoveKey(key);
            _logLines.Add(stamped);
            _logKeys.Add(key);
            PinLiveFooter();
            PublishLog();
            return;
        }

        int footer = FirstLiveIndex();
        if (footer >= 0)
        {
            _logLines.Insert(footer, stamped);
            _logKeys.Insert(footer, key);
        }
        else
        {
            _logLines.Add(stamped);
            _logKeys.Add(key);
        }

        PinLiveFooter();
        PublishLog();
    }

    private int FirstLiveIndex()
    {
        for (int i = 0; i < _logKeys.Count; i++)
        {
            if (_logKeys[i] is "ui:live" or "lib:live")
                return i;
        }

        return -1;
    }

    private void ClearLiveFooter()
    {
        RemoveKey("ui:live");
        RemoveKey("lib:live");
        PublishLog();
    }

    private void PinLiveFooter()
    {
        string? libLine = null;
        string? uiLine = null;
        for (int i = _logKeys.Count - 1; i >= 0; i--)
        {
            if (_logKeys[i] == "lib:live")
            {
                libLine = _logLines[i];
                _logLines.RemoveAt(i);
                _logKeys.RemoveAt(i);
            }
            else if (_logKeys[i] == "ui:live")
            {
                uiLine = _logLines[i];
                _logLines.RemoveAt(i);
                _logKeys.RemoveAt(i);
            }
        }

        if (libLine is not null)
        {
            _logLines.Add(libLine);
            _logKeys.Add("lib:live");
        }

        if (uiLine is not null)
        {
            _logLines.Add(uiLine);
            _logKeys.Add("ui:live");
        }
    }

    private void RemoveKey(string key)
    {
        for (int i = _logKeys.Count - 1; i >= 0; i--)
        {
            if (_logKeys[i] != key)
                continue;
            _logLines.RemoveAt(i);
            _logKeys.RemoveAt(i);
        }
    }

    private void PublishLog()
    {
        _log.Clear();
        for (int i = 0; i < _logLines.Count; i++)
        {
            if (i > 0)
                _log.AppendLine();
            _log.Append(_logLines[i]);
        }

        LogText = _log.ToString();
    }

    private string? ProgressKey(string message)
    {
        string text = BuildProgressTracker.Clean(message);
        if (text.StartsWith("Progress ", StringComparison.Ordinal))
            return "ui:live";
        if (IsLibraryProgressTick(message))
            return "lib:live";
        return null;
    }

    private static bool IsLibraryProgressTick(string message)
    {
        string text = BuildProgressTracker.Clean(message);
        if (text.StartsWith("Progress ", StringComparison.Ordinal))
            return false;
        if (text.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase))
            return false;
        if (text.Contains("processing large file", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Kraken level", StringComparison.OrdinalIgnoreCase))
            return true;
        if (text.Contains('%', StringComparison.Ordinal)
            && (text.Contains("read ", StringComparison.OrdinalIgnoreCase)
                || text.Contains(" data ", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("data ", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Kraken", StringComparison.OrdinalIgnoreCase)
                || text.Contains("[stage ", StringComparison.OrdinalIgnoreCase)
                || text.Contains("%)", StringComparison.Ordinal)
                || text.Contains("% saved", StringComparison.OrdinalIgnoreCase)))
            return true;
        return false;
    }
}
