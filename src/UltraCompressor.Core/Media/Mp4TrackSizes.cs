using System.Text;

namespace UltraCompressor.Core.Media;

/// <summary>Kích thước media thật trong một tệp MP4, đọc từ box chứ không ước lượng.</summary>
/// <param name="VideoBytes">Tổng byte mẫu của mọi track video.</param>
/// <param name="AudioBytes">Tổng byte mẫu của mọi track audio.</param>
/// <param name="ContainerBytes">
/// Phần còn lại của tệp: <c>FileSize − (video + audio)</c>. Đây chính là định nghĩa
/// container overhead, không phải một hằng số đoán.
/// </param>
public sealed record Mp4TrackMediaSizes(long VideoBytes, long AudioBytes, long ContainerBytes);

/// <summary>
/// Đọc kích thước từng track trong MP4 để đo container overhead thật.
///
/// <para>Vì sao không dùng ffprobe: bản ffmpeg đi kèm không có ffprobe. Vì sao không parse
/// stderr của ffmpeg: giòn — một bản dựng đổi câu chữ là sai số. Box <c>stsz</c> cho byte
/// mẫu thật của từng track, nên <c>FileSize − (video + audio)</c> là overhead thật.</para>
///
/// <para>Phạm vi cố ý hẹp: MP4 chuẩn mà chính ffmpeg của ta sinh ra (box lồng nhau
/// moov→trak→mdia→minf→stbl→stsz, size 32/64 bit). Tệp lạ, box hỏng, track thiếu
/// <c>stsz</c> → trả <c>false</c> kèm lý do, không đoán. Đoán 0 cho track không đọc được
/// là nói dối có chủ đích vì nó làm overhead phình lên đúng bằng phần thiếu.</para>
/// </summary>
public static class Mp4TrackSizes
{
    public static bool TryRead(string path, out Mp4TrackMediaSizes sizes, out string? failure)
    {
        sizes = new Mp4TrackMediaSizes(0, 0, 0);
        failure = null;

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = $"không đọc được tệp: {ex.Message}";
            return false;
        }

        var reader = new Reader(bytes);
        if (!reader.FindTopLevelMoov(out var moov))
        {
            failure = "không tìm thấy box moov";
            return false;
        }

        var video = 0L;
        var audio = 0L;

        var traks = moov.Children("trak");
        if (traks is null)
        {
            failure = "box con trong moov bị hỏng";
            return false;
        }

        foreach (var trak in traks)
        {
            // Mỗi tầng đều kiểm hỏng riêng: bỏ qua box lạ thì được, nhưng box hỏng thì
            // vị trí các box sau không còn đáng tin — phải báo hỏng.
            var mdias = trak.Children("mdia");
            if (mdias is null)
            {
                failure = "box con trong trak bị hỏng";
                return false;
            }

            var mdia = mdias.Count > 0 ? mdias[0] : null;
            if (mdia is null)
            {
                continue;
            }

            var hdlrs = mdia.Children("hdlr");
            var minfs = mdia.Children("minf");
            if (hdlrs is null || minfs is null)
            {
                failure = "box con trong mdia bị hỏng";
                return false;
            }

            var handler = hdlrs.Count > 0 ? hdlrs[0].Handler() : null;
            if (handler is not ("vide" or "soun"))
            {
                continue;
            }

            var stbls = minfs.Count > 0 ? minfs[0].Children("stbl") : null;
            if (stbls is null)
            {
                failure = "box con trong minf bị hỏng";
                return false;
            }

            var stszs = stbls.Count > 0 ? stbls[0].Children("stsz") : null;
            if (stszs is null)
            {
                failure = "box con trong stbl bị hỏng";
                return false;
            }

            var samples = stszs.Count > 0 ? stszs[0].SampleBytes() : null;
            if (samples is null)
            {
                failure = $"track {handler} thiếu bảng mẫu (stsz) nên không biết nó nặng bao nhiêu";
                return false;
            }

            if (handler is not ("vide" or "soun"))
            {
                continue;
            }

            if (samples is null)
            {
                failure = $"track {handler} thiếu bảng mẫu (stsz) nên không biết nó nặng bao nhiêu";
                return false;
            }

            if (handler == "vide")
            {
                video += samples.Value;
            }
            else
            {
                audio += samples.Value;
            }
        }

        var container = bytes.Length - video - audio;
        if (container < 0)
        {
            failure = "tổng mẫu track vượt quá kích thước tệp — box hỏng";
            return false;
        }

        sizes = new Mp4TrackMediaSizes(video, audio, container);
        return true;
    }

    /// <summary>Con trỏ đọc box có kiểm biên. Mọi lần đọc quá biên đều thành lỗi hiền.</summary>
    private sealed class Reader(byte[] bytes)
    {
        public bool FindTopLevelMoov(out Box moov)
        {
            moov = null!;
            var position = 0;
            while (position < bytes.Length)
            {
                var box = ReadBox(position, bytes.Length);
                if (box is null)
                {
                    return false;
                }

                if (box.Type == "moov")
                {
                    moov = box;
                    return true;
                }

                position = box.End;
            }

            return false;
        }

        public Box? ReadBox(int position, int parentEnd)
        {
            if (position < 0 || position + 8 > parentEnd || position + 8 > bytes.Length)
            {
                return null;
            }

            var size = ReadU32(position);
            var type = Encoding.ASCII.GetString(bytes, position + 4, 4);
            var header = 8;
            long end;

            if (size == 1)
            {
                if (position + 16 > parentEnd || position + 16 > bytes.Length)
                {
                    return null;
                }

                var large = ReadU64(position + 8);
                if (large < 16)
                {
                    return null;
                }

                header = 16;
                end = position + (long)large;
            }
            else if (size == 0)
            {
                end = parentEnd;
            }
            else
            {
                if (size < 8)
                {
                    return null;
                }

                end = position + size;
            }

            // Box rỗng (size == 8, chỉ header như `free`) là HỢP LỆ — ffmpeg ghi chúng để
            // căn lề. Điều kiện ở đây chỉ bắt box lố biên, không bắt box rỗng: lần đầu viết
            // `<=` đã loại nhầm box rỗng và làm cả tệp remux "mất moov".
            if (end > parentEnd || end > bytes.Length || end < position + header)
            {
                return null;
            }

            return new Box(type, position + header, (int)(end - position - header), this);
        }

        public uint ReadU32(int position) =>
            ((uint)bytes[position] << 24)
            | ((uint)bytes[position + 1] << 16)
            | ((uint)bytes[position + 2] << 8)
            | bytes[position + 3];

        public ulong ReadU64(int position) =>
            ((ulong)ReadU32(position) << 32) | ReadU32(position + 4);

        public string ReadAscii(int position, int length) =>
            Encoding.ASCII.GetString(bytes, position, length);
    }

    private sealed class Box(string type, int payload, int payloadLength, Reader reader)
    {
        public string Type { get; } = type;

        public int End { get; } = payload + payloadLength;

        /// <summary>
        /// Con trực tiếp cùng loại. Trả null khi gặp box hỏng giữa chừng — bỏ qua nó thì
        /// các box sau bị đếm sai vị trí, nên thà báo hỏng còn hơn trả số liều.
        /// </summary>
        public List<Box>? Children(string type)
        {
            var found = new List<Box>();
            var position = payload;
            while (position < End)
            {
                var child = reader.ReadBox(position, End);
                if (child is null)
                {
                    return null;
                }

                if (child.Type == type)
                {
                    found.Add(child);
                }

                position = child.End;
            }

            return found;
        }

        public string? Handler()
        {
            // hdlr là fullbox: version(1) + flags(3) + pre_defined(4), rồi handler(4).
            if (payloadLength < 12)
            {
                return null;
            }

            return reader.ReadAscii(payload + 8, 4);
        }

        public long? SampleBytes()
        {
            // stsz là fullbox: version(1) + flags(3) + sample_size(4) + count(4).
            if (payloadLength < 12)
            {
                return null;
            }

            var sampleSize = reader.ReadU32(payload + 4);
            var count = reader.ReadU32(payload + 8);

            if (sampleSize != 0)
            {
                return (long)sampleSize * count;
            }

            if (payloadLength < 12 + count * 4L)
            {
                return null;
            }

            var total = 0L;
            for (var i = 0; i < count; i++)
            {
                total += reader.ReadU32(payload + 12 + (int)i * 4);
            }

            return total;
        }
    }
}
