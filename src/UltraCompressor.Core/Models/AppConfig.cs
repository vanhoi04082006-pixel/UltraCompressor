using UltraCompressor.Core.Planning;

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

    // ---------------------------------------------------------------- chọn đoạn đại diện

    /// <summary>Số khung hình lấy mẫu mỗi giây khi quét đặc tính tệp.</summary>
    public double AnalysisSampleFps { get; set; } = 4.0;

    /// <summary>
    /// Độ dài mỗi mẫu quét (giây). Ngắn để rẻ, đủ dài để lấy được chuyển động.
    /// </summary>
    public double AnalysisSampleSeconds { get; set; } = 2.0;

    /// <summary>
    /// Số mẫu ít nhất và nhiều nhất khi quét. Số mẫu thực tế tăng theo log thời lượng:
    /// tệp ngắn không cần quét nhiều, tệp dài thì một mẫu phải phủ ít thời gian hơn.
    /// </summary>
    public int AnalysisMinSamples { get; set; } = 8;

    public int AnalysisMaxSamples { get; set; } = 24;

    /// <summary>
    /// Số đoạn đại diện mong muốn. Không phải con số cứng của thuật toán: thuật toán
    /// chọn nhiều nhất số này đoạn, và có thể chọn ít hơn khi tệp không có đủ kiểu nội
    /// dung khác nhau.
    /// </summary>
    public int TargetWindowCount { get; set; } = 3;

    /// <summary>Trần cứng, để một cấu hình sai không khiến công cụ encode hàng chục cửa sổ.</summary>
    public int MaxWindowCount { get; set; } = 5;

    /// <summary>Độ dài mỗi đoạn được chọn để đo chất lượng (giây).</summary>
    public double WindowDurationSeconds { get; set; } = 3.0;

    /// <summary>
    /// Hai đoạn cách nhau dưới ngưỡng này được coi là trùng nhau. Không có khoảng cách
    /// tối thiểu thì thuật toán dễ chọn ba cửa sổ nằm trong cùng một cảnh.
    /// </summary>
    public double MinWindowSeparationSeconds { get; set; } = 30.0;

    /// <summary>
    /// Cỡ chuyển động được coi là "khác biệt đáng kể", tính theo độ lệch giữa mẫu cao
    /// nhất và mẫu trung vị sau khi chuẩn hoá. Tệp gần như tĩnh sẽ không có đoạn
    /// HIGH_MOTION nào — đúng, vì tạo ra nó chỉ là bịa.
    /// </summary>
    public double MotionSpreadThreshold { get; set; } = 0.15;

    /// <summary>
    /// Mốc chuyển động <b>tuyệt đối</b> trên thang chênh luma 0–255 của ffmpeg, bên dưới
    /// đây coi như tệp không có chuyển động đáng kể.
    ///
    /// <para>Cần mốc này vì chuẩn hoá tương đối luôn khuếch đại nhiễu thành [0,1]: một
    /// tệp gần như tĩnh mà YDIF dao động 0,18–0,22 sẽ ra "biến thiên toàn phạm vi" và
    /// sinh ra một đoạn HIGH_MOTONG hoàn toàn vô nghĩa. Chuẩn hoá tương đối quyết định
    /// <i>thứ tự</i>; mốc tuyệt đối quyết định <i>vai trò có tồn tại hay không</i>.</para>
    ///
    /// <para>Mốc 1,0 là khoảng 4% dải thực tế đo được trên thư viện người dùng
    /// (0,00 đến 23,54), và bằng khoảng một bậc luma. Yêu cầu mở rộng tập mẫu trước khi
    /// coi con số này là chính thức — xem <c>docs/QUALITY-CALIBRATION.md</c>.</para>
    /// </summary>
    public double MotionAbsoluteFloor { get; set; } = 1.0;

    /// <summary>
    /// Mốc chi tiết tuyệt đối, trên thang [0,1] mà ffmpeg đã quy đổi sẵn cho
    /// <c>normalized_entropy</c>. Dải này đã chuẩn hoá nên không cần hiệu chỉnh thêm.
    /// </summary>
    public double SpatialAbsoluteFloor { get; set; } = 0.05;

    /// <summary>Trọng số cộng dồn ra độ khó tổng thể. Xem <c>RepresentativeWindowSelector</c>.</summary>
    public double ComplexityWeightSpatial { get; set; } = 0.35;

    public double ComplexityWeightMotion { get; set; } = 0.30;

    public double ComplexityWeightScene { get; set; } = 0.15;

    public double ComplexityWeightSharpness { get; set; } = 0.20;

    // ---------------------------------------------------------------- ngân sách tính toán

    /// <summary>
    /// Ngân sách tính toán: được phép dùng bao nhiêu công sức để <i>tìm</i> cách nén tốt.
    /// Tách khỏi mức nén vì "nhanh" và "chất lượng thấp" là hai điều khác nhau.
    /// </summary>
    public ComputeBudget ComputeBudget { get; set; } = ComputeBudget.Normal;

    /// <summary>
    /// Trần số ứng viên được sinh ra. Có trần vì một tập ứng viên vô hạn sẽ biến giai
    /// đoạn tìm kiếm thành thử mọi tổ hợp.
    /// </summary>
    public int MaxInitialCandidates { get; set; } = 24;

    /// <summary>Số nhánh độ phân giải tối đa, tính cả nhánh giữ nguyên nguồn.</summary>
    public int MaxResolutionBranches { get; set; } = 3;

    /// <summary>
    /// Bật tìm kiếm thích ứng theo nội dung: <c>CandidatePlanner</c> sinh tập ứng viên có
    /// cấu trúc, <c>PilotSearch</c> đo chất lượng thật trên các đoạn đại diện, rồi mới
    /// encode toàn tệp ứng viên được chọn.
    ///
    /// <para><b>Tắt là mặc định, và đó là quyết định có ý thức.</b> Đường thích ứng tốn
    /// nhiều encode thử hơn: mỗi ứng viên được thử là một lần encode đoạn cộng một lần
    /// VMAF. Người dùng bật lên là đồng ý trả cái giá đó, nên mặc định phải là đường cũ đã
    /// được kiểm chứng, không phải đường mới.</para>
    ///
    /// <para>Tắt cờ này giữ nguyên hành vi cũ <b>bit nào cũng không đổi</b>: cùng kế hoạch,
    /// cùng lệnh, cùng kết quả.</para>
    ///
    /// <para>Khi bật và tìm kiếm <b>hỏng hạ tầng</b> thì rơi về đường cũ và ghi rõ. Khi
    /// tìm kiếm chạy đúng mà <b>không ứng viên nào đạt</b> thì giữ bản gốc và KHÔNG rơi về
    /// đường cũ — tệp đã nén hiệu quả là chuyện thường, không phải lỗi.</para>
    /// </summary>
    public bool EnableAdaptiveSearch { get; set; }

    /// <summary>
    /// Trần số ứng viên được <b>đo thật</b> trong một lần tìm kiếm. Tách khỏi
    /// <see cref="MaxInitialCandidates"/> vì cái đó giới hạn việc <i>sinh</i> ra bao nhiêu,
    /// còn cái này giới hạn việc <i>đo</i> bao nhiêu — và hai thứ này tốn công khác nhau.
    /// </summary>
    public int MaxSearchEvaluations { get; set; } = 12;

    /// <summary>
    /// Có thử AV1 không. Tắt mặc định: libaom chậm hơn libx265 khoảng một đến hai bậc độ
    /// lũy, nên bật sẽ làm công cụ dừng vô dụng với tệp dài.
    /// </summary>
    public bool EnableAv1Search { get; set; }

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
