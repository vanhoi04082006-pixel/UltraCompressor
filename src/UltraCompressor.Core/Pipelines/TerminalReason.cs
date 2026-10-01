using UltraCompressor.Core.Models;
using UltraCompressor.Core.Search;

namespace UltraCompressor.Core.Pipelines;

/// <summary>
/// Mã kết cục của một lần chạy pipeline — chuỗi ổn định để gom số liệu, tách khỏi câu
/// chữ tiếng Việt dành cho người dùng.
///
/// <para>Tồn tại vì <c>item.DecisionReason</c> là trường mà báo cáo và thống kê đọc. Khi
/// pipeline không nói được thì phải có một mã thay thế <b>ổn định</b>, chứ không phải để
/// trống: trường null nghĩa là "không ai biết vì sao", và đó là loại thông tin mất thì khó
/// phục hồi nhất.</para>
///
/// <para>Mã của đường thích ứng do <see cref="SearchDecisionReasons"/> định nghĩa — cùng hệ
/// với mã mà tìm kiếm phát ra, nên không có hai nguồn sự thật cho cùng một kết cục.</para>
/// </remarks>
public static class TerminalReason
{
    /// <summary>Tiền tố của mạng dựng từ <see cref="SkipReason"/> khi pipeline không nói.</summary>
    /// <remarks>
    /// Mã dựng theo tên lý do bỏ qua là dạng tệ nhất có thể: nó nói được "đã bỏ vì lý do X" mà
    /// không bịa thêm điều gì. Pipeline nào biết rõ hơn thì tự đặt <c>PipelineResult.Reason</c>.
    /// </remarks>
    public const string SkipPrefix = "SKIP_";

    /// <summary>Mã kết cục chắc chắn có, cho một kết quả pipeline.</summary>
    public static string For(PipelineResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        // Pipeline đã nói thì tin nó: đó là mã chi tiết hơn, và nó là mà báo cáo đã dùng.
        if (!string.IsNullOrWhiteSpace(result.Reason))
        {
            return result.Reason;
        }

        return ForSkip(result.Skip);
    }

    /// <summary>Mã kết cục dựng từ một lý do bỏ qua.</summary>
    public static string ForSkip(SkipReason skip) =>
        SkipPrefix + skip.ToString().ToUpperInvariant();
}
