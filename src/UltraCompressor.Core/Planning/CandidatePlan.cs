namespace UltraCompressor.Core.Planning;

/// <summary>Kết quả lập kế hoạch: tập ứng viên đã lọc và sắp xếp, kèm số liệu chẩn đoán.</summary>
public sealed record CandidatePlan
{
    public required IReadOnlyList<CompressionCandidate> Candidates { get; init; }

    public required CandidateDiagnostics Diagnostics { get; init; }

    /// <summary>Chỉ lấy phần ứng viên encode, theo đúng thứ tự nên dò.</summary>
    /// <remarks>
    /// <see cref="OriginalCandidate"/> <b>không</b> nằm trong đây, và đó là chủ đích: tìm kiếm
    /// không encode gì thì không dò được nhánh không-nén. Nhánh đó được so ở tầng quyết định,
    /// sau tìm kiếm, bằng ngữ nghĩa riêng.
    /// </remarks>
    public IEnumerable<VideoEncodeCandidate> EncodeCandidates =>
        Candidates.OfType<VideoEncodeCandidate>();

    /// <summary>
    /// Nhánh giữ nguyên bản gốc, nếu có. Đây là ứng viên ngang hàng, nên nó nằm trong
    /// <see cref="Candidates"/> chứ không phải một ngoại lệ ở cuối đường ống.
    /// </summary>
    public OriginalCandidate? Original => Candidates.OfType<OriginalCandidate>().FirstOrDefault();

    /// <summary>Các nhánh (codec × kích thước) có mặt, theo thứ tự dò.</summary>
    public IEnumerable<string> Branches => EncodeCandidates
        .Select(c => c.BranchId)
        .Distinct(StringComparer.Ordinal);
}

/// <summary>
/// Số liệu chẩn đoán của một lần lập kế hoạch.
///
/// <para>Không phải để trang trí. Khi không có ứng viên nào, đây là thứ duy nhất nói cho
/// biết <i>vì sao</i> — bỏ vì thiếu codec, bị chặn vì độ phân giải không hợp lệ, hay vì
/// cấu hình giới hạn quá chặt.</para>
/// </summary>
public sealed record CandidateDiagnostics
{
    public required int ResolutionBranches { get; init; }

    public required IReadOnlyList<string> CodecsConsidered { get; init; }

    public required IReadOnlyList<string> CodecsSkipped { get; init; }

    public required IReadOnlyList<string> Rejected { get; init; }

    public required int DuplicatesRemoved { get; init; }

    public required int Truncated { get; init; }

    /// <summary>Ghi chú dạng tiếng Việt cho người đọc nhật ký.</summary>
    public required IReadOnlyList<string> Notes { get; init; }

    public static CandidateDiagnostics Empty { get; } = new()
    {
        ResolutionBranches = 0,
        CodecsConsidered = [],
        CodecsSkipped = [],
        Rejected = [],
        DuplicatesRemoved = 0,
        Truncated = 0,
        Notes = [],
    };

    public override string ToString() =>
        $"{CandidatesSummary()}";

    private string CandidatesSummary() =>
        $"nhánh hình={ResolutionBranches}, codec=[{string.Join(",", CodecsConsidered)}], "
        + $"bỏ={Rejected.Count}, trùng={DuplicatesRemoved}, cắt={Truncated}";
}
