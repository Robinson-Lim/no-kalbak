using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using DnfItemChecker.App.Mvvm;
using DnfItemChecker.App.State;
using DnfItemChecker.Core.Comparison;
using DnfItemChecker.Core.Ocr;
using DnfItemChecker.Core.Data;
using DnfItemChecker.Core.Models;
using DnfItemChecker.Core.Stats;
using DnfItemChecker.Vision;

namespace DnfItemChecker.App.ViewModels;

/// <summary>
/// Tab3 (인게임 인식): once the tab is shown and the app window is not active it arms a live watch.
/// Item inspection always uses the tooltip under the cursor; registration is an explicit user mode that
/// treats that same single tooltip as the currently equipped item. Comparison readings remain isolated
/// to the recognizer's optional inspection API and never choose the registration target.
/// </summary>
public sealed class InGameTabViewModel : ViewModelBase
{
    // Capture box around the cursor (px). Narrow horizontally to keep the inventory/background out, but
    // tall enough to span the whole screen vertically (the capture service clamps the height to the
    // screen) so a long tooltip that flips upward above a low item keeps its top — name/등급/부위 —
    // in frame. The recognizer crops the tooltip out of this strip.
    private const int CaptureWidth = 620;
    private const int CaptureHeight = 1600;

    // Live-watch tuning. The game supplies its own tooltip hover delay; any application dwell made a
    // short cursor sweep feel delayed. Capture on the first stable poll and retry once if the tooltip
    // is still fading in.
    private const int PollMs = 50;
    private const int DwellMs = 0;
    private const int MoveThreshold = 4;
    private const int RecaptureThreshold = 6;
    private const int LiveRecognitionTimeoutMs = 2500;
    private const int RecognitionRetryBackoffMs = 250;

    private enum LiveLoopState
    {
        Stopped,
        Watching,
        Recognizing,
    }

    private readonly IScreenCaptureService _capture;
    private readonly IOcrService _ocr;
    private readonly ITooltipRecognizer _recognizer;
    private readonly IComparisonEngine _engine;
    private readonly IEquippedStatStore _equippedStore;
    private readonly IStatTable _statTable;
    private readonly AppState _state;

    private DispatcherTimer? _timer;
    private LiveLoopState _loopState = LiveLoopState.Stopped;
    private CancellationTokenSource? _liveRecognitionCts;
    private long _retryNotBeforeTick;
    private (int X, int Y) _lastPos;
    private long _stableSinceTick;
    private (int X, int Y) _recognizedAt = (int.MinValue, int.MinValue);
    private (int X, int Y) _retriedAt = (int.MinValue, int.MinValue);
    private CancellationTokenSource? _enrichmentCts;
    private long _recognitionVersion;

    private bool _isLive;
    private bool _isTabActive;
    // The shell starts active; live capture starts when the window is deactivated and DNF can be foreground.
    private bool _isWindowActive = true;
    private bool _isSaveMode;
    private string? _statusMessage;
    private string? _detectedRarity;
    private string? _detectedSlot;
    private string? _detectedGrade;
    private string? _observedDisplay;
    private string? _referenceDisplay;
    private ComparisonOutcome? _outcome;
    private bool _hasResult;

    private TooltipReading? _lastEquippedReading;
    private MainStat? _lastEquippedStat;
    private int? _lastEquippedValue;
    private EquippedItemMatch? _lastEquippedMatch;
    private long _equippedCandidateVersion;
    private string? _equippedCandidateDisplay;
    private string? _equippedMatchDisplay;
    private IReadOnlyList<DfEquippedItem>? _equipmentSnapshot;
    private string? _equipmentSnapshotKey;
    private HashSet<string> _registeredSlots = new(StringComparer.Ordinal);
    private long _registrationProgressVersion;
    public InGameTabViewModel(
        IScreenCaptureService capture, IOcrService ocr, ITooltipRecognizer recognizer,
        IComparisonEngine engine, IEquippedStatStore equippedStore,
        IStatTable statTable, AppState state)
    {
        _capture = capture;
        _ocr = ocr;
        _recognizer = recognizer;
        _engine = engine;
        _equippedStore = equippedStore;
        _statTable = statTable;
        _state = state;

        ToggleLiveCommand = new RelayCommand(() => IsLive = !IsLive,
            () => _ocr.IsAvailable && _state.HasSelection);
        ToggleSaveModeCommand = new RelayCommand(ToggleSaveMode,
            () => _ocr.IsAvailable && _state.HasSelection);
        SaveEquippedCommand = new AsyncRelayCommand(SaveEquippedAsync, CanSaveEquipped);
        _state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppState.SelectedCharacter))
            {
                CancelValueEnrichment();
                OnPropertyChanged(nameof(Header));
                ToggleLiveCommand.RaiseCanExecuteChanged();
                ToggleSaveModeCommand.RaiseCanExecuteChanged();
                _equipmentSnapshot = null;
                _equipmentSnapshotKey = null;
                ClearEquippedCandidate();
                _ = RefreshRegistrationProgressAsync(_state.SelectedCharacter);
                UpdateLoopState();
            }
            else if (e.PropertyName is nameof(AppState.EquippedItems))
            {
                // A tab2 refresh invalidates the API identity used by any in-flight refinement and save
                // candidate, even when the selected character itself did not change.
                CancelValueEnrichment();
                _equipmentSnapshot = _state.EquippedItems;
                _equipmentSnapshotKey = _state.EquippedItemsKey;
                ClearEquippedCandidate();
            }
            else if (e.PropertyName is nameof(AppState.MainStat))
            {
                CancelValueEnrichment();
                OnPropertyChanged(nameof(Header));
                ClearEquippedCandidate();
            }
        };
        _ = RefreshRegistrationProgressAsync(_state.SelectedCharacter);
    }

    public ObservableCollection<string> RawLines { get; } = new();

    public RelayCommand ToggleLiveCommand { get; }
    public RelayCommand ToggleSaveModeCommand { get; }
    public AsyncRelayCommand SaveEquippedCommand { get; }
    public bool IsOcrAvailable => _ocr.IsAvailable;

    public bool IsLive
    {
        get => _isLive;
        private set
        {
            if (SetProperty(ref _isLive, value))
            {
                OnPropertyChanged(nameof(LiveButtonText));
                UpdateLoopState();
            }
        }
    }
    public bool IsSaveMode
    {
        get => _isSaveMode;
        private set
        {
            if (SetProperty(ref _isSaveMode, value))
            {
                OnPropertyChanged(nameof(SaveModeButtonText));
            }
        }
    }

    public string SaveModeButtonText => _isSaveMode ? "착용 장비 등록 종료" : "착용 장비 등록 시작";

    public string? EquippedCandidateDisplay
    {
        get => _equippedCandidateDisplay;
        private set => SetProperty(ref _equippedCandidateDisplay, value);
    }
    public string? EquippedMatchDisplay
    {
        get => _equippedMatchDisplay;
        private set => SetProperty(ref _equippedMatchDisplay, value);
    }
    public string RegistrationProgress => $"착용 장비 등록: {_registeredSlots.Count}/{EquipmentSlots.All.Count}";

    public string RegisteredSlotsDisplay => string.Join(
        " · ",
        EquipmentSlots.All.Select(slot => $"{slot} {(_registeredSlots.Contains(slot) ? "✓" : "-")}"));

    public bool HasEquippedCandidate => _lastEquippedReading is not null
        && _lastEquippedStat is not null
        && _lastEquippedValue is > 0;

    public string LiveButtonText => _isLive ? "⏸ 실시간 인식 중지" : "▶ 실시간 인식 시작";

    public string Header => _state.SelectedCharacter is { } c
        ? $"{c.CharacterName} · 주능력치 {_state.MainStatKorean}"
        : "캐릭터를 먼저 선택하세요.";

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string? DetectedRarity
    {
        get => _detectedRarity;
        private set => SetProperty(ref _detectedRarity, value);
    }

    public string? DetectedSlot
    {
        get => _detectedSlot;
        private set => SetProperty(ref _detectedSlot, value);
    }

    public string? DetectedGrade
    {
        get => _detectedGrade;
        private set => SetProperty(ref _detectedGrade, value);
    }

    public string? ObservedDisplay
    {
        get => _observedDisplay;
        private set => SetProperty(ref _observedDisplay, value);
    }

    public string? ReferenceDisplay
    {
        get => _referenceDisplay;
        private set => SetProperty(ref _referenceDisplay, value);
    }

    public ComparisonOutcome? Outcome
    {
        get => _outcome;
        private set => SetProperty(ref _outcome, value);
    }

    public bool HasResult
    {
        get => _hasResult;
        private set => SetProperty(ref _hasResult, value);
    }

    private void ToggleSaveMode()
    {
        IsSaveMode = !IsSaveMode;
        if (IsSaveMode)
            IsLive = true;
        CancelValueEnrichment();
        _ = RefreshRegistrationProgressAsync(_state.SelectedCharacter);
        ClearEquippedCandidate();
        ResetRecognitionGate(); // re-read the current hover in the new mode
        ToggleSaveModeCommand.RaiseCanExecuteChanged();
        StatusMessage = IsSaveMode
            ? "착용 장비 등록 중: 장비창에서 현재 착용 아이템에 커서를 올리세요."
            : "일반 인식 모드입니다.";
    }

    private void ResetRecognitionGate()
    {
        _recognizedAt = (int.MinValue, int.MinValue);
        _retriedAt = (int.MinValue, int.MinValue);
    }

    /// <summary>
    /// Called by the shell when the app window gains or loses foreground activation. The cursor can
    /// stay on the same game item across Alt+Tab, so both activation edges re-arm that hover.
    /// </summary>
    public void SetWindowActive(bool active)
    {
        if (_isWindowActive == active) return;
        _isWindowActive = active;
        CancelValueEnrichment();
        ResetRecognitionGate();
        UpdateLoopState();
    }

    private void ClearEquippedCandidate()
    {
        _equippedCandidateVersion++;
        _lastEquippedReading = null;
        _lastEquippedStat = null;
        _lastEquippedValue = null;
        _lastEquippedMatch = null;
        EquippedCandidateDisplay = null;
        EquippedMatchDisplay = null;
        OnPropertyChanged(nameof(HasEquippedCandidate));
        SaveEquippedCommand?.RaiseCanExecuteChanged();
    }
    private async Task RefreshRegistrationProgressAsync(RosterCharacter? character)
    {
        long version = ++_registrationProgressVersion;
        _registeredSlots.Clear();
        OnPropertyChanged(nameof(RegistrationProgress));
        OnPropertyChanged(nameof(RegisteredSlotsDisplay));
        if (character is null) return;

        try
        {
            var observations = await _equippedStore.GetForCharacterAsync(
                character.ServerId, character.CharacterId);
            if (version != _registrationProgressVersion) return;

            var saved = observations.Select(observation => observation.Slot)
                .ToHashSet(StringComparer.Ordinal);
            _registeredSlots = new HashSet<string>(
                EquipmentSlots.All.Where(saved.Contains), StringComparer.Ordinal);
            OnPropertyChanged(nameof(RegistrationProgress));
            OnPropertyChanged(nameof(RegisteredSlotsDisplay));
        }
        catch (Exception ex)
        {
            if (version == _registrationProgressVersion && IsSaveMode)
                StatusMessage = $"착용 장비 등록 현황을 불러오지 못했습니다: {ex.Message}";
        }
    }


    private bool CanSaveEquipped()
        => IsSaveMode && _state.HasSelection && _lastEquippedReading is not null
            && _lastEquippedStat is not null && _lastEquippedValue is > 0;

    /// <summary>Called by the shell when tab3 is shown/hidden. Entering auto-arms live recognition.</summary>
    public void SetTabActive(bool active)
    {
        _isTabActive = active;
        if (active && _ocr.IsAvailable && _state.HasSelection)
            IsLive = true;   // 상시 인식: arm on entry
        UpdateLoopState();
    }

    private void UpdateLoopState()
    {
        if (_isLive && _isTabActive && !_isWindowActive && _ocr.IsAvailable && _state.HasSelection)
            StartLoop();
        else
            StopLoop();
    }

    private void StartLoop()
    {
        if (_timer is not null) return;

        _lastPos = _capture.GetCursorPosition();
        _stableSinceTick = System.Diagnostics.Stopwatch.GetTimestamp();
        _retryNotBeforeTick = 0;
        ResetRecognitionGate();
        _loopState = LiveLoopState.Watching;

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(PollMs),
        };
        _timer.Tick += OnTick;
        _timer.Start();
        StatusMessage = IsSaveMode
            ? "착용 장비 등록 중: 장비창에서 현재 착용 아이템에 커서를 올리세요."
            : "실시간 인식 중… 게임에서 아이템 위에 커서를 올리세요.";
    }

    private void StopLoop()
    {
        CancelValueEnrichment();
        _liveRecognitionCts?.Cancel();
        _loopState = LiveLoopState.Stopped;
        if (_timer is null) return;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer = null;
        if (!_isLive) StatusMessage = "실시간 인식 일시중지.";
    }

    // Runs on the UI thread (DispatcherTimer). The explicit state prevents overlapping captures, and
    // the per-attempt deadline guarantees that one native OCR call cannot kill the live loop forever.
    private async void OnTick(object? sender, EventArgs e)
    {
        // A tab/focus toggle can restart the timer while the cancelled native call is
        // still draining. Do not replace its CTS or start another foreground attempt.
        if (_loopState != LiveLoopState.Watching || _liveRecognitionCts is not null) return;

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_retryNotBeforeTick > now) return;

        var pos = _capture.GetCursorPosition();
        if (Math.Abs(pos.X - _lastPos.X) > MoveThreshold || Math.Abs(pos.Y - _lastPos.Y) > MoveThreshold)
        {
            CancelValueEnrichment();
            _lastPos = pos;
            _stableSinceTick = now;
            return; // still moving
        }

        bool dwelled = ElapsedMilliseconds(_stableSinceTick, now) >= DwellMs;
        bool newSpot = Math.Abs(pos.X - _recognizedAt.X) > RecaptureThreshold
                    || Math.Abs(pos.Y - _recognizedAt.Y) > RecaptureThreshold;
        if (!dwelled || !newSpot) return;

        _recognizedAt = pos;
        _loopState = LiveLoopState.Recognizing;
        var cts = new CancellationTokenSource();
        cts.CancelAfter(LiveRecognitionTimeoutMs);
        _liveRecognitionCts = cts;
        try
        {
            await RecognizeOnceAsync(cts.Token);
            if (cts.IsCancellationRequested && _loopState == LiveLoopState.Recognizing)
            {
                ResetRecognitionGate();
                _retryNotBeforeTick = System.Diagnostics.Stopwatch.GetTimestamp()
                    + (long)(System.Diagnostics.Stopwatch.Frequency * RecognitionRetryBackoffMs / 1000.0);
                StatusMessage = "인식 시간이 초과되어 잠시 후 다시 시도합니다.";
            }
        }
        finally
        {
            if (ReferenceEquals(_liveRecognitionCts, cts))
                _liveRecognitionCts = null;
            cts.Dispose();
            if (_loopState == LiveLoopState.Recognizing)
                _loopState = LiveLoopState.Watching;
        }
    }
    /// <summary>
    /// Waits for one WPF composition pass after the view-model notifications have been raised.
    /// Recognition is only started by <see cref="DispatcherTimer"/> on the UI dispatcher, so the
    /// static rendering event is subscribed and completed on that same dispatcher. If composition
    /// is suspended (for example while minimized), return a non-rendered result after a bounded
    /// wait so the live loop cannot remain permanently busy.
    /// </summary>
    private readonly record struct UiRenderCommit(long? Tick, bool Rendered);

    private static async Task<UiRenderCommit> AwaitNextRenderAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<UiRenderCommit>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var timeout = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };

        void Complete(UiRenderCommit result)
        {
            CompositionTarget.Rendering -= OnRendering;
            timeout.Stop();
            timeout.Tick -= OnTimeout;
            completion.TrySetResult(result);
        }

        void OnRendering(object? sender, EventArgs e)
            => Complete(new UiRenderCommit(
                System.Diagnostics.Stopwatch.GetTimestamp(), Rendered: true));

        void OnTimeout(object? sender, EventArgs e)
            => Complete(new UiRenderCommit(Tick: null, Rendered: false));

        CompositionTarget.Rendering += OnRendering;
        timeout.Tick += OnTimeout;
        timeout.Start();
        using var cancellationRegistration = cancellationToken.Register(
            () => completion.TrySetCanceled(cancellationToken));
        try
        {
            return await completion.Task;
        }
        finally
        {
            CompositionTarget.Rendering -= OnRendering;
            timeout.Stop();
            timeout.Tick -= OnTimeout;
        }
    }

    private async Task RecognizeOnceAsync(CancellationToken cancellationToken)

    {
        ScreenCaptureSnapshot? attemptedCapture = null;
        MainStat? stat = _state.MainStat;
        long stableSinceTick = _stableSinceTick;
        long captureMs = 0;
        long recognizeMs = 0;
        long recognitionEndTick = 0;
        try
        {
            // Capture only the DNF surface. The recognizer first locates the one tooltip under the
            // cursor, so comparison panels and unrelated UI never enter the hot path.
            var swCap = System.Diagnostics.Stopwatch.StartNew();
            var capture = await Task.Run(() =>
                _capture.CaptureAroundCursorBmpWithMetadata(CaptureWidth, CaptureHeight))
                .WaitAsync(cancellationToken);
            captureMs = swCap.ElapsedMilliseconds;
            attemptedCapture = capture;
            if (capture.Bytes.Length == 0)
            {
                StatusMessage = "화면 캡처 실패: 캡처된 픽셀이 없습니다.";
                return;
            }

            // Inspect only the DNF surface. This preserves the last game result when the user returns
            // to this window and prevents unrelated apps (including this WPF UI) from being OCR'd or
            // written to the debug ring as if they were game tooltips.
            var foreground = capture.Metadata.ForegroundWindow;
            if (LiveCapturePolicy.IsSelfForeground(
                    foreground, unchecked((uint)Environment.ProcessId))
                || !LiveCapturePolicy.IsDnfForeground(foreground))
            {
                return;
            }

            var swRec = System.Diagnostics.Stopwatch.StartNew();
            var recognition = await _recognizer.RecognizeAsync(
                capture.Bytes,
                (capture.CursorX, capture.CursorY),
                cancellationToken,
                includeComparison: false,
                mode: TooltipRecognitionMode.Immediate,
                includeItemName: false);
            swRec.Stop();
            recognizeMs = swRec.ElapsedMilliseconds;
            recognitionEndTick = System.Diagnostics.Stopwatch.GetTimestamp();
            var cursorAfterRecognition = _capture.GetCursorPosition();
            var capturedCursor = capture.Metadata.CursorScreenPx;
            if (Math.Abs(cursorAfterRecognition.X - capturedCursor.X) > RecaptureThreshold
                || Math.Abs(cursorAfterRecognition.Y - capturedCursor.Y) > RecaptureThreshold)
            {
                CancelValueEnrichment();
                return;
            }

            var character = _state.SelectedCharacter;
            var equipment = IsSaveMode && character is { } selectedCharacter
                ? GetEquipmentSnapshot(selectedCharacter)
                : Array.Empty<DfEquippedItem>();
            var reading = recognition.Reading;
            string? rarity = recognition.Rarity;
            string? slot = reading.Slot;
            // Observed = the character's main stat from the single tooltip; fall back to the keyword-less
            // value ("271 +113") recovered without a stat label.
            int? observed = stat is { } s
                ? (reading.MainStatValues.TryGetValue(s.ToKorean(), out int v) ? v : reading.BareMainStat)
                : null;

            var immediateEvidence = ApplyImmediateCandidateTransition(
                recognition, stat, equipment);
            if (!immediateEvidence.IsTooltip)
            {
                // Immediate mode may miss the pixel locator even when the capture contains a tooltip.
                // Schedule one balanced retry for this cursor position; _recognizedAt prevents repeated
                // retries while the cursor remains still, and a real blank capture is rejected there.
            bool fallbackScheduled = false;
            if (character is not null)
            {
                StartAccuracyEnrichment(
                    capture.Bytes,
                    cursorImage: (capture.CursorX, capture.CursorY),
                    cursorScreen: (capture.Metadata.CursorScreenPx.X, capture.Metadata.CursorScreenPx.Y),
                    stat: stat, equipment: equipment);
                fallbackScheduled = true;
            }
            if (!fallbackScheduled
                && (Math.Abs((long)_retriedAt.X - _recognizedAt.X) > RecaptureThreshold
                    || Math.Abs((long)_retriedAt.Y - _recognizedAt.Y) > RecaptureThreshold))
            {
                _retriedAt = _recognizedAt;
                _recognizedAt = (int.MinValue, int.MinValue);
            }
            StatusMessage = "툴팁 대기 중… 아이템 위에 커서를 올리세요.";
            var noTooltipCommit = await AwaitNextRenderAsync(cancellationToken);
            _ = Task.Run(() => SaveDebugCapture(capture, rarity, slot, stat, observed,
                stableSinceTick, recognitionEndTick, noTooltipCommit,
                captureMs, recognizeMs,
                outcome: "no-tooltip", recognitionSucceeded: false, timing: recognition.Timing));
            return;
            }
            // Always refine a recognized tooltip off the live loop. This preserves the immediate
            // response while restoring the balanced path's value/quality/label accuracy shortly after.
            if (character is not null)
                StartAccuracyEnrichment(
                    capture.Bytes,
                    cursorImage: (capture.CursorX, capture.CursorY),
                    cursorScreen: (capture.Metadata.CursorScreenPx.X, capture.Metadata.CursorScreenPx.Y),
                    stat: stat, equipment: equipment);

            RawLines.Clear();
            foreach (var line in reading.RawLines) RawLines.Add(line);
            DetectedRarity = rarity ?? "판별 실패";
            DetectedSlot = slot ?? "판별 실패";
            DetectedGrade = reading.GradeTier is { } gt
                ? (reading.GradePercent is int gp ? $"{gt} {gp}%" : gt)
                : "등급 미인식";

            // The 100% verdict is grade-based (등급 + 품질%) — the stat value is supplementary.
            var result = _engine.Compare(slot, rarity, reading.GradeTier, reading.GradePercent, observed, stat);
            Outcome = result.Outcome;
            if (stat is { } st)
            {
                ObservedDisplay = observed is int ov ? $"{st.ToKorean()} {ov}" : "관측값 없음";
                ReferenceDisplay = result.ReferenceValue is int rv ? $"{st.ToKorean()} {rv}" : "참조 없음";
            }
            else
            {
                ObservedDisplay = "주능력치 미해석";
                ReferenceDisplay = "주능력치 미해석";
            }

            HasResult = true;
            StatusMessage = $"{result.Note ?? "인식 완료."} (캡처 {captureMs}ms + 인식 {recognizeMs}ms)"
                + (IsSaveMode ? " 장비창의 현재 착용 아이템을 확인하세요." : string.Empty);
            var resultCommit = await AwaitNextRenderAsync(cancellationToken);
            _ = Task.Run(() => SaveDebugCapture(capture, rarity, slot, stat, observed,
                stableSinceTick, recognitionEndTick, resultCommit,
                captureMs, recognizeMs,
                outcome: "recognized", recognitionSucceeded: true, recognition.Timing));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            if (cancellationToken.IsCancellationRequested) return;
            StatusMessage = $"인식 실패: {ex.Message}";
            if (attemptedCapture is { Bytes.Length: > 0 } failureCapture)
            {
                try
                {
                    long failureRecognitionEnd = Math.Max(
                        recognitionEndTick, failureCapture.Metadata.CaptureEndTicks);
                    var failureCommit = await AwaitNextRenderAsync(cancellationToken);
                    _ = Task.Run(() => SaveDebugCapture(failureCapture, null, null, stat, null,
                        stableSinceTick, failureRecognitionEnd, failureCommit,
                        captureMs, recognizeMs,
                        outcome: "exception", recognitionSucceeded: false, timing: null));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
            }
        }
    }

    /// <summary>
    /// Applies the immediate pass's candidate transition without starting asynchronous refinement.
    /// Weak provisional evidence invalidates an old save candidate; truly empty polls preserve it.
    /// </summary>
    internal (bool IsTooltip, bool HasProvisionalEvidence) ApplyImmediateCandidateTransition(
        TooltipRecognition recognition, MainStat? stat, IReadOnlyList<DfEquippedItem> equipment)
    {
        var reading = recognition.Reading;
        bool isTooltip = reading.GradeTier is not null
                      || reading.MainStatValues.Count > 0
                      || reading.BareMainStat is not null;
        string? rarity = recognition.Rarity;
        string? slot = reading.Slot;
        bool hasProvisionalEvidence = isTooltip
            || (reading.ItemName is not null && rarity is not null && slot is not null);

        if (!isTooltip)
        {
            if (IsSaveMode && hasProvisionalEvidence)
                ClearEquippedCandidate();
            return (isTooltip, hasProvisionalEvidence);
        }

        if (IsSaveMode)
            ClearEquippedCandidate();
        UpdateEquippedCandidate(recognition, stat, equipment, accuracyConfirmed: false);
        return (isTooltip, hasProvisionalEvidence);
    }

    private void UpdateEquippedCandidate(
        TooltipRecognition recognition, MainStat? stat, IReadOnlyList<DfEquippedItem> equipment,
        bool accuracyConfirmed)
    {
        if (!IsSaveMode) return;

        // A newly observed provisional tooltip is invalidated by the live-loop callsite above. This
        // method preserves a confirmed candidate until the next verified tooltip replaces it.
        if (accuracyConfirmed)
            ClearEquippedCandidate();

        // Registration mode treats the tooltip under the cursor as the equipped item. Comparison
        // readings are intentionally not consulted here.
        var reading = recognition.Reading;
        bool hasReading = reading.GradeBox is not null
                       || reading.MainStatValues.Count > 0
                       || reading.BareMainStat is not null
                       || reading.ItemName is not null;
        if (!hasReading)
        {
            SaveEquippedCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(HasEquippedCandidate));
            return;
        }

        var match = EquippedItemMatcher.Find(
            reading, recognition.Rarity ?? reading.RarityLabel, equipment, _statTable, stat);
        if (match is null)
        {
            EquippedMatchDisplay = equipment.Count == 0
                ? "2번 탭의 장착 장비 목록이 없습니다."
                : "현재 착용 아이템의 단일 툴팁을 2번 탭 장비와 매칭하지 못했습니다.";
            SaveEquippedCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(HasEquippedCandidate));
            return;
        }


        string reference = match.ReferenceValue is int referenceValue && stat is { } referenceStat
            ? $" · {referenceStat.ToKorean()} 100% 기준 {referenceValue}"
            : string.Empty;
        string matchDisplay =
            $"현재 장착 매칭: {match.Item.ItemName} · {match.Rarity} · {match.Slot}{reference}";
        EquippedMatchDisplay = matchDisplay;

        if (!accuracyConfirmed || stat is not { } selectedStat)
        {
            if (!accuracyConfirmed)
                EquippedMatchDisplay += " · 정밀 보정 대기 중";
            SaveEquippedCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(HasEquippedCandidate));
            return;
        }

        int? value = reading.MainStatValues.TryGetValue(selectedStat.ToKorean(), out int parsed)
            ? parsed
            : reading.BareMainStat;
        if (value is not > 0)
        {
            SaveEquippedCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(HasEquippedCandidate));
            return;
        }
        _lastEquippedReading = reading with { RarityLabel = recognition.Rarity ?? reading.RarityLabel };
        _lastEquippedStat = selectedStat;
        _lastEquippedValue = value;
        _lastEquippedMatch = match;
        _equippedCandidateVersion++;
        EquippedCandidateDisplay =
            $"{match.Item.ItemName} · {match.Slot} · {selectedStat.ToKorean()} {value} (장착 슬롯)";
        OnPropertyChanged(nameof(HasEquippedCandidate));
        SaveEquippedCommand.RaiseCanExecuteChanged();
    }

    private async Task SaveEquippedAsync()
    {
        var character = _state.SelectedCharacter;
        var reading = _lastEquippedReading;
        var stat = _lastEquippedStat;
        var value = _lastEquippedValue;
        var match = _lastEquippedMatch;
        long candidateVersion = _equippedCandidateVersion;
        if (character is null || stat is not { } selectedStat || reading is null
            || value is not > 0 || match is null)
        {
            StatusMessage = "장착 툴팁에서 주능력치 값을 먼저 인식하세요.";
            return;
        }

        try
        {
            // Tab2 owns the current API snapshot. Registration uses the hovered single tooltip to
            // choose that character's current slot row; no comparison panel or network refresh is used.
            var observation = new EquippedStatObservation(
                ServerId: character.ServerId,
                CharacterId: character.CharacterId,
                Slot: match.Slot,
                ItemId: match.Item.ItemId,
                ItemName: match.Item.ItemName,
                Rarity: match.Rarity,
                Stat: selectedStat,
                ObservedValue: value.Value,
                GradeTier: reading.GradeTier,
                QualityPercent: reading.GradePercent,
                CapturedAtUtc: DateTimeOffset.UtcNow,
                Source: "equipped-tooltip");
            // Schema creation is synchronous inside the async SQLite bootstrap. Run the entire local
            // write off-dispatcher so the live capture/OCR loop and WPF input remain responsive.
            await Task.Run(() => _equippedStore.UpsertAsync(observation)).ConfigureAwait(true);
            ++_registrationProgressVersion;
            _registeredSlots.Add(match.Slot);
            OnPropertyChanged(nameof(RegistrationProgress));
            OnPropertyChanged(nameof(RegisteredSlotsDisplay));
            // A newer balanced/provisional result may have arrived while the write was pending. Only
            // clear the candidate that the user actually saved; never erase the newer candidate.
            if (candidateVersion == _equippedCandidateVersion)
                ClearEquippedCandidate();
            StatusMessage =
                $"{match.Slot} 등록 완료 ({_registeredSlots.Count}/{EquipmentSlots.All.Count}). 다음 장비에 커서를 올려주세요.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"장착값 저장 실패: {ex.Message}";
        }
    }
    private IReadOnlyList<DfEquippedItem> GetEquipmentSnapshot(RosterCharacter character)
    {
        string key = $"{character.ServerId}/{character.CharacterId}";
        if (_state.TryGetEquippedItems(character.ServerId, character.CharacterId, out var shared))
        {
            _equipmentSnapshot = shared;
            _equipmentSnapshotKey = key;
            return shared;
        }
        if (_equipmentSnapshot is not null
            && string.Equals(_equipmentSnapshotKey, key, StringComparison.Ordinal))
            return _equipmentSnapshot;
        return Array.Empty<DfEquippedItem>();
    }

    private void StartAccuracyEnrichment(
        byte[] imageBytes, (int X, int Y) cursorImage, (int X, int Y) cursorScreen, MainStat? stat,
        IReadOnlyList<DfEquippedItem> equipment)
    {
        if (imageBytes.Length == 0) return;

        CancelValueEnrichment();
        var cts = new CancellationTokenSource();
        _enrichmentCts = cts;
        long version = ++_recognitionVersion;
        _ = EnrichRecognitionAsync(imageBytes, cursorImage, cursorScreen, stat, equipment, version, cts);
    }

    private async Task EnrichRecognitionAsync(
        byte[] imageBytes, (int X, int Y) cursorImage, (int X, int Y) cursorScreen, MainStat? stat,
        IReadOnlyList<DfEquippedItem> equipment, long version, CancellationTokenSource cts)
    {
        try
        {
            // Let a short cursor sweep settle before spending the balanced ONNX pass. The immediate
            // result is already visible; this task only repairs numeric/label accuracy.
            await Task.Delay(75, cts.Token).ConfigureAwait(true);
            var enriched = await _recognizer.RecognizeAsync(
                imageBytes, cursorImage, cts.Token, includeComparison: false,
                mode: TooltipRecognitionMode.Balanced,
                includeItemName: IsSaveMode);
            var current = _capture.GetCursorPosition();
            if (cts.IsCancellationRequested || version != _recognitionVersion
                || (!IsSaveMode && !IsLive)
                || Math.Abs(current.X - cursorScreen.X) > RecaptureThreshold
                || Math.Abs(current.Y - cursorScreen.Y) > RecaptureThreshold)
                return;
            ApplyEnrichedRecognition(enriched, stat, equipment);
        }
        catch (OperationCanceledException)
        {
            // Cursor movement, tab changes, and mode changes intentionally cancel stale refinement.
        }
        catch
        {
            // The immediate OCR result remains valid when optional refinement fails.
        }
        finally
        {
            if (ReferenceEquals(_enrichmentCts, cts))
                _enrichmentCts = null;
            cts.Dispose();
        }
    }

    private void ApplyEnrichedRecognition(
        TooltipRecognition recognition, MainStat? stat, IReadOnlyList<DfEquippedItem> equipment)
    {
        var reading = recognition.Reading;
        string? rarity = recognition.Rarity;
        string? slot = reading.Slot;
        bool isTooltip = reading.GradeTier is not null
                      || reading.MainStatValues.Count > 0
                      || reading.BareMainStat is not null;
        if (!isTooltip) return;

        int? observed = stat is { } s
            ? (reading.MainStatValues.TryGetValue(s.ToKorean(), out int v) ? v : reading.BareMainStat)
            : null;
        RawLines.Clear();
        foreach (var line in reading.RawLines) RawLines.Add(line);
        DetectedRarity = rarity ?? "판별 실패";
        DetectedSlot = slot ?? "판별 실패";
        DetectedGrade = reading.GradeTier is { } gt
            ? (reading.GradePercent is int gp ? $"{gt} {gp}%" : gt)
            : "등급 미인식";

        var result = _engine.Compare(slot, rarity, reading.GradeTier, reading.GradePercent, observed, stat);
        Outcome = result.Outcome;
        if (stat is { } st)
        {
            ObservedDisplay = observed is int ov ? $"{st.ToKorean()} {ov}" : "관측값 없음";
            ReferenceDisplay = result.ReferenceValue is int rv ? $"{st.ToKorean()} {rv}" : "참조 없음";
        }
        else
        {
            ObservedDisplay = "주능력치 미해석";
            ReferenceDisplay = "주능력치 미해석";
        }
        HasResult = true;
        UpdateEquippedCandidate(recognition, stat, equipment, accuracyConfirmed: true);
        double elapsed = recognition.Timing?.TotalMs ?? 0;
        StatusMessage = $"{result.Note ?? "정밀 인식 완료."} (정밀 보정 {elapsed:0}ms)"
            + (IsSaveMode ? " 장비창의 현재 착용 아이템을 확인하세요." : string.Empty);
    }

    private void CancelValueEnrichment()
    {
        _recognitionVersion++;
        _enrichmentCts?.Cancel();
        _enrichmentCts = null;
    }


    // --- Debug: persist the exact capture the recognizer saw, plus a machine-readable sidecar that
    // records physical coordinates, virtual-monitor topology, foreground client bounds, DPI context,
    // cursor movement, and the settle→UI timing interval. The PNG remains directly usable by RecogProbe.
    private static readonly string DebugDir =
        Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, "debug_captures");
    // Six profiles × the default 30 trials need 180 artifacts; retain a margin for retries and
    // no-tooltip attempts instead of evicting evidence before the acceptance run completes.
    private const int DebugRingLimit = 240;
    private static long _debugSequence;
    private static readonly object DebugLock = new();

    private static long ElapsedMilliseconds(long startTicks, long endTicks)
    {
        if (endTicks <= startTicks) return 0;
        return (long)Math.Max(0, Math.Round(
            (endTicks - startTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency));
    }

    private static double? ElapsedMillisecondsNullable(long startTicks, long? endTicks)
    {
        if (endTicks is not long end) return null;
        if (end <= startTicks) return 0;
        return Math.Max(0, (end - startTicks) * 1000.0
            / System.Diagnostics.Stopwatch.Frequency);
    }

    private static void SaveDebugCapture(ScreenCaptureSnapshot capture, string? rarity, string? slot,
        MainStat? stat, int? observed, long stableSinceTick, long recognitionEndTick,
        UiRenderCommit uiCommit, long captureMs, long recognizeMs, string outcome,
        bool recognitionSucceeded, TooltipRecognitionTiming? timing)
    {
        if (capture.Bytes.Length == 0) return;
        try
        {
            lock (DebugLock)
            {
            Directory.CreateDirectory(DebugDir);
            var metadata = capture.Metadata;
            string settleToUiText = uiCommit.Tick is long commitTick
                ? ElapsedMilliseconds(stableSinceTick, commitTick).ToString()
                : "none";
            long stableAtTick = checked(stableSinceTick + (long)Math.Round(
                metadata.StopwatchFrequency * DwellMs / 1000.0));
            var liveTiming = new LiveCaptureTiming(
                StableSinceTicks: stableSinceTick,
                StableAtTicks: stableAtTick,
                CaptureStartTicks: metadata.CaptureStartTicks,
                CaptureEndTicks: metadata.CaptureEndTicks,
                RecognitionEndTicks: recognitionEndTick,
                UiCommitTicks: uiCommit.Tick,
                UiRendered: uiCommit.Rendered,
                CursorStoppedToUiMs: ElapsedMillisecondsNullable(stableSinceTick, uiCommit.Tick),
                StableWindowToUiMs: ElapsedMillisecondsNullable(stableAtTick, uiCommit.Tick),
                CaptureToUiMs: ElapsedMillisecondsNullable(metadata.CaptureStartTicks, uiCommit.Tick),
                CursorPollIntervalMs: PollMs,
                RequiredStableMs: DwellMs,
                Outcome: outcome,
                RecognitionSucceeded: recognitionSucceeded,
                CursorMovedDuringCapture: metadata.CursorMovedDuringCapture,
                Recognition: timing);

            string name = $"cap_{DateTime.Now:yyMMdd_HHmmss_fff}_{System.Threading.Interlocked.Increment(ref _debugSequence):D4}"
                        + $"__r-{NamePart(rarity ?? "none")}__s-{NamePart(slot ?? "none")}"
                        + $"__m-{NamePart(stat?.ToKorean() ?? "none")}__o-{(observed is int ov ? ov.ToString() : "none")}"
                        + $"__c-{capture.CursorX}x{capture.CursorY}"
                        + $"__res-{metadata.VirtualScreenPx.Width}x{metadata.VirtualScreenPx.Height}"
                        + $"__origin-{metadata.VirtualScreenPx.Left}x{metadata.VirtualScreenPx.Top}"
                        + $"__cap-{captureMs}__t-{recognizeMs}__u-{settleToUiText}__ui-{(uiCommit.Rendered ? "rendered" : "timeout")}"
                        + $"__p-{(timing?.FastPathUsed == true ? "fast" : timing?.FallbackUsed == true ? "fallback" : "full")}"
                        + $"__l-{timing?.LocatorMs:0}__crop-{timing?.CropMs:0}__w-{timing?.WindowsOcrMs:0}"
                        + $"__n-{timing?.OnnxOcrMs:0}__q-{timing?.LabelOcrMs:0}"
                        + $"__f-{NamePart(timing?.FallbackReason ?? "none")}.png";

            // The capture pipeline carries BMP; persist as PNG so the ring buffer stays compact and
            // the existing offline recognizer keeps reading *.png.
            byte[] pngBytes;
            int imageWidth;
            int imageHeight;
            using (var ms = new MemoryStream(capture.Bytes, writable: false))
            using (var bmp = new System.Drawing.Bitmap(ms))
            using (var png = new MemoryStream())
            {
                bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                pngBytes = png.ToArray();
                imageWidth = bmp.Width;
                imageHeight = bmp.Height;
            }

            string imagePath = Path.Combine(DebugDir, name);
            string imageTempPath = imagePath + ".tmp";
            File.WriteAllBytes(imageTempPath, pngBytes);
            File.Move(imageTempPath, imagePath, overwrite: true);

            var artifact = new LiveCaptureArtifact(
                SchemaVersion: 1,
                CaptureKind: "real-live",
                ImageFile: name,
                ImageSha256: Convert.ToHexString(SHA256.HashData(pngBytes)),
                ImageWidth: imageWidth,
                ImageHeight: imageHeight,
                Capture: metadata,
                Timing: liveTiming,
                Rarity: rarity,
                Slot: slot,
                MainStat: stat?.ToKorean(),
                ObservedValue: observed);
            string sidecarPath = Path.ChangeExtension(imagePath, ".json");
            string sidecarTempPath = sidecarPath + ".tmp";
            File.WriteAllText(sidecarTempPath,
                JsonSerializer.Serialize(artifact, LiveCaptureValidator.JsonOptions));
            File.Move(sidecarTempPath, sidecarPath, overwrite: true);

            foreach (var f in new DirectoryInfo(DebugDir).GetFiles("cap_*.png")
                         .OrderByDescending(f => f.LastWriteTimeUtc)
                         .ThenByDescending(f => f.Name, StringComparer.Ordinal)
                         .Skip(DebugRingLimit))
            {
                try
                {
                    f.Delete();
                    string oldSidecar = Path.ChangeExtension(f.FullName, ".json");
                    if (File.Exists(oldSidecar)) File.Delete(oldSidecar);
                }
                catch { /* debug best-effort */ }
            }
            }
        }
        catch { /* debug best-effort */ }
    }

    private static string NamePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(ch => invalid.Contains(ch) ? '_' : ch));
    }

}
