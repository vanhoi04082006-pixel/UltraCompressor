using UltraCompressor.Core.Models;
using UltraCompressor.Core.Planning;

namespace UltraCompressor.Core.Pipelines;

/// <summary>
/// Nén PDF bằng Ghostscript.
///
/// Sửa so với bản gốc:
///  - kiểm tra exit code và nếu lỗi thì báo rõ thay vì im lặng đánh dấu "Giữ nguyên" (bug B4),
///  - ném <see cref="SkipException"/> khi thiếu gswin64c nên job không bị treo bởi MessageBox
///    trong luồng nền,
///  - thêm <c>-dQUIET</c> để không tràn log.
/// </summary>
public sealed class PdfPipeline : FFmpegPipelineBase
{
    public override MediaKind Kind => MediaKind.Pdf;

    public override async Task<PipelineResult> RunAsync(
        PipelineContext context, Action<int> onProgress, CancellationToken token)
    {
        var gs = context.Tools.Ghostscript
            ?? throw new SkipException(
                SkipReason.MissingTool,
                "Chưa có Ghostscript. Cài tại https://ghostscript.com/releases/ rồi khai báo đường dẫn trong Cài đặt.");

        var plan = CompressionPlanner.PlanPdf(context.Level.ToGoal());
        onProgress(0);

        // Ghostscript không có cơ chế tiến độ dạng máy đọc được mà vẫn giữ được chế độ im
        // lặng, nên báo 0 rồi 100. Không có callback tiến độ: trước đây nó báo 50% giữa
        // chừng — số bịa, và với PDF nhiều trăm trang thì thanh tiến độ đứng yên ở 50% suốt
        // thời gian xử lý, trông y hệt tệp bị kẹt.
        var (result, _) = await ExecuteToolAsync(
            gs,
            [
                "-dQUIET",
                "-dNOPAUSE",
                "-dBATCH",
                "-dSAFER",
                "-sDEVICE=pdfwrite",
                "-dCompatibilityLevel=1.4",
                $"-dPDFSETTINGS={plan.Preset}",
                $"-sOutputFile={context.TempPath}",
                context.SourcePath,
            ],
            onProgress: _ => { },
            token);

        if (result.Cancelled)
        {
            SafeDelete(context.TempPath);
            return PipelineResult.Failed(SkipReason.Cancelled, "Đã hủy.");
        }

        if (!result.Succeeded)
        {
            SafeDelete(context.TempPath);
            var detail = result.StandardErrorText;
            if (detail.Length > 500) detail = detail[^500..];
            return PipelineResult.Failed(
                SkipReason.ProcessFailed,
                $"Ghostscript trả về mã {result.ExitCode}. {detail}".Trim());
        }

        if (!File.Exists(context.TempPath))
        {
            return PipelineResult.Failed(SkipReason.ProcessFailed, "Ghostscript không tạo ra tệp.");
        }

        onProgress(100);
        return PipelineResult.Ok(new FileInfo(context.TempPath).Length);
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }
}
