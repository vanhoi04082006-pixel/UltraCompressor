using UltraCompressor.Core.Models;

namespace UltraCompressor.Core.Tests;

/// <summary>
/// Container đầu ra phải do <b>lệnh encode</b> quyết định, không phải bởi phần mở rộng
/// của tệp nguồn.
///
/// <para>Lỗi này đã đo trực tiếp trên ffmpeg thật: cùng một lệnh encode, chỉ khác phần mở
/// rộng đầu ra, thì <c>.mkv</c> ra Matroska và <c>.ts</c> ra MPEG-TS, còn
/// <c>-movflags +faststart</c> bị nuốt lặng lẽ. Nguồn <c>.webm</c> có tiếng còn hỏng hẳn vì
/// lệnh dùng <c>-c:a aac</c> mà WebM không nhận.</para>
///
/// <para>Bảng dưới đây chính là hồ sơ của lỗi đó, ở dạng kiểm thử: nếu ai đó cho phần mở
/// rộng nguồn quyết định container đầu ra trở lại, các test này sẽ đỏ.</para>
/// </remarks>
public class OutputContainerTests
{
    [Theory]
    // Nguồn MP4: container đầu ra vẫn phải là MP4.
    [InlineData("clip.mp4")]
    // Matroska: trước đây cho ra Matroska và mất faststart.
    [InlineData("clip.mkv")]
    // MPEG-TS: trước đây cho ra MPEG-TS, mất faststart, và là container tệ nhất cho web.
    [InlineData("clip.ts")]
    [InlineData("clip.m2ts")]
    // WebM: trước đây hỏng hẳn vì AAC không hợp lệ trong WebM.
    [InlineData("clip.webm")]
    // Các container còn lại mà bộ phân loại vẫn chấp nhận.
    [InlineData("clip.avi")]
    [InlineData("clip.flv")]
    [InlineData("clip.wmv")]
    [InlineData("clip.mpg")]
    [InlineData("clip.m4v")]
    [InlineData("clip.3gp")]
    [InlineData("clip.vob")]
    // Hoa/thường trộn lẫn — phải cho cùng kết quả, không phụ thuộc hệ điều hành.
    [InlineData("CLIP.MKV")]
    [InlineData("Clip.Ts")]
    [InlineData("clip.MP4")]
    // Không có phần mở rộng: vẫn phải ra MP4, tuyệt đối không phải chuỗi rỗng.
    [InlineData("clip")]
    // Thư mục giống tệp.
    [InlineData("a.b.mp4")]
    public void Video_luon_ra_mp4_bat_ke_nguon_la_gi(string source) =>
        Assert.Equal(".mp4", OutputContainer.ExtensionFor(MediaKind.Video, source));

    [Theory]
    // Không có phần mở rộng thì không có phần mở rộng đầu ra — nhưng đây là lỗi cấu hình,
    // nên phải nói ra chứ không âm thầm tạo tệp không đuôi.
    [InlineData("clip", "")]
    [InlineData("clip.mp4", ".mp4")]
    [InlineData("CLIP.MP4", ".mp4")]
    [InlineData("clip.MKV", ".mkv")]
    [InlineData("a.b.ts", ".ts")]
    public void Loai_khong_co_movflags_thi_bam_theo_nguon(string source, string expected) =>
        Assert.Equal(expected, OutputContainer.ExtensionFor(MediaKind.Audio, source));

    [Theory]
    [InlineData(MediaKind.Image, "photo.png", ".png")]
    [InlineData(MediaKind.Image, "photo.jpg", ".jpg")]
    [InlineData(MediaKind.Gif, "anim.gif", ".gif")]
    [InlineData(MediaKind.Pdf, "doc.pdf", ".pdf")]
    public void Cac_loai_khac_bam_theo_container_nguon(
        MediaKind kind, string source, string expected) =>
        // Gifsicle cần .gif để chạy, Ghostscript cần .pdf: ép container ở đây sẽ phá công cụ.
        Assert.Equal(expected, OutputContainer.ExtensionFor(kind, source));

    [Fact]
    public void Container_dau_ra_khong_bao_gio_la_container_nguon_voi_video()
    {
        // Bất biến dạng thuộc tính: mọi phần mở rộng video mà app chấp nhận đều phải ra MP4.
        // Riêng nguồn `.mp4` thì container đầu ra trùng container nguồn — đó là hợp đồng, không
        // phải kế thừa mù quáng.
        foreach (var ext in new[]
        {
            ".mkv", ".mov", ".avi", ".webm", ".flv", ".wmv", ".m4v",
            ".mpg", ".mpeg", ".ts", ".m2ts", ".3gp", ".vob",
        })
        {
            var output = OutputContainer.ExtensionFor(MediaKind.Video, $"nguon{ext}");

            Assert.Equal(OutputContainer.Mp4, output);
            Assert.NotEqual(ext, output);
        }
    }

    [Fact]
    public void Container_va_tai_lieu_dung_mp4()
    {
        // Lệnh encode mang `-movflags +faststart`, nên container đầu ra bắt buộc là MP4.
        // Đây là lý do, không phải lựa chọn thẩm mỹ.
        Assert.Equal(".mp4", OutputContainer.Mp4);
        Assert.Equal(".jpg", OutputContainer.Jpeg);
    }

    [Fact]
    public void Khong_duoc_dung_phan_mo_rong_nguon_lam_container_dau_ra()
    {
        // Hàm chỉ nhận phần mở rộng có dấu chấm. Nhận chuỗi rỗng là lỗi chọn container ở chỗ
        // gọi, và nếu im lặng chấp nhận thì lỗi đó chỉ lộ ra ở tệp đầu ra hỏng.
        using var workspace = new Storage.TempWorkspace();

        Assert.Throws<ArgumentException>(() => workspace.CreatePath(string.Empty));
        Assert.Throws<ArgumentException>(() => workspace.CreatePath("mp4"));
        Assert.Throws<ArgumentException>(() => workspace.CreatePath("."));

        // Có dấu chấm và chữ thì chấp nhận.
        var ok = workspace.CreatePath(OutputContainer.Mp4);
        Assert.Equal(".mp4", Path.GetExtension(ok));
    }

    [Fact]
    public void Duong_dan_tam_giu_phan_mo_rong_dau_ra_chu_khong_phai_nguon()
    {
        using var workspace = new Storage.TempWorkspace();

        var fromMkv = workspace.CreatePath(OutputContainer.ExtensionFor(MediaKind.Video, "nguon.mkv"));
        var fromTs = workspace.CreatePath(OutputContainer.ExtensionFor(MediaKind.Video, "nguon.ts"));

        // Cùng container đầu ra dù nguồn khác nhau: ffmpeg chọn muxer theo tệp đầu ra.
        Assert.Equal(".mp4", Path.GetExtension(fromMkv));
        Assert.Equal(".mp4", Path.GetExtension(fromTs));
    }

    [Fact]
    public void Suffix_vao_truoc_phan_mo_rong()
    {
        using var workspace = new Storage.TempWorkspace();

        // GifPipeline tạo tệp trung gian bằng cách chèn hậu tố; hậu tố phải nằm TRƯỚC đuôi,
        // nếu không ffmpeg/gifsicle không đoán được định dạng.
        var path = workspace.CreatePath(OutputContainer.Mp4, ".stage1");

        Assert.Equal(".mp4", Path.GetExtension(path));

        // Và hậu tố nằm TRƯỚC đuôi, để gifsicle/ffmpeg vẫn đoán được định dạng.
        Assert.EndsWith(".stage1.mp4", path, StringComparison.Ordinal);
    }

    [Theory]
    // Đuôi đã khớp thì không đụng tới — tuyệt đối đa số.
    [InlineData("clip.mp4", "clip.mp4", MediaKind.Video, "clip.mp4")]
    [InlineData("clip.MP4", "clip.MP4", MediaKind.Video, "clip.MP4")]
    // Đuôi nguồn khác container thì phải đổi, kể cả khi người dùng đã đặt tên đích khác.
    [InlineData("clip.ts", "clip.ts", MediaKind.Video, "clip.mp4")]
    [InlineData("clip.mkv", "clip.mkv", MediaKind.Video, "clip.mp4")]
    [InlineData("clip.webm", "clip.webm", MediaKind.Video, "clip.mp4")]
    [InlineData("clip.ts", "renamed.mov", MediaKind.Video, "renamed.mp4")]
    // Không có đuôi thì phải ra tên CÓ đuôi, không phải chuỗi rỗng.
    [InlineData("clip", "clip", MediaKind.Video, "clip.mp4")]
    // Loại khác video bám theo nguồn, nên tên đích không bị đổi.
    [InlineData("song.mp3", "song.mp3", MediaKind.Audio, "song.mp3")]
    [InlineData("song.wav", "out.wav", MediaKind.Audio, "out.wav")]
    public void Duong_dan_dich_phai_khop_container_that(
        string source, string destination, MediaKind kind, string expected) =>
        Assert.Equal(
            expected,
            Path.GetFileName(OutputContainer.ApplyContract(destination, source, kind, _ => false)));

    [Fact]
    public void Khong_duoc_de_gi_lai_ten_da_co()
    {
        // Thư mục có sẵn cả `clip.ts` và `clip.mp4`. Ghi vào `clip.mp4` sẽ xoá mất tệp của
        // người dùng, nên phải dồn sang tên có số thứ tự.
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "clip.mp4", "clip (2).mp4" };

        var result = OutputContainer.ApplyContract("clip.ts", "clip.ts", MediaKind.Video, taken.Contains);

        Assert.Equal("clip (3).mp4", Path.GetFileName(result));
    }

    [Fact]
    public void Khong_ghi_de_nguon_khi_phai_doi_duoi()
    {
        var work = Directory.CreateTempSubdirectory("uc-contract-").FullName;

        try
        {
            var source = Path.Combine(work, "clip.ts");
            File.WriteAllText(source, "ban goc");

            var result = OutputContainer.ApplyContract(source, source, MediaKind.Video);

            // Đuôi đích khác đuôi nguồn nên không thể là chính tệp nguồn — nếu bằng nhau thì
            // lệnh encode sẽ ghi đè mất bản gốc.
            Assert.NotEqual(source, result);
            Assert.Equal(".mp4", Path.GetExtension(result));
            Assert.False(File.Exists(result));
            Assert.Equal("ban goc", File.ReadAllText(source));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }
}
