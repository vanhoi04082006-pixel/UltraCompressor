using UltraCompressor.Core.Media;
using Xunit;

namespace UltraCompressor.Core.Tests;

/// <summary>
/// Parser kích thước track MP4, dùng để đo container overhead thật:
/// <c>overhead = FileSize − (video + audio)</c>.
///
/// <para>Không dùng ffprobe (bản ffmpeg đi kèm không có), không parse stderr (giòn).
/// Đọc trực tiếp box <c>stsz</c> của từng track nên con số là byte thật, không phải ước
/// lượng. Chỉ hỗ trợ MP4 chuẩn mà chính ffmpeg của ta sinh ra — tệp lạ thì trả false chứ
/// không đoán.</para>
/// </summary>
public class Mp4TrackSizesTests
{
    // ---------------------------------------------------------------- dựng box giả

    private static void U32(List<byte> into, uint value)
    {
        into.Add((byte)(value >> 24));
        into.Add((byte)(value >> 16));
        into.Add((byte)(value >> 8));
        into.Add((byte)value);
    }

    private static void U64(List<byte> into, ulong value)
    {
        for (var shift = 56; shift >= 0; shift -= 8)
        {
            into.Add((byte)(value >> shift));
        }
    }

    private static byte[] Box(string type, byte[] payload)
    {
        var box = new List<byte>();
        U32(box, (uint)(8 + payload.Length));
        box.AddRange(System.Text.Encoding.ASCII.GetBytes(type));
        box.AddRange(payload);
        return [.. box];
    }

    private static byte[] FullBox(string type, byte[] payload)
    {
        // version(1) + flags(3) rồi mới tới nội dung.
        var full = new List<byte> { 0, 0, 0, 0 };
        full.AddRange(payload);
        return Box(type, [.. full]);
    }

    private static byte[] Stsz(uint[] entries)
    {
        var payload = new List<byte>();
        U32(payload, 0); // sample_size = 0 → có bảng từng mẫu
        U32(payload, (uint)entries.Length);
        foreach (var entry in entries)
        {
            U32(payload, entry);
        }

        return FullBox("stsz", [.. payload]);
    }

    private static byte[] Hdlr(string handler)
    {
        var payload = new List<byte> { 0, 0, 0, 0 }; // pre_defined
        payload.AddRange(System.Text.Encoding.ASCII.GetBytes(handler));
        payload.AddRange(new byte[12]); // reserved
        payload.Add(0); // tên rỗng + null
        return FullBox("hdlr", [.. payload]);
    }

    private static byte[] Trak(string handler, uint[] entries) =>
        Box("trak", Concat(
            Box("mdia", Concat(
                Hdlr(handler),
                Box("minf", Box("stbl", Stsz(entries)))))));

    private static byte[] Concat(params byte[][] parts)
    {
        var all = new List<byte>();
        foreach (var part in parts)
        {
            all.AddRange(part);
        }

        return [.. all];
    }

    private static string WriteTemp(byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"uc-mp4-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(path, content);
        return path;
    }

    // ---------------------------------------------------------------- hành vi

    [Fact]
    public void Doc_duoc_video_audio_va_container_theo_cong_thuc()
    {
        // video: 1000 + 2000 = 3000; audio: 500; mdat payload 3500.
        // overhead = file − 3500 = ftyp + moov + header mdat.
        var mdatPayload = new byte[3500];
        var content = Concat(
            Box("ftyp", new byte[16]),
            Box("moov", Concat(
                Trak("vide", [1000, 2000]),
                Trak("soun", [500]))),
            Box("mdat", mdatPayload));

        var path = WriteTemp(content);
        try
        {
            Assert.True(Mp4TrackSizes.TryRead(path, out var sizes, out var failure), failure);

            Assert.Equal(3000, sizes.VideoBytes);
            Assert.Equal(500, sizes.AudioBytes);
            Assert.Equal(content.Length - 3500, sizes.ContainerBytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Mdat_largesize_64bit_van_dung()
    {
        // size == 1 nghĩa là 8 byte tiếp theo là largesize. Parser cộng nhầm 8 byte đó
        // vào payload thì overhead sai đúng 8 byte — nhỏ nhưng sai có hệ thống.
        var payload = new byte[100];
        var box = new List<byte>();
        U32(box, 1);
        box.AddRange(System.Text.Encoding.ASCII.GetBytes("mdat"));
        U64(box, (ulong)(16 + payload.Length));
        box.AddRange(payload);

        var content = Concat(
            Box("ftyp", new byte[16]),
            Box("moov", Trak("vide", [100])),
            [.. box]);

        var path = WriteTemp(content);
        try
        {
            Assert.True(Mp4TrackSizes.TryRead(path, out var sizes, out var failure), failure);

            Assert.Equal(100, sizes.VideoBytes);
            Assert.Equal(0, sizes.AudioBytes);
            Assert.Equal(content.Length - 100, sizes.ContainerBytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Box_rong_khong_lam_mat_moov()
    {
        // Hồi quy lỗi thật: ffmpeg ghi box `free` size 8 (chỉ header, không payload) để
        // căn lề, và parser cũ loại nhầm nó khiến cả tệp remux "không có moov". Box rỗng
        // là hợp lệ — chỉ box lố biên mới là hỏng.
        var content = Concat(
            Box("ftyp", new byte[16]),
            Box("free", []),
            Box("moov", Trak("vide", [100])),
            Box("mdat", new byte[100]));

        var path = WriteTemp(content);
        try
        {
            Assert.True(Mp4TrackSizes.TryRead(path, out var sizes, out var failure), failure);

            Assert.Equal(100, sizes.VideoBytes);
            Assert.Equal(content.Length - 100, sizes.ContainerBytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Track_thieu_stsz_thi_tra_false_chu_khong_doan_khong()
    {
        // Track video không có bảng mẫu thì không biết nó nặng bao nhiêu. Đoán 0 là nói
        // dối có chủ đích; trả false để người gọi biết mà không dùng số này.
        var content = Concat(
            Box("ftyp", new byte[16]),
            Box("moov", Box("trak", Box("mdia", Hdlr("vide")))),
            Box("mdat", new byte[10]));

        var path = WriteTemp(content);
        try
        {
            Assert.False(Mp4TrackSizes.TryRead(path, out _, out var failure));
            Assert.False(string.IsNullOrWhiteSpace(failure));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Tep_cat_doi_tra_false_chu_khong_nem()
    {
        var content = Concat(
            Box("ftyp", new byte[16]),
            Box("moov", Trak("vide", [1000, 2000])));
        var truncated = content[..^10];

        var path = WriteTemp(truncated);
        try
        {
            Assert.False(Mp4TrackSizes.TryRead(path, out _, out var failure));
            Assert.False(string.IsNullOrWhiteSpace(failure));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Khong_phai_mp4_tra_false()
    {
        var path = WriteTemp("day khong phai la mp4"u8.ToArray());
        try
        {
            Assert.False(Mp4TrackSizes.TryRead(path, out _, out var failure));
            Assert.False(string.IsNullOrWhiteSpace(failure));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Tep_rong_tra_false()
    {
        var path = WriteTemp([]);
        try
        {
            Assert.False(Mp4TrackSizes.TryRead(path, out _, out _));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
