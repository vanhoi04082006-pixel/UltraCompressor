using UltraCompressor.Core.Media;

namespace UltraCompressor.Core.Tests;

/// <summary>
/// Kiểm thử đọc header <c>Range</c>.
///
/// <para>Phần này trước đây nằm trong <c>MediaHost</c> của tầng giao diện và **không có
/// test nào**, nên sai thì biểu hiện thành "video không phát" — cùng triệu chứng với tệp
/// hỏng, và người dùng không có cách nào phân biệt.</para>
///
/// <para>Ca quan trọng nhất là <see cref="Range_mo_chi_tra_mot_khoi_nho"/>: nó là nguyên
/// nhân trực tiếp làm treo cửa sổ, và đã từng cho ra một response 552 MB.</para>
/// </summary>
public sealed class ByteRangeParserTests
{
    private const long OneGb = 1024L * 1024 * 1024;

    // ---------------------------------------------------------------- không có range

    [Fact]
    public void Khong_co_header_trai_nguyen_tep()
    {
        // Client không gửi Range thì phải trả 200 cả tệp — giống server tĩnh.
        Assert.Null(ByteRangeParser.Parse(null, OneGb));
        Assert.Null(ByteRangeParser.Parse("", OneGb));
        Assert.Null(ByteRangeParser.Parse("   ", OneGb));
    }

    [Theory]
    [InlineData("items=0-10")]
    [InlineData("bytes")]
    [InlineData("bytes=abc")]
    [InlineData("0-10")]
    public void Header_khong_phai_dang_byte_trai_nguyen_tep(string header)
    {
        // Không hiểu thì bỏ qua range, KHÔNG trả 416. 416 chỉ dành cho range sai
        // không thể phục vụ; hiểu sai cú pháp rồi từ chối là lỗi của mình.
        Assert.Null(ByteRangeParser.Parse(header, OneGb));
    }

    [Fact]
    public void Range_nhieu_doan_bi_bo_qua()
    {
        // Client media không dùng nhiều đoạn; ghép tay dễ sai hơn là bỏ qua.
        Assert.Null(ByteRangeParser.Parse("bytes=0-10,20-30", OneGb));
    }

    // ---------------------------------------------------------------- range đóng

    [Fact]
    public void Range_dong_tra_nguyen_so_byte_yeu_cau()
    {
        var r = ByteRangeParser.Parse("bytes=100-199", OneGb);

        Assert.NotNull(r);
        Assert.False(r!.Value.Invalid);
        Assert.Equal(100, r.Value.Start);
        Assert.Equal(100, r.Value.Count);
        Assert.Equal(199, r.Value.End);
    }

    [Fact]
    public void Range_dong_khong_bi_cat_khoi_du_da_len()
    {
        // Client đã tính kỹ cần bao nhiêu. Cắt thêm chỉ làm phát giật mà không giảm
        // được lượng dữ liệu phải tải, nên range đóng phải giữ nguyên.
        const long big = 8L * 1024 * 1024;
        var r = ByteRangeParser.Parse("bytes=0-8388607", OneGb);

        Assert.Equal(big, r!.Value.Count);
    }

    [Fact]
    public void Range_dinh_dau_bi_cat_ve_doi_tep()
    {
        // "bytes=0-999999999" trên tệp 1 MB: phải trả về đúng 1 MB, không phải báo sai.
        var r = ByteRangeParser.Parse("bytes=0-999999999", 1024 * 1024);

        Assert.False(r!.Value.Invalid);
        Assert.Equal(0, r.Value.Start);
        Assert.Equal(1024 * 1024, r.Value.Count);
    }

    [Fact]
    public void Range_sau_roi_tra_so_byte_cuoi()
    {
        var r = ByteRangeParser.Parse("bytes=-500", OneGb);

        Assert.Equal(OneGb - 500, r!.Value.Start);
        Assert.Equal(500, r.Value.Count);
    }

    [Fact]
    public void Range_sau_roi_dai_hon_tep_thi_lay_nguyen_tep()
    {
        var r = ByteRangeParser.Parse("bytes=-999999999", 1024);

        Assert.Equal(0, r!.Value.Start);
        Assert.Equal(1024, r.Value.Count);
    }

    // ---------------------------------------------------------------- range mở: nguyên nhân treo máy

    [Fact]
    public void Range_mo_chi_tra_mot_khoi_nho()
    {
        // Đây là ca đã gây treo: client hỏi "bytes=0-" nghĩa là "cho tôi từ đầu", bản
        // cũ hiểu thành "tới hết tệp" rồi gửi cả 552 MB trên UI thread.
        var r = ByteRangeParser.Parse("bytes=0-", OneGb);

        Assert.False(r!.Value.Invalid);
        Assert.Equal(0, r.Value.Start);
        Assert.Equal(ByteRangeParser.MaxChunkBytes, r.Value.Count);
    }

    [Fact]
    public void Range_mo_bat_dau_o_giua_tai_van_trai_mot_khoi()
    {
        // Chromium hay hỏi "bytes=N-" sau khi tua. Kết quả phải là một khối có độ dài
        // giới hạn, không phải phần còn lại của tệp.
        var r = ByteRangeParser.Parse("bytes=2097152-", OneGb);

        Assert.Equal(2097152, r!.Value.Start);
        Assert.Equal(ByteRangeParser.MaxChunkBytes, r.Value.Count);
        Assert.Equal(2097152 + ByteRangeParser.MaxChunkBytes - 1, r.Value.End);
    }

    [Fact]
    public void Range_mo_ngan_hon_khoi_thi_giu_nguyen()
    {
        // Tệp nhỏ hơn một khối thì không có gì để cắt.
        var r = ByteRangeParser.Parse("bytes=0-", 1000);

        Assert.Equal(1000, r!.Value.Count);
    }

    [Fact]
    public void Che_do_khong_gioi_han_phai_giu_nguyen_ho_tinh()
    {
        // maxChunk = 0 là hành vi cũ. Giữ lại để có thể đối chiếu hành vi cũ khi cần,
        // thay vì phải sửa lại app/ để tìm lỗi.
        var r = ByteRangeParser.Parse("bytes=0-", OneGb, maxChunk: 0);

        Assert.Equal(OneGb, r!.Value.Count);
    }

    // ---------------------------------------------------------------- range sai

    [Theory]
    [InlineData("bytes=2000-")]      // bắt đầu sau cuối tệp
    [InlineData("bytes=1024-")]      // bắt đầu đúng bằng độ dài tệp
    [InlineData("bytes=500-100")]    // đảo ngược
    [InlineData("bytes=-0")]
    [InlineData("bytes=-abc")]
    public void Range_sai_phai_tra_416(string header)
    {
        var r = ByteRangeParser.Parse(header, 1024);

        Assert.NotNull(r);
        Assert.True(r!.Value.Invalid, $"'{header}' phải bị đánh dấu Invalid để trả 416");
    }

    [Fact]
    public void Tep_rong_khong_phai_range_sai()
    {
        // Tệp 0 byte: không có byte nào để phục vụ, nhưng đây không phải lỗi header.
        Assert.Null(ByteRangeParser.Parse("bytes=0-", 0));
    }
}
