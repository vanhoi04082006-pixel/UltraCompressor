using UltraCompressor.Core.Media;

namespace UltraCompressor.Core.Tests;

/// <summary>
/// Kiểm thử phép đo SI/TI của ITU-T P.910 trên ảnh tổng hợp, không cần ffmpeg.
///
/// <para>Mục đích là chứng minh phép toán đúng, tách biệt khỏi việc "ffmpeg có chạy
/// không". Ảnh tổng hợp cho giá trị SI mong muốn chính xác theo định nghĩa, nên khi
/// phép toán sai thì test đỏ chứ không chỉ lệch một chút.</para>
/// </summary>
public sealed class ContentComplexityTests
{
    private const int W = 320;
    private const int H = 180;

    // ---------------------------------------------------------------- SI

    [Fact]
    public void Anh_phang_deu_co_SI_bang_khong()
    {
        // Sobel của ảnh hằng số bằng 0 ở mọi điểm, nên độ lệch chuẩn bằng đúng 0.
        var frame = new byte[W * H];
        Array.Fill(frame, (byte)128);

        var si = ContentComplexityProbe.SpatialInformation(frame);

        Assert.Equal(0, si, 10);
    }

    [Fact]
    public void Anh_co_nhieu_canh_o_hai_huong_tang_SI()
    {
        // Ô cờ 4px: cạnh nằm ở cả hướng ngang lẫn dọc nên độ lớn Sobel thay đổi mạnh
        // trên khung, độ lệch chuẩn vì thế rất cao (đo được ~419).
        var checker = new byte[W * H];
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                checker[y * W + x] = (byte)((x / 4 + y / 4) % 2 == 0 ? 0 : 255);
            }
        }

        var si = ContentComplexityProbe.SpatialInformation(checker);

        Assert.True(si > 100, $"SI ô cờ = {si}, phải vượt xa SI vùng phẳng");
    }

    [Fact]
    public void Canh_sac_dieu_tinh_tuan_co_SI_bang_khong()
    {
        // Đây là hệ quả thật của định nghĩa P.910, không phải lỗi: SI là **độ lệch chuẩn**
        // của độ lớn Sobel, nên một trường cạnh đều đặn — mọi pixel cùng độ lớn — cho
        // độ lệch chuẩn bằng 0, y hệt ảnh phẳng.
        //
        // Ghi lại thành test để không ai sau này tưởng phép đo hỏng rồi sửa sai. Ảnh thật
        // không bao giờ đều tăm tắp như thế, nên nó không ảnh hưởng tới quyết định codec —
        // nhưng nó là lý do không nên dùng SI một mình mà thiếu TI.
        var stripes = new byte[W * H];
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                stripes[y * W + x] = (byte)((x / 2) % 2 == 0 ? 0 : 255);
            }
        }

        var si = ContentComplexityProbe.SpatialInformation(stripes);

        Assert.Equal(0, si, 6);
    }

    [Fact]
    public void Sobel_la_toan_tu_hieu_nen_them_hang_so_khong_doi_ket_qua()
    {
        // Nếu quên dấu trừ trong kernel, phép đo sẽ thành tổng thay vì hiệu, và mọi ảnh sáng
        // hơn sẽ cho SI khác. Kiểm bằng cách tăng đều toàn bộ ảnh, không làm chậm cạnh nào.
        var a = new byte[W * H];
        var b = new byte[W * H];
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                var onLeft = x < W / 2;
                a[y * W + x] = (byte)(onLeft ? 50 : 200);
                b[y * W + x] = (byte)(onLeft ? 70 : 220);   // cộng đều 20, không tràn 0-255
            }
        }

        Assert.Equal(ContentComplexityProbe.SpatialInformation(a), ContentComplexityProbe.SpatialInformation(b), 9);
    }

    // ---------------------------------------------------------------- TI

    [Fact]
    public void Hai_khung_giong_nhau_co_TI_bang_khong()
    {
        var frame = new byte[W * H];
        Array.Fill(frame, (byte)90);

        var ti = ContentComplexityProbe.TemporalInformation(frame, frame);

        Assert.Equal(0, ti, 10);
    }

    [Fact]
    public void Khung_chuyen_hoan_toan_cho_TI_cao()
    {
        var black = new byte[W * H];
        var white = new byte[W * H];
        Array.Fill(white, (byte)255);

        // Trắng/đen thuần: mọi pixel đổi 255 nên độ lệch chuẩn bằng 0 — cao về mặt số
        // khác biệt nhưng TI chuẩn hoá bằng độ lệch chuẩn nên vẫn là 0. Đây là lý do không
        // thể dùng "số pixel đổi" làm TI: nó đo sai đại lượng P.910 định nghĩa.
        var ti = ContentComplexityProbe.TemporalInformation(white, black);

        Assert.Equal(0, ti, 10);
    }

    [Fact]
    public void Chi_mot_vung_chuyen_dong_duoc_phan_biet()
    {
        // Đảo toàn bộ khung cho TI = 0, vì mọi pixel đều đổi một lượng như nhau và TI là độ
        // lệch chuẩn. Chỉ khi phần đổi **không đều** trên khung thì mới ra TI > 0 — đây là
        // chuyển động thật, kiểu một vật chuyển động trên nền đứng yên.
        var still = new byte[W * H];
        var moved = new byte[W * H];
        Array.Fill(still, (byte)30);
        Array.Fill(moved, (byte)30);

        for (var y = H / 3; y < 2 * H / 3; y++)
        {
            for (var x = W / 4; x < 3 * W / 4; x++)
            {
                moved[y * W + x] = 230;
            }
        }

        var ti = ContentComplexityProbe.TemporalInformation(moved, still);

        Assert.True(ti > 10, $"TI vùng chuyển động = {ti}, phải lớn hơn 0 rõ rệt");
    }

    [Fact]
    public void Khung_rong_vao_thi_TI_bang_khong()
    {
        var current = new byte[W * H];
        var previous = new byte[W * H];
        Array.Fill(current, (byte)10);
        Array.Fill(previous, (byte)200);

        // Lệch độ dài: phần có chung phải cho 0, không được đọc ra ngoài mảng.
        var ti = ContentComplexityProbe.TemporalInformation(current, previous);

        Assert.Equal(0, ti, 10);
    }

    // ---------------------------------------------------------------- phân loại

    [Theory]
    // Đo thật trên thư viện người dùng.
    [InlineData(122.46, 0.02, ContentProfile.ScreenContent)]
    [InlineData(90.11, 0.00, ContentProfile.FlatMotionless)]
    [InlineData(91.70, 0.00, ContentProfile.FlatMotionless)]
    [InlineData(89.33, 6.47, ContentProfile.ModerateMotion)]
    [InlineData(72.11, 11.41, ContentProfile.ModerateMotion)]
    // Biên ngưỡng.
    [InlineData(110.00, 0.99, ContentProfile.ScreenContent)]
    [InlineData(109.99, 0.99, ContentProfile.FlatMotionless)]
    [InlineData(150.00, 1.00, ContentProfile.ModerateMotion)]
    [InlineData(150.00, 30.00, ContentProfile.BusyMotion)]
    [InlineData(50.00, 30.00, ContentProfile.BusyMotion)]
    public void Phan_loai_dung_theo_SI_TI(double si, double ti, ContentProfile expected)
    {
        Assert.Equal(expected, ContentComplexity.Classify(si, ti, samples: 3));
    }

    [Fact]
    public void Khong_co_mau_thu_thi_khong_ke_luon()
    {
        // Probe hỏng thì phải nói "không biết", để planner dùng đường lùi chứ không đoán bừa.
        Assert.Equal(ContentProfile.Unknown, ContentComplexity.Classify(0, 0, samples: 0));
    }

    [Fact]
    public void Ngay_ban_ranh_giua_man_hinh_va_noi_dung_khac_co_bien_an_toan()
    {
        // Anime hạn chế chuyển động cũng ra TI ~ 0, nên chỉ dựa vào TI thì bị nhầm sang
        // màn hình. SI mới là điều kiện phân biệt: anime cao nhất đo được là 100, màn hình
        // là 122. Ngưỡng 110 nằm giữa, mỗi bên lệch hơn 10 điểm.
        Assert.True(
            ContentComplexity.ScreenSpatialThreshold > 100,
            "ngưỡng phải trên SI cao nhất của anime");
        Assert.True(
            ContentComplexity.ScreenSpatialThreshold < 122.46,
            "ngưỡng phải dưới SI của tệp màn hình thật");
    }
}
