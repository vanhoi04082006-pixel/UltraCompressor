using System.Globalization;
using UltraCompressor.Core.Toolchain;

namespace UltraCompressor.Core.Tests;

/// <summary>
/// Kiểm thử lệnh so sánh cạnh nhau.
///
/// <para>Ca quan trọng nhất là <see cref="Phai_ghep_o_PIPE_chu_khong_phai_goi_thang_ffplay"/>.
/// Kế hoạch ban đầu là "một ffplay với <c>-f hstack</c>", và cả ba hướng đều không chạy
/// được trên tệp thật:
/// <c>-f hstack</c> → Unknown input format; <c>-filter_complex</c> → Option not found;
/// hai <c>-i</c> với ffplay → "provided as input filename, but ... was already specified".
/// Nguyên nhân gốc: <b>ffplay chỉ nhận một tệp</b>. Cách chạy được là để ffmpeg CLI ghép
/// rồi đổ sang ffplay qua pipe.</para>
/// </summary>
public sealed class SideBySideCommandTests
{
    private const string Original = @"C:\video\phim.mp4.bak";
    private const string Compressed = @"C:\video\phim.mp4";

    private static SideBySideCommand Build(
        int? leftWidth = 1920, int? leftHeight = 1080,
        int? rightWidth = 1920, int? rightHeight = 1080)
        => SideBySidePlayer.Build(
            @"C:\tools\ffmpeg.exe", @"C:\tools\ffplay.exe",
            Original, Compressed, leftWidth, leftHeight, rightWidth, rightHeight);

    private static string Graph(SideBySideCommand command)
    {
        var args = command.FfmpegArguments.ToArray();
        return args[Array.IndexOf(args, "-filter_complex") + 1];
    }

    // ---------------------------------------------------------------- pipe, không phải ffplay trực tiếp

    [Fact]
    public void Phai_ghep_o_PIPE_chu_khong_phai_goi_thang_ffplay()
    {
        var command = Build();
        var args = command.FfmpegArguments.ToArray();

        // ffmpeg ghi ra stdout...
        Assert.Equal("-", args[^1]);

        // ...và ffplay đọc từ stdin.
        Assert.Contains("pipe:0", command.FfplayArguments);

        // ffplay không được có tuỳ chọn nào chỉ của ffmpeg CLI.
        Assert.DoesNotContain("-filter_complex", command.FfplayArguments);
        Assert.DoesNotContain("-f", command.FfplayArguments);
    }

    [Fact]
    public void Moi_tham_so_phai_truoc_dau_hoi_output()
    {
        // Đặt -map sau dấu `-` thì ffmpeg coi là output thứ hai và báo
        // "Unable to choose an output format for 'pipe:1'". Đã gặp lỗi này thật.
        var args = Build().FfmpegArguments.ToArray();
        var output = Array.LastIndexOf(args, "-");

        foreach (var option in new[] { "-map", "-c:v", "-c:a", "-f" })
        {
            var at = Array.IndexOf(args, option);
            Assert.True(at >= 0 && at < output, $"{option} phải nằm trước dấu `-` ở cuối");
        }
    }

    [Fact]
    public void Hai_tep_duoc_truyen_dung_thu_tu_goc_truoc_nen()
    {
        var args = Build().FfmpegArguments.ToArray();
        var left = Array.IndexOf(args, Original);
        var right = Array.IndexOf(args, Compressed);

        Assert.True(left >= 0, "phải truyền bản gốc");
        Assert.True(right > left, "bản gốc phải trước bản nén");
        Assert.Equal(left - 1, Array.IndexOf(args, "-i", 0, left));
    }

    // ---------------------------------------------------------------- biểu đồ lọc

    [Fact]
    public void Dung_hstack_va_bien_loc()
    {
        Assert.Contains("hstack=inputs=2", Graph(Build()));
    }

    [Fact]
    public void Cung_chi_cao_thi_khong_scale()
    {
        // Cùng 1920x1080: ghép thẳng, khỏi tốn thêm một bước scale.
        var graph = Graph(Build());
        Assert.Equal("[0:v][1:v]hstack=inputs=2[v]", graph);
        Assert.DoesNotContain("scale", graph);
    }

    [Fact]
    public void Lech_chieu_cao_phai_scale_ve_chung_moi_ben()
    {
        // Bản nén bị thu nhỏ 1080 -> 720. hstack yêu cầu hai luồng cùng chiều cao.
        var graph = Graph(Build(rightWidth: 1280, rightHeight: 720));

        Assert.Contains($"scale=-2:{SideBySidePlayer.MaxPaneHeight}[a]", graph);
        Assert.Contains($"scale=-2:{SideBySidePlayer.MaxPaneHeight}[b]", graph);
        Assert.EndsWith("hstack=inputs=2[v]", graph);
    }

    [Fact]
    public void Khong_biet_kich_thuoc_thi_van_dung_scale_phong_hao()
    {
        var graph = Graph(Build(leftWidth: null, leftHeight: null, rightWidth: null, rightHeight: null));
        Assert.Contains("scale=", graph);
    }

    [Fact]
    public void Bieu_do_cuoi_phai_co_nhan_v()
    {
        // -map "[v]" tham chiếu nhãn này. Thiếu nhãn thì ffmpeg báo "No such filter: 'v'".
        var args = Build().FfmpegArguments.ToArray();
        var mapped = args[Array.IndexOf(args, "-map") + 1];

        Assert.Equal("[v]", mapped);
    }

    // ---------------------------------------------------------------- cửa sổ

    [Fact]
    public void Tu_dong_thoat_de_khong_mot_cua_so_day_bia_cai_troi()
    {
        Assert.Contains("-autoexit", Build().FfplayArguments);
    }

    [Fact]
    public void Khung_cua_so_rong_gap_khoang_hai_lan_ben_phai()
    {
        var args = Build().FfplayArguments.ToArray();
        var x = int.Parse(args[Array.IndexOf(args, "-x") + 1], CultureInfo.InvariantCulture);
        var y = int.Parse(args[Array.IndexOf(args, "-y") + 1], CultureInfo.InvariantCulture);

        Assert.Equal(540, y);
        Assert.Equal(1920, x);
        Assert.True(x > y, "cửa sổ phải rộng hơn cao vì có hai bên cạnh nhau");
    }

    [Fact]
    public void Chieu_cao_khong_bao_phinh_qua_man_hinh()
    {
        var args = Build().FfplayArguments.ToArray();
        var y = int.Parse(args[Array.IndexOf(args, "-y") + 1], CultureInfo.InvariantCulture);

        Assert.True(y <= SideBySidePlayer.MaxPaneHeight);
    }

    [Fact]
    public void Tieu_de_cua_sinh_keo_theo_ten_tep()
    {
        var command = SideBySidePlayer.Build(
            @"C:\tools\ffmpeg.exe", @"C:\tools\ffplay.exe", Original, Compressed,
            1920, 1080, 1920, 1080, "UltraCompressor — phim.mp4 (gốc | đã nén)");

        var args = command.FfplayArguments.ToArray();
        Assert.Equal("UltraCompressor — phim.mp4 (gốc | đã nén)", args[Array.IndexOf(args, "-window_title") + 1]);
    }

    // ---------------------------------------------------------------- hiển thị lệnh

    [Fact]
    public void Hien_thi_lenh_giu_nguyen_dau_ngoac_vuong()
    {
        // Dấu ngoặc vuông phải còn nguyên trong lệnh ghi log, nếu không lệnh đó chạy
        // không được và người đọc log tưởng mình đang nhìn một lệnh sai.
        var display = Build().Display();

        Assert.Contains("[0:v][1:v]hstack=inputs=2[v]", display);
        Assert.Contains("|", display);
    }

    [Fact]
    public void Duong_dan_co_khoang_trang_duoc_boc_dau_ngoac()
    {
        var command = SideBySidePlayer.Build(
            @"C:\Program Files\ffmpeg.exe", @"C:\Program Files\ffplay.exe",
            Original, Compressed, 1920, 1080, 1920, 1080);

        Assert.Contains("\"C:\\Program Files\\ffmpeg.exe\"", command.Display());
        Assert.Contains("\"C:\\Program Files\\ffplay.exe\"", command.Display());
    }
}
