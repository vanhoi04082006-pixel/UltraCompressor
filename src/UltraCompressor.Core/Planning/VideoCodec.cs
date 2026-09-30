namespace UltraCompressor.Core.Planning;

/// <summary>
/// Codec video mà bộ lập kế hoạch được phép sinh ứng viên.
///
/// <para>Mỗi codec có một miền tìm kiếm riêng, vì <b>thang CRF không dùng chung</b>:
/// x264 chạy 0–51 mặc định 23, x265 chạy 0–51 mặc định 28, libaom-av1 chạy 0–63. Cùng
/// con số trên hai codec là hai điểm chất lượng khác nhau, nên coi chúng là một thang
/// chung là một preset table trá hình.</para>
/// </summary>
public enum VideoCodec
{
    H264,
    Hevc,
    Av1,
}
