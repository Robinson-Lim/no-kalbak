using System.IO;
using System.Reflection;
using DnfItemChecker.App.State;
using DnfItemChecker.App.ViewModels;
using DnfItemChecker.Core.Comparison;
using DnfItemChecker.Core.Data;
using DnfItemChecker.Core.Models;
using DnfItemChecker.Core.Ocr;
using DnfItemChecker.Core.Stats;
using DnfItemChecker.Vision;

namespace DnfItemChecker.App.Tests;

public sealed class InGameTabSaveTests
{
    [Fact]
    public async Task ImmediateCandidateTransitionHandlesWeakProvisionalAndEmptyPolls()
    {
        var path = Path.Combine(Path.GetTempPath(), $"app_test_{Guid.NewGuid():N}.json");
        var table = new JsonStatTable(path);
        await table.LoadAsync();

        try
        {
            var character = Character();
            var item = Equipped("팔찌", "찬란한 황금향의 영광");
            var state = State(character, item);
            var vm = CreateViewModel(table, state, new RecordingEquippedStore());
            vm.ToggleSaveModeCommand.Execute(null);

            var confirmed = Recognition(
                item.ItemName, item.SlotName, item.ItemRarity, value: 153, includeGrade: true);
            ApplyCandidate(vm, confirmed, MainStat.Strength, new[] { item }, accuracyConfirmed: true);
            Assert.True(vm.HasEquippedCandidate);

            // The same evidence classification used by RecognizeOnceAsync must not create a save
            // candidate before Balanced refinement confirms the reading.
            var immediateEvidence = vm.ApplyImmediateCandidateTransition(
                confirmed, MainStat.Strength, new[] { item });
            Assert.True(immediateEvidence.IsTooltip);
            Assert.True(immediateEvidence.HasProvisionalEvidence);
            Assert.False(vm.HasEquippedCandidate);
            Assert.False(vm.SaveEquippedCommand.CanExecute(null));

            ApplyCandidate(vm, confirmed, MainStat.Strength, new[] { item }, accuracyConfirmed: true);
            Assert.True(vm.HasEquippedCandidate);

            // A new tooltip with only name/slot/rarity evidence invalidates the old candidate before
            // its refinement can complete.
            var weakProvisional = Recognition(
                item.ItemName, item.SlotName, item.ItemRarity, value: null, includeGrade: false);
            var weakEvidence = vm.ApplyImmediateCandidateTransition(
                weakProvisional, MainStat.Strength, new[] { item });
            Assert.False(weakEvidence.IsTooltip);
            Assert.True(weakEvidence.HasProvisionalEvidence);
            Assert.False(vm.HasEquippedCandidate);
            Assert.False(vm.SaveEquippedCommand.CanExecute(null));

            // A genuinely empty poll is not a new item observation; it preserves a confirmed candidate
            // while the user moves from the game tooltip to the Save button.
            ApplyCandidate(vm, confirmed, MainStat.Strength, new[] { item }, accuracyConfirmed: true);
            var emptyEvidence = vm.ApplyImmediateCandidateTransition(
                new TooltipRecognition(EmptyReading(), null), MainStat.Strength, new[] { item });
            Assert.False(emptyEvidence.IsTooltip);
            Assert.False(emptyEvidence.HasProvisionalEvidence);
            Assert.True(vm.HasEquippedCandidate);
            Assert.True(vm.SaveEquippedCommand.CanExecute(null));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task SaveCompletionDoesNotClearCandidateConfirmedDuringDelayedWrite()
    {
        var path = Path.Combine(Path.GetTempPath(), $"app_test_{Guid.NewGuid():N}.json");
        var table = new JsonStatTable(path);
        await table.LoadAsync();

        try
        {
            var character = Character();
            var first = Equipped("팔찌", "찬란한 황금향의 영광");
            var second = Equipped("반지", "새로 확인한 반지");
            var state = State(character, first, second);
            var store = new DelayedEquippedStore();
            var vm = CreateViewModel(table, state, store);
            vm.ToggleSaveModeCommand.Execute(null);

            ApplyCandidate(vm, Recognition(
                first.ItemName, first.SlotName, rarity: null, value: 153, includeGrade: true),
                MainStat.Strength, new[] { first, second }, accuracyConfirmed: true);
            Assert.True(vm.SaveEquippedCommand.CanExecute(null));

            vm.SaveEquippedCommand.Execute(null);
            await store.Entered.WaitAsync(TimeSpan.FromSeconds(5));

            ApplyCandidate(vm, Recognition(
                second.ItemName, second.SlotName, second.ItemRarity, value: 140, includeGrade: true),
                MainStat.Strength, new[] { first, second }, accuracyConfirmed: true);
            Assert.True(vm.HasEquippedCandidate);
            Assert.Contains(second.ItemName, vm.EquippedCandidateDisplay);

            store.Release();
            var saved = await store.Saved.WaitAsync(TimeSpan.FromSeconds(5));
            await EventuallyAsync(() =>
                vm.StatusMessage?.Contains("등록 완료") == true
                && vm.SaveEquippedCommand.CanExecute(null));

            Assert.Equal(first.ItemId, saved.ItemId);
            Assert.Equal("에픽", saved.Rarity);
            Assert.True(vm.HasEquippedCandidate);
            Assert.Contains(second.ItemName, vm.EquippedCandidateDisplay);
            Assert.True(vm.SaveEquippedCommand.CanExecute(null));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task SuccessfulSaveClearsUnchangedCandidate()
    {
        var path = Path.Combine(Path.GetTempPath(), $"app_test_{Guid.NewGuid():N}.json");
        var table = new JsonStatTable(path);
        await table.LoadAsync();

        try
        {
            var character = Character();
            var item = Equipped("팔찌", "찬란한 황금향의 영광");
            var state = State(character, item);
            var store = new RecordingEquippedStore();
            var vm = CreateViewModel(table, state, store);
            vm.ToggleSaveModeCommand.Execute(null);

            ApplyCandidate(vm, Recognition(
                item.ItemName, item.SlotName, rarity: null, value: 153, includeGrade: true),
                MainStat.Strength, new[] { item }, accuracyConfirmed: true);
            Assert.True(vm.SaveEquippedCommand.CanExecute(null));

            vm.SaveEquippedCommand.Execute(null);
            var saved = await store.Saved.WaitAsync(TimeSpan.FromSeconds(5));
            await EventuallyAsync(() =>
                vm.StatusMessage?.Contains("등록 완료") == true
                && !vm.HasEquippedCandidate
                && !vm.SaveEquippedCommand.CanExecute(null));

            Assert.Equal(item.ItemId, saved.ItemId);
            Assert.False(vm.HasEquippedCandidate);
            Assert.False(vm.SaveEquippedCommand.CanExecute(null));
            Assert.Equal("착용 장비 등록: 1/11", vm.RegistrationProgress);
            Assert.Contains("팔찌 ✓", vm.RegisteredSlotsDisplay);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
    [Fact]
    public async Task RegistrationUsesHoveredReadingInsteadOfComparisonReading()
    {
        var path = Path.Combine(Path.GetTempPath(), $"app_test_{Guid.NewGuid():N}.json");
        var table = new JsonStatTable(path);
        await table.LoadAsync();

        try
        {
            var character = Character();
            var inspected = Equipped("상의", "마우스 아래 상의");
            var equipped = Equipped("팔찌", "비교 패널 팔찌");
            var state = State(character, inspected, equipped);
            var vm = CreateViewModel(table, state, new RecordingEquippedStore());
            vm.ToggleSaveModeCommand.Execute(null);

            var left = Recognition(inspected.ItemName, inspected.SlotName, "에픽", 120, includeGrade: true);
            var right = Recognition(equipped.ItemName, equipped.SlotName, "에픽", 153, includeGrade: true);
            var comparison = new TooltipRecognition(
                left.Reading, left.Rarity, new[] { left.Reading, right.Reading },
                Timing: null, EquippedReading: right.Reading, EquippedRarity: right.Rarity);

            ApplyEnriched(vm, comparison, MainStat.Strength, new[] { inspected, equipped });

            Assert.True(vm.HasEquippedCandidate);
            Assert.Contains(inspected.ItemName, vm.EquippedCandidateDisplay);
            Assert.Contains("힘 120", vm.EquippedCandidateDisplay);
            Assert.DoesNotContain("힘 153", vm.EquippedCandidateDisplay);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
    [Fact]
    public async Task SingleTooltipMetadataMatchIsSafeInRegistrationContext()
    {
        var path = Path.Combine(Path.GetTempPath(), $"app_test_{Guid.NewGuid():N}.json");
        var table = new JsonStatTable(path);
        await table.LoadAsync();

        try
        {
            var character = Character();
            var item = Equipped("팔찌", "현재 장착 팔찌");
            var state = State(character, item);
            var vm = CreateViewModel(table, state, new RecordingEquippedStore());
            vm.ToggleSaveModeCommand.Execute(null);

            var reading = new TooltipReading(
                new[] { "힘 153" }, null, null, "최상급", 100,
                new Dictionary<string, int> { ["힘"] = 153 },
                item.SlotName, item.ItemRarity, null);

            // Registration mode was entered explicitly, so the hovered single tooltip is the
            // equipped-item ground truth. A unique slot/rarity match is sufficient to select the
            // API item even if the colored name OCR is absent.
            ApplyEnriched(
                vm, new TooltipRecognition(reading, item.ItemRarity),
                MainStat.Strength, new[] { item });
            Assert.True(vm.HasEquippedCandidate);
            Assert.Contains("힘 153", vm.EquippedCandidateDisplay);

            // A comparison-shaped result must still use the hovered/left reading, not the right side.
            var right = reading with
            {
                MainStatValues = new Dictionary<string, int> { ["힘"] = 99 },
            };
            var comparison = new TooltipRecognition(
                reading, item.ItemRarity, new[] { reading, right },
                Timing: null, EquippedReading: right, EquippedRarity: item.ItemRarity);
            ApplyEnriched(vm, comparison, MainStat.Strength, new[] { item });
            Assert.True(vm.HasEquippedCandidate);
            Assert.Contains("힘 153", vm.EquippedCandidateDisplay);
            Assert.DoesNotContain("힘 99", vm.EquippedCandidateDisplay);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }




    [Fact]
    public void WindowActivationRearmsSameHoverAfterAltTab()
    {
        var path = Path.Combine(Path.GetTempPath(), $"app_test_{Guid.NewGuid():N}.json");
        var table = new JsonStatTable(path);

        try
        {
            var character = Character();
            var state = State(character);
            var vm = CreateViewModel(table, state, new RecordingEquippedStore());
            var recognizedAt = typeof(InGameTabViewModel).GetField(
                "_recognizedAt", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(recognizedAt);

            var hover = (123, 456);
            recognizedAt!.SetValue(vm, hover);
            vm.SetWindowActive(false); // app -> DNF
            Assert.Equal((int.MinValue, int.MinValue), recognizedAt.GetValue(vm));

            recognizedAt.SetValue(vm, hover); // a result was observed while DNF was foreground
            vm.SetWindowActive(true); // DNF -> app
            Assert.Equal((int.MinValue, int.MinValue), recognizedAt.GetValue(vm));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }


    private static InGameTabViewModel CreateViewModel(
        IStatTable table, AppState state, IEquippedStatStore store) =>
        new(
            new NoopCapture(),
            new NoopOcr(),
            new FakeRecognizer(new TooltipRecognition(EmptyReading(), null)),
            new ComparisonEngine(table),
            store,
            table,
            state);

    private static RosterCharacter Character() =>
        new("카인", "character-1", "테스트 캐릭터", 115, "job", "grow", "직업", "전직", 1000, "모험단");

    private static AppState State(RosterCharacter character, params DfEquippedItem[] items)
    {
        var state = new AppState
        {
            SelectedCharacter = character,
            MainStat = MainStat.Strength,
        };
        state.SetEquippedItems(character.ServerId, character.CharacterId, items);
        return state;
    }

    private static DfEquippedItem Equipped(string slot, string name) =>
        new(slot, slot, $"id-{slot}", name, null, slot, 115, "에픽",
            null, null, 0, "최상급", null, 0, null);

    private static TooltipRecognition Recognition(
        string itemName, string slot, string? rarity, int? value, bool includeGrade)
    {
        var stats = value is int v
            ? new Dictionary<string, int> { ["힘"] = v }
            : new Dictionary<string, int>();
        var reading = new TooltipReading(
            new[] { itemName }, itemName, null,
            includeGrade ? "최상급" : null,
            includeGrade ? 100 : null,
            stats, slot, rarity, null);
        return new TooltipRecognition(reading, rarity);
    }

    private static TooltipReading EmptyReading() =>
        new(Array.Empty<string>(), null, null, null, null,
            new Dictionary<string, int>(), null, null, null);

    private static void ApplyCandidate(
        InGameTabViewModel vm,
        TooltipRecognition recognition,
        MainStat stat,
        IReadOnlyList<DfEquippedItem> equipment,
        bool accuracyConfirmed)
    {
        var method = typeof(InGameTabViewModel).GetMethod(
            "UpdateEquippedCandidate", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(vm, new object?[] { recognition, stat, equipment, accuracyConfirmed });
    }
    private static void ApplyEnriched(
        InGameTabViewModel vm,
        TooltipRecognition recognition,
        MainStat stat,
        IReadOnlyList<DfEquippedItem> equipment)
    {
        var method = typeof(InGameTabViewModel).GetMethod(
            "ApplyEnrichedRecognition", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(vm, new object?[] { recognition, stat, equipment });
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class FakeRecognizer(TooltipRecognition result) : ITooltipRecognizer
    {
        public Task<TooltipRecognition> RecognizeAsync(
            byte[] imageBytes, (int X, int Y)? cursor = null,
            CancellationToken ct = default, bool includeComparison = false,
            TooltipRecognitionMode mode = TooltipRecognitionMode.Balanced,
            bool includeItemName = true) =>
            Task.FromResult(result);
    }

    private class RecordingEquippedStore : IEquippedStatStore
    {
        private readonly TaskCompletionSource<EquippedStatObservation> _saved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<EquippedStatObservation> Saved => _saved.Task;

        public virtual Task<IReadOnlyList<EquippedStatObservation>> GetForCharacterAsync(
            string serverId, string characterId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EquippedStatObservation>>(Array.Empty<EquippedStatObservation>());

        public virtual Task UpsertAsync(EquippedStatObservation observation, CancellationToken ct = default)
        {
            _saved.TrySetResult(observation);
            return Task.CompletedTask;
        }
    }

    private sealed class DelayedEquippedStore : RecordingEquippedStore
    {
        private readonly TaskCompletionSource<object?> _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<EquippedStatObservation> _saved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<object?> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;
        public new Task<EquippedStatObservation> Saved => _saved.Task;

        public override async Task UpsertAsync(
            EquippedStatObservation observation, CancellationToken ct = default)
        {
            _entered.TrySetResult(null);
            await _release.Task.WaitAsync(ct);
            _saved.TrySetResult(observation);
        }

        public void Release() => _release.TrySetResult(null);
    }

    private sealed class NoopCapture : IScreenCaptureService
    {
        public ScreenRect VirtualScreenBounds => new(0, 0, 1920, 1080);
        public byte[] CaptureRegionBmp(int x, int y, int width, int height) => Array.Empty<byte>();
        public ScreenCaptureSnapshot CaptureAroundCursorBmpWithMetadata(int width, int height) =>
            throw new NotSupportedException();
        public (int X, int Y) GetCursorPosition() => (0, 0);
    }

    private sealed class NoopOcr : IOcrService
    {
        public bool IsAvailable => true;

        public Task<OcrResult> RecognizeAsync(
            byte[] imageBytes, CancellationToken ct = default, double maxScale = 4.0) =>
            Task.FromResult(new OcrResult(Array.Empty<OcrLine>()));
    }
}
