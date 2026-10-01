using UltraCompressor.Core.Media;
using UltraCompressor.Core.Planning;

namespace UltraCompressor.Core.Search;

/// <summary>
/// Seam cho việc encode một ứng viên trên các đoạn đại diện.
///
/// <para>Tách ra để <see cref="PilotSearch"/> kiểm thử được trên máy không có ffmpeg. Lý do
/// cụ thể: nếu orchestration chỉ kiểm thử được khi có ffmpeg, thì trên CI — nơi không cài
/// ffmpeg — toàn bộ logic về thứ tự đo, loại sớm, chọn ứng viên và fallback sẽ <b>không
/// được kiểm tra lần nào</b>. Test bị bỏ qua thì tệ hơn test đỏ, vì nó tạo cảm giác an toàn
/// giả.</para>
///
/// <para>Cài đặt thật là <see cref="PilotEncoder"/>.</para>
/// </summary>
public interface IPilotEncodeRunner
{
    Task<IReadOnlyList<PilotArtifact>> EncodeAsync(
        VideoEncodeCandidate candidate,
        string sourcePath,
        int sourceWidth,
        int sourceHeight,
        IReadOnlyList<RepresentativeWindow> windows,
        CancellationToken token);

    /// <summary>Xoá clip thử nghiệm đã đo xong.</summary>
    void Release(IReadOnlyList<PilotArtifact> artifacts);
}

/// <summary>
/// Seam đo chất lượng dùng cho giai đoạn tìm kiếm là <see cref="IQualityMeasure"/>, đã có
/// sẵn trong <c>UltraCompressor.Core.Media</c> và do <see cref="QualityProbe"/> hiện thực.
///
/// <para>Không tạo seam thứ hai. Bản rút gọn từng được cân nhắc — chỉ cần clip tham chiếu,
/// clip ứng viên và mô hình — nhưng nó làm mất <c>candidateWidth</c> và
/// <c>candidateHeight</c>, mà <c>QualityProbe</c> dùng để quyết định có đưa ứng viên nhỏ
/// hơn về đúng khổ hiển thị hay không. Seam thiếu thông tin thì cài đặt thật buộc phải đoán,
/// và phép đo đoán sai thì ra con số sai một cách rất khó nhận ra.</para>
///
/// <para>Trả <c>null</c> nghĩa là <b>không đo được</b> — khác hẳn với điểm 0, và
/// <see cref="QualityAggregator"/> sẽ coi là không khả thi.</para>
///
/// <para>Còn một điều mà seam này chưa phủ, và là việc phải làm ở giai đoạn kế: chuẩn hoá
/// <c>PTS</c> và timebase trước <c>libvmaf</c>. Hiện việc neo thời gian đảm bảo bằng cách cho
/// cả hai bên cùng bắt đầu từ khung hình đầu, nhưng nếu nguồn có timestamp khác nhau thì
/// <c>libvmaf</c> vẫn có thể ghép cặp khung hình lệch.</para>
