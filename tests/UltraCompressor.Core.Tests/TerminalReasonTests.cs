using UltraCompressor.Core.Models;
using UltraCompressor.Core.Pipelines;
using UltraCompressor.Core.Search;
using Xunit;

namespace UltraCompressor.Core.Tests;

/// <summary>
/// <b>Mọi đường kết thúc của một item phải có mã lý do đọc được bằng máy.</b>
///
/// <para>Trước đây hai nhánh giữ bản gốc của đường thích ứng thoát sớm khỏi engine mà không
/// để lại mã nào: engine thoát ở nhánh <c>!result.Success</c> trước khi lưới 5A kịp gán. Hậu quả
/// là báo cáo và thống kê không phân biệt được "bản gốc thắng" với "không tìm được gì đạt" —
/// đúng hai thứ mà cả giai đoạn 5B sinh ra để phân biệt.</para>
///
/// <para>Lớp <see cref="TerminalReason"/> là nơi quyết định, đặt ở Core để kiểm thử được
/// không cần dựng cửa sổ. Bảng ở đây khóa hành vi đó lại.</para>
/// </remarks>
public class TerminalReasonTests
{
    [Fact]
    public void Pipeline_no_ma_thi_dung_ma_theo_ly_do_bo_qua()
    {
        // Đường cũ không gắn mã. Kết quả vẫn phải có mã ổn định, và mã phái sinh phải nói
        // được "đã bỏ vì lý do X" chứ không bịa thêm điều gì.
        foreach (var skip in Enum.GetValues<SkipReason>())
        {
            var result = PipelineResult.NotWorthIt(skip, 0, "không đáng nén");

            var reason = TerminalReason.For(result);

            Assert.False(string.IsNullOrWhiteSpace(reason));
            Assert.Equal(TerminalReason.SkipPrefix + skip.ToString().ToUpperInvariant(), reason);
        }
    }

    [Fact]
    public void Pipeline_co_ma_thi_tin_no()
    {
        // Bốn kết cục của đường thích ứng phải đi thẳng tới item.DecisionReason.
        foreach (var code in SearchDecisionReasons.Outcomes)
        {
            var result = PipelineResult.NotWorthIt(SkipReason.NotWorthIt, 0, "giữ bản gốc")
                with
            { Reason = code };

            Assert.Equal(code, TerminalReason.For(result));
        }
    }

    [Fact]
    public void Ma_rong_hoac_khong_co_ky_tu_thi_coi_nhu_khong_co_ma()
    {
        // `Reason` là chuỗi do người viết đặt; khoảng trắng không phải là một mã.
        var blank = PipelineResult.NotWorthIt(SkipReason.NotWorthIt, 0, "giữ bản gốc")
            with
        { Reason = "   " };

        Assert.Equal(
            TerminalReason.ForSkip(SkipReason.NotWorthIt),
            TerminalReason.For(blank));
    }

    [Fact]
    public void Ket_qua_thanh_cong_thi_ma_phai_sau_cung_co_the_se_doi()
    {
        // Nhánh thành công không quyết định dứt điểm: lưới 5A sẽ ghi đè bằng mã của nó. Mã trung
        // gian ở đây chỉ để không có ô trống nào — nhưng phải là mã dựng được, không phải null.
        var ok = PipelineResult.Ok(1_000);

        Assert.False(string.IsNullOrWhiteSpace(TerminalReason.For(ok)));
        Assert.Equal("SKIP_NONE", TerminalReason.For(ok));
    }

    [Fact]
    public void Ma_lui_ve_duy_nhat_khi_duong_cu_bien_mat()
    {
        // Đường thích ứng rơi về đường cũ là kết cục CẦN ĐẾM, không phải chi tiết. Và nguyên
        // nhân gốc vẫn nằm trong thông báo — mã chỉ nói "đã rơi về", không nói "vì sao".
        var fallback = PipelineResult.NotWorthIt(SkipReason.NotWorthIt, 0, "LEGACY_FALLBACK_USED (probe hỏng)")
            with
        { Reason = SearchDecisionReasons.LegacyFallbackUsed };

        Assert.Equal(SearchDecisionReasons.LegacyFallbackUsed, TerminalReason.For(fallback));
        Assert.Contains("probe hỏng", "LEGACY_FALLBACK_USED (probe hỏng)", StringComparison.Ordinal);
    }

    [Fact]
    public void Khong_bao_gio_con_null()
    {
        // Bất biến: mọi tổ hợp lý đều cho ra mã. Đây là bản sao lỗi mà DoD cấm.
        var results = new[]
        {
            PipelineResult.Ok(1),
            PipelineResult.Failed(SkipReason.Error, "lỗi"),
            PipelineResult.NotWorthIt(SkipReason.NotWorthIt, 0, "x"),
            PipelineResult.NotWorthIt(SkipReason.BelowMinSaving, 0, "x") with { Reason = "MA" },
        };

        Assert.All(results, r => Assert.False(string.IsNullOrWhiteSpace(TerminalReason.For(r))));
        Assert.All(
            Enum.GetValues<SkipReason>(),
            s => Assert.False(string.IsNullOrWhiteSpace(TerminalReason.ForSkip(s))));
    }

    [Fact]
    public void Cac_ma_ket_cuc_khong_trung_nhau()
    {
        // Bốn kết cục phải đếm tách bạch được; trùng nhau là mất thống kê.
        Assert.Equal(4, SearchDecisionReasons.Outcomes.Distinct(StringComparer.Ordinal).Count());

        // Và không mã nào trong đó trùng với mã dựng từ lý do bỏ qua.
        foreach (var code in SearchDecisionReasons.Outcomes)
        {
            Assert.DoesNotContain(code, Enum.GetValues<SkipReason>().Select(TerminalReason.ForSkip));
        }
    }
}
