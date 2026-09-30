namespace UltraCompressor.Core.Models;

/// <summary>Cấu hình ứng dụng, lưu ở <c>config.json</c> cạnh tệp thực thi.</summary>
public sealed class AppConfig
{
    public CompressionLevel Level { get; set; } = CompressionLevel.Balanced;

    /// <summary>
    /// Mặc định chạy thử: nén ra thư mục tạm, xem kết quả rồi mới ghi đè. Đây là hành vi mặc
    /// định an toàn nhất — người dùng luôn xem được kết quả trước khi mất bản gốc.
    /// </summary>
    public bool DryRunDefault { get; set; } = true;

    /// <summary>Số nén song song. 0 = tự động theo số nhân/cpu và RAM.</summary>
    public int MaxConcurrent { get; set; }

    public ToolPaths Tools { get; set; } = new();

    /// <summary>
    /// Phần trăm tiết kiệm tối thiểu để chấp nhận kết quả. Độc lập với mức nén —
    /// sửa bug B3 của bản gốc (bản gốc dùng ngưỡng tăng dần nên mức Mạnh lại khó đạt nhất).
    /// </summary>
    public double MinSavingPercent { get; set; } = 1.0;

    /// <summary>
    /// Có đo chất lượng sau khi nén không.
    ///
    /// <para>Bật mặc định vì đây là lưới chặn cuối: tắt đi thì ứng viên chỉ còn được cân
    /// bằng kích thước, và một ứng viên nhỏ hơn 5% nhưng hỏng rõ sẽ đi qua. Nếu ffmpeg
    /// thiếu <c>libvmaf</c> thì việc đo trả về null và mọi tệp vẫn nén bình thường — tắt
    /// mục này không làm nhanh hơn, chỉ mất lớp bảo vệ.</para>
    /// </summary>
    public bool QualityCheckEnabled { get; set; } = true;

    /// <summary>
    /// Độ dài đoạn dùng để đo chất lượng (giây). Chỉ một đoạn ngắn, không phải cả tệp.
    ///
    /// <para>Không có số liệu đo để biết đo bao nhiêu là đủ, nên 3 giây là chỗ dừng
    /// tạm: đủ nhiều khung để VMAF ổn định (một lượt 3 giây ở 24 fps cho khoảng 72
    /// khung) mà vẫn rẻ. Giai đoạn bộ chọn đoạn đại diện sẽ thay con số này bằng
    /// chính sách riêng, có dữ liệu đi kèm.</para>
    /// </summary>
    public double QualityCheckWindowSeconds { get; set; } = 3.0;

    /// <summary>Bỏ qua tệp nhỏ hơn mức này. 0 = không bỏ qua.</summary>
    public long MinFileSizeBytes { get; set; }

    public bool IncludeSubfolders { get; set; } = true;

    /// <summary>Danh sách mẫu tên tệp cần bỏ qua, không phân biệt hoa thường. Ví dụ: <c>*.bak</c>, <c>Thumbs.db</c>.</summary>
    public List<string> ExcludePatterns { get; set; } = ["*.bak", "*.tmp", "Thumbs.db", ".DS_Store", "*~"];

    // KHONG con truong "so ngay giu tep .bak" nua. Truoc day mac dinh la 30 ngay, nen
    // nhanh "Duyet" chi bao duyet chu khong xoa gi - dung nguon y cua nguoi dung: duyet
    // xong la xoa han goc de giai phong dung luong, con "Hoan tac" dung khi chua duyet.
    // Tieu chuc giu lai da duoc bo khoi Cai dat; muc 30 ngay cho phep don tep .bak roi
    // vai nam dinh ngay trong AppHost, khong phai thong so cau hinh.

    /// <summary>Chạy đo chất lượng VMAF trên vài frame mẫu trước khi áp dụng kết quả.</summary>
    public bool MeasureQuality { get; set; }

    /// <summary>Cảnh báo nếu ổ đĩa không đủ chỗ cho bước ghi tạm.</summary>
    public bool CheckFreeSpace { get; set; } = true;

    /// <summary>Hệ số nhân số luồng nén so với số nhân logic.</summary>
    public double ConcurrencyScale { get; set; } = 0.5;

    /// <summary>Chỉ báo mức log ghi ra tệp: Error, Warning, Info, Debug.</summary>
    public string LogLevel { get; set; } = "Info";

    /// <summary>Chủ đề giao diện: <c>system</c>, <c>light</c>, <c>dark</c>.</summary>
    public string Theme { get; set; } = "system";

    /// <summary>
    /// Codec video đầu ra: <c>hevc</c> (mặc định) hoặc <c>h264</c>.
    ///
    /// <para><b>Vì sao mặc định là HEVC.</b> Đo trên 6 tệp ngẫu nhiên trong chính thư viện
    /// này (20 giây mỗi tệp, CRF tương đương chất lượng):</para>
    ///
    /// <list type="bullet">
    /// <item>Tệp nhỏ hơn 1,3–2,6 lần. Không tệp nào HEVC thua.</item>
    /// <item>Chênh lệch SSIM ≤ 0,0002 — dưới ngưỡng nhìn thấy được, tức cùng chất
    /// lượng thật, không phải hy sinh chất lượng để lấy dung lượng.</item>
    /// <item>Đổi lại: chậm hơn 2,7–10 lần, tùy độ phân giải. Tệp 1440p50 chậm nhất.</item>
    /// </list>
    ///
    /// <para>Ngoại lệ đã biết: video quay màn hình thì HEVC không thu được byte nào mà
    /// vẫn mất thời gian. Người dùng nén màn hình nên chọn H.264.</para>
    /// </summary>
    public string VideoCodec { get; set; } = "hevc";
}
