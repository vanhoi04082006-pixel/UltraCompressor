using UltraCompressor.Core.Models;

namespace UltraCompressor.Core.Toolchain;

/// <summary>Gom trạng thái của toàn bộ công cụ ngoài, có cache và kiểm tra lại được.</summary>
public sealed class ToolChain
{
    private readonly ToolLocator _locator;
    private readonly ToolHealthChecker _checker;
    private readonly Dictionary<ToolKind, ToolReport> _reports = [];

    public ToolChain(ToolLocator locator, ToolHealthChecker checker)
    {
        _locator = locator;
        _checker = checker;
    }

    public IReadOnlyList<ToolReport> Reports =>
        [.. Enum.GetValues<ToolKind>().Select(Get)];

    public ToolReport Get(ToolKind kind)
    {
        if (_reports.TryGetValue(kind, out var report)) return report;
        var placeholder = new ToolReport
        {
            Kind = kind,
            DisplayName = kind.ToString(),
            Required = kind != ToolKind.FFplay,
            Path = _locator.Locate(kind),
            Health = ToolHealth.Unknown,
        };
        _reports[kind] = placeholder;
        return placeholder;
    }

    public string? PathOf(ToolKind kind) => _locator.Locate(kind);

    /// <summary>Cho phần so sánh tìm công cụ mà không phải tự dựng lại một bộ dò.</summary>
    public ToolLocator Locator => _locator;

    public async Task<IReadOnlyList<ToolReport>> CheckAllAsync(CancellationToken token = default)
    {
        var tasks = Enum.GetValues<ToolKind>().Select(async kind =>
        {
            var report = await _checker.CheckAsync(kind, token);
            _reports[kind] = report;
            return report;
        });

        return await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Có công cụ bắt buộc nào đang thiếu/hỏng không. Dùng để chặn trước khi bắt đầu.
    /// FFplay chỉ dùng để xem trước nên không chặn.
    /// </summary>
    public bool HasBlockingProblem() => Reports.Any(r => r.Required && r.Health is ToolHealth.Missing or ToolHealth.Broken);

    /// <summary>
    /// Có công cụ nào đang hỏng không ở mức chỉ ảnh hưởng một loại media cụ thể không.
    /// Ví dụ Ghostscript hỏng thì job chỉ toàn PDF vẫn chạy được, còn job có video thì cảnh báo.
    /// </summary>
    public IEnumerable<ToolReport> BrokenTools() => Reports.Where(r => r.Health is ToolHealth.Broken or ToolHealth.Missing);

    /// <summary>Các công cụ thực sự cần cho một tập loại media cụ thể.</summary>
    public static IReadOnlyList<ToolKind> RequiredToolsFor(IReadOnlyCollection<MediaKind> kinds)
    {
        var required = new List<ToolKind>();

        // Mọi loại trừ PDF đều đi qua ffmpeg.
        if (kinds.Any(k => k != MediaKind.Pdf)) required.Add(ToolKind.FFmpeg);
        if (kinds.Contains(MediaKind.Gif)) required.Add(ToolKind.Gifsicle);
        if (kinds.Contains(MediaKind.Pdf)) required.Add(ToolKind.Ghostscript);

        return required;
    }

    /// <summary>
    /// Chỉ những công cụ vừa thiếu vừa thực sự cần cho job này. Dùng để quyết định chặn hay cảnh báo.
    ///
    /// Công cụ <b>chưa kiểm tra</b> cũng bị coi là chặn: không có bằng chứng rằng nó chạy
    /// được thì không nên bắt đầu nén. Việc kiểm tra diễn ra ở luồng nền ngay khi mở ứng
    /// dụng, nên người dùng gần như không bao giờ phải chờ.
    /// </summary>
    public IReadOnlyList<ToolReport> BlockingProblemsFor(IReadOnlyCollection<MediaKind> kinds) =>
        [.. RequiredToolsFor(kinds)
            .Select(Get)
            .Where(r => r.Health is ToolHealth.Missing or ToolHealth.Broken or ToolHealth.Unknown)];
}
